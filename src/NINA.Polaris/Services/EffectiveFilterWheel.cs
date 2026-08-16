// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details. You should have received a copy of the license along with
// this program. If not, see <https://www.gnu.org/licenses/>.

using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>
/// FILTERNAME: makes the operator's saved filter names (on the rig) the source
/// of truth for LABELS, regardless of what the driver can do — the way NINA
/// desktop treats them.
///
/// The ASCOM/Alpaca spec makes a wheel's <c>Names</c> read-only: the driver
/// owns them and the host cannot push new ones. So a custom wheel that only
/// reports "clear1, clear2, …" left the operator with no way to rename filters
/// in Polaris, because renaming was wired to push into the driver (INDI's
/// writable <c>FILTER_NAME</c> vector) and 501'd everywhere else.
///
/// This decorator wraps the real backend and overlays the rig's
/// <c>EquipmentProfile.FilterNames</c> per slot, so every consumer that reads
/// through <c>EquipmentManager.FilterWheel</c> — the status feed, the FITS
/// FILTER keyword, selection-by-name, the sequencer — sees the effective
/// (renamed) names without knowing anything changed. Editing persists to the
/// profile for ALL drivers and additionally pushes into the driver when it
/// accepts names (INDI), so nothing regresses for writable wheels.
/// </summary>
public sealed class EffectiveFilterWheel : IFilterWheel {
    private readonly IFilterWheel _inner;
    private readonly ProfileService _profiles;

    public EffectiveFilterWheel(IFilterWheel inner, ProfileService profiles) {
        _inner = inner;
        _profiles = profiles;
    }

    /// <summary>The wrapped backend, for code that legitimately needs the raw
    /// driver (none today; kept for clarity).</summary>
    public IFilterWheel Inner => _inner;

    // ── straight pass-through ────────────────────────────────────────
    public string DeviceName => _inner.DeviceName;
    public bool IsConnected => _inner.IsConnected;
    public int Position => _inner.Position;
    public bool IsMoving => _inner.IsMoving;
    public int FilterCount => _inner.FilterCount;

    // Capabilities intentionally reflect the INNER driver: the filter-name
    // RESTORE service keys on SupportsEditNames to decide whether to push saved
    // names back into the driver, and that must stay true only for writable
    // (INDI) wheels. Profile-side editing is surfaced separately by the status
    // layer (editNames is emitted true whenever a wheel is connected).
    public FilterWheelCapabilities Capabilities => _inner.Capabilities;

    public Task ConnectAsync(CancellationToken ct = default) => _inner.ConnectAsync(ct);
    public Task DisconnectAsync(CancellationToken ct = default) => _inner.DisconnectAsync(ct);
    public Task SetPositionAsync(int position, CancellationToken ct = default)
        => _inner.SetPositionAsync(position, ct);

    // ── effective (profile-overlaid) names ───────────────────────────

    /// <summary>Driver names with the rig's saved names laid over them per slot:
    /// a non-blank saved name wins, otherwise the driver's own name shows. The
    /// length always matches the driver's slot count, so downstream code that
    /// pairs names with slots is unaffected.</summary>
    public string[] FilterNames {
        get {
            var driver = _inner.FilterNames ?? Array.Empty<string>();
            var saved = _profiles.ActiveEquipmentProfile?.FilterNames ?? Array.Empty<string>();
            var outp = new string[driver.Length];
            for (int i = 0; i < driver.Length; i++) {
                outp[i] = (i < saved.Length && !string.IsNullOrWhiteSpace(saved[i]))
                    ? saved[i]
                    : driver[i];
            }
            return outp;
        }
    }

    /// <summary>The effective name of the slot the wheel currently sits on.
    /// Resolved by matching the driver's own current name to its slot (so the
    /// per-driver position base, 0 vs 1, never enters into it) and returning the
    /// overlaid name for that slot.</summary>
    public string CurrentFilterName {
        get {
            var driver = _inner.FilterNames ?? Array.Empty<string>();
            var cur = _inner.CurrentFilterName ?? "";
            int idx = Array.FindIndex(driver,
                n => string.Equals(n, cur, StringComparison.OrdinalIgnoreCase));
            var eff = FilterNames;
            return (idx >= 0 && idx < eff.Length) ? eff[idx] : cur;
        }
    }

    /// <summary>Move to the slot whose EFFECTIVE name matches, then delegate to
    /// the driver by its OWN name for that slot so the backend maps to the right
    /// position with its native base. Falls back to the driver's own resolution
    /// when the name isn't in the effective list (e.g. a caller passing a raw
    /// driver name).</summary>
    public Task SetFilterByNameAsync(string filterName, CancellationToken ct = default) {
        var eff = FilterNames;
        int idx = Array.FindIndex(eff,
            n => string.Equals(n, filterName, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return _inner.SetFilterByNameAsync(filterName, ct);
        var driver = _inner.FilterNames ?? Array.Empty<string>();
        if (idx < driver.Length) return _inner.SetFilterByNameAsync(driver[idx], ct);
        return _inner.SetPositionAsync(idx, ct);
    }

    /// <summary>Persist filter names on the rig (the label source of truth for
    /// every driver) and, when the driver accepts names, push them in too. A
    /// blank entry keeps the current effective name for that slot rather than
    /// erasing it, and per-filter focus offsets follow the rename by slot so
    /// they aren't orphaned.</summary>
    public async Task SetFilterNamesAsync(string[] names, CancellationToken ct = default) {
        names ??= Array.Empty<string>();
        var before = FilterNames;   // current effective, for the offset remap + blank-keep
        var final = new string[names.Length];
        for (int i = 0; i < names.Length; i++) {
            var n = (names[i] ?? "").Trim();
            final[i] = !string.IsNullOrEmpty(n) ? n
                     : (i < before.Length ? before[i] : n);
        }

        var prof = _profiles.ActiveEquipmentProfile;
        if (prof != null) {
            _profiles.UpdateEquipmentProfile(prof.Id, r => {
                r.FilterNames = (string[])final.Clone();
                // Carry per-filter focus offsets across the rename: for each slot
                // whose name changed, move the value from the old key to the new.
                if (r.FilterOffsets != null && r.FilterOffsets.Count > 0) {
                    var remapped = new Dictionary<string, int>(r.FilterOffsets);
                    for (int i = 0; i < final.Length && i < before.Length; i++) {
                        if (!string.Equals(before[i], final[i], StringComparison.Ordinal)
                            && remapped.TryGetValue(before[i], out var off)) {
                            remapped.Remove(before[i]);
                            remapped[final[i]] = off;
                        }
                    }
                    r.FilterOffsets = remapped;
                }
            });
        }

        // Writable driver (INDI): push so the driver's own labels match too.
        // Best-effort — a driver that rejects the push must not fail the save
        // that already landed on the rig.
        if (_inner.Capabilities.SupportsEditNames) {
            try { await _inner.SetFilterNamesAsync(final, ct); }
            catch { /* profile is authoritative; driver push is a bonus */ }
        }
    }
}
