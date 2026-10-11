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

using NINA.INDI.Client;

namespace NINA.INDI.Devices;

public class IndiFilterWheel : NINA.Image.Interfaces.IFilterWheel {
    private readonly IndiClient _client;

    public string DeviceName { get; }
    /// <summary>
    /// True only when the INDI client is up AND the device's per-device
    /// CONNECTION switch is in the CONNECT state. See
    /// <see cref="IndiCamera.IsConnected"/> for the rationale.
    /// </summary>
    public bool IsConnected
        => _client.IsConnected
           && _client.GetSwitch(DeviceName, "CONNECTION", "CONNECT");

    public int Position {
        get => (int)_client.GetNumber(DeviceName, "FILTER_SLOT", "FILTER_SLOT_VALUE");
    }

    public bool IsMoving {
        get {
            var prop = _client.GetProperty(DeviceName, "FILTER_SLOT");
            return prop?.State == Protocol.IndiPropertyState.Busy;
        }
    }

    public string[] FilterNames {
        get {
            var prop = _client.GetProperty(DeviceName, "FILTER_NAME");
            if (prop is Protocol.IndiTextProperty textProp && textProp.Values.Count > 0) {
                // FIX: previously did OrderBy(kv.Key) (lexicographic),
                // which on wheels with >9 slots ordered the names
                // FILTER_SLOT_NAME_1, _10, _2, _3, ..., _9 -- garbage
                // alignment with the slot dropdown. Sort by the
                // trailing integer instead. Falls back to lexicographic
                // for any element whose name doesn't end in digits, so
                // a non-conformant driver doesn't crash the read.
                return textProp.Values
                    .OrderBy(kv => ExtractIndex(kv.Key))
                    .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => kv.Value)
                    .ToArray();
            }
            return [];
        }
    }

    /// <summary>Parse the trailing integer from an INDI filter-name
    /// element id like <c>FILTER_SLOT_NAME_1</c> / <c>FILTER_SLOT_NAME_10</c>.
    /// Returns <see cref="int.MaxValue"/> for anything that doesn't
    /// end in digits, so non-conformant elements sink to the end of
    /// the list instead of corrupting the ordering of the conformant
    /// ones.</summary>
    private static int ExtractIndex(string elementId) {
        int i = elementId.Length - 1;
        while (i >= 0 && char.IsDigit(elementId[i])) i--;
        if (i == elementId.Length - 1) return int.MaxValue;
        var tail = elementId.AsSpan(i + 1);
        return int.TryParse(tail, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n : int.MaxValue;
    }

    public int FilterCount {
        get {
            var names = FilterNames;
            return names.Length > 0 ? names.Length : 0;
        }
    }

    public string CurrentFilterName {
        get {
            var pos = Position;
            var names = FilterNames;
            if (pos >= 1 && pos <= names.Length)
                return names[pos - 1];
            return $"Filter {pos}";
        }
    }

    public IndiFilterWheel(IndiClient client, string deviceName) {
        _client = client;
        DeviceName = deviceName;
    }

    /// <summary>The slot range the DRIVER publishes on FILTER_SLOT, or (0, 0)
    /// when the property is not there. Two things are read off it that nothing
    /// else can tell us:
    ///
    /// A position below the minimum means the wheel does not know where it is.
    /// A ZWO EFW answers -1 to the SDK while it is uncalibrated, which the INDI
    /// driver publishes as slot 0 against a minimum of 1, and in that state
    /// nothing the wheel says about itself is true, including how many slots it
    /// has. A firmware write leaves it exactly there, and it looks like a dead
    /// wheel: field session 2026-10-02, where the cure was a calibration.
    ///
    /// A maximum larger than the number of FILTER_NAME elements means the slot
    /// count and the name vector disagree, and the name vector is what every
    /// consumer sizes its filter list from. Reloading the driver rebuilds it;
    /// when that is not enough, the driver's saved config is in the way.
    /// Same session: the wheel reported 8 slots behind 4 names for a while.
    /// </summary>
    public (int Min, int Max) SlotRange {
        get {
            if (_client.GetProperty(DeviceName, "FILTER_SLOT") is Protocol.IndiNumberProperty p
                && p.Values.TryGetValue("FILTER_SLOT_VALUE", out var el)) {
                return ((int)Math.Round(el.Min), (int)Math.Round(el.Max));
            }
            return (0, 0);
        }
    }

    /// <summary>The driver offers a calibration (ZWO's EFW does, as
    /// FILTER_CALIBRATION). Probed live, so a wheel without one reports false
    /// and the button stays hidden.</summary>
    public bool SupportsCalibration
        => _client.GetProperty(DeviceName, "FILTER_CALIBRATION") is Protocol.IndiSwitchProperty;

    /// <summary>Run the wheel's own calibration: it turns the carousel to find
    /// its index and relearns its positions. Ack-based, because a refusal here
    /// (wheel busy, motor stuck) has to reach the operator rather than look
    /// like a calibration that worked. The wheel is unusable until it finishes,
    /// and on a ZWO EFW it takes the better part of a minute.</summary>
    public async Task CalibrateAsync(CancellationToken ct = default) {
        if (!SupportsCalibration)
            throw new InvalidOperationException(
                $"Filter wheel '{DeviceName}' does not offer a calibration.");
        var ack = await _client.SetSwitchAsyncAck(DeviceName, "FILTER_CALIBRATION",
            new Dictionary<string, bool> { ["CALIBRATE"] = true }, ct: ct);
        if (ack.Rejected) {
            var detail = string.IsNullOrEmpty(ack.AlertMessage)
                ? "(no message from driver)"
                : ack.AlertMessage;
            throw new InvalidOperationException(
                $"Filter wheel '{DeviceName}' rejected the calibration: {detail}");
        }
    }

    public Task ConnectAsync(CancellationToken ct = default)
        => _client.ConnectDeviceAsync(DeviceName, ct);

    public Task DisconnectAsync(CancellationToken ct = default)
        => _client.DisconnectDeviceAsync(DeviceName, ct);

    public async Task SetPositionAsync(int position, CancellationToken ct = default) {
        // INDIROB-1: ack-based write so a rejected filter change
        // (slot out of range, wheel uncalibrated, motor stuck) surfaces
        // as an exception with the driver's message instead of silently
        // failing. Filter wheels are slow (1-3s per change) so the
        // Alert path matters -- a fire-and-forget that hits an Alert
        // would let the rest of the sequence run with the wrong filter.
        var ack = await _client.SetNumberAsyncAck(DeviceName, "FILTER_SLOT",
            new Dictionary<string, double> { ["FILTER_SLOT_VALUE"] = position }, ct: ct);
        if (ack.Rejected) {
            var detail = string.IsNullOrEmpty(ack.AlertMessage)
                ? "(no message from driver)"
                : ack.AlertMessage;
            throw new InvalidOperationException(
                $"Filter wheel '{DeviceName}' rejected slot change to {position}: {detail}");
        }
        // TimedOut = silent driver. Don't throw here because some
        // mechanical wheels legitimately take longer than the 5s
        // default ack window to acknowledge (servo windup); the
        // upstream caller polls Position separately for completion.

        // Persist the slot in the driver's own config (debounced).
        //
        // INDI drivers save FILTER_SLOT in ~/.indi/<driver>_config.xml, and
        // IndiClient auto-dispatches CONFIG_LOAD on every CONNECT, which
        // re-applies that value and MOVES the wheel. Nothing wrote the file
        // after a filter change, so whatever slot happened to be saved once
        // became the filter every session started on: a rig left on Ha+ came
        // back the next night on the slot in the file. Saving here is what
        // makes "the last filter used" the thing the driver remembers, and it
        // is the same treatment the serial-port choice gets in
        // IndiClient.SetDevicePortAsync.
        //
        // Deliberately also on the TimedOut path: CONFIG_SAVE stores the
        // driver's CURRENT property values, so a write that never landed
        // saves the slot the wheel is really on, not the one we asked for.
        _client.ScheduleConfigSaveDebounced(DeviceName);
    }

    public async Task SetFilterByNameAsync(string filterName, CancellationToken ct = default) {
        var names = FilterNames;
        for (int i = 0; i < names.Length; i++) {
            if (names[i].Equals(filterName, StringComparison.OrdinalIgnoreCase)) {
                await SetPositionAsync(i + 1, ct);
                return;
            }
        }
        throw new InvalidOperationException($"Filter '{filterName}' not found. Available: {string.Join(", ", names)}");
    }

    /// <summary>Capabilities advertisement. INDI wheels with a
    /// FILTER_NAME text vector accept name pushes; we probe the live
    /// property so wheels without it correctly report SupportsEditNames=false.</summary>
    public NINA.Image.Interfaces.FilterWheelCapabilities Capabilities
        => new(SupportsEditNames:
            _client.GetProperty(DeviceName, "FILTER_NAME") is Protocol.IndiTextProperty);

    /// <summary>Push a new filter-name set into the driver via INDI
    /// standard <c>FILTER_NAME</c>, then persist it (see the CONFIG_SAVE
    /// note at the end of this method -- writing the vector alone only
    /// changes the RUNNING driver). Element ids
    /// must match what the driver already advertises (typically
    /// <c>FILTER_SLOT_NAME_1</c>..<c>_N</c> sorted by trailing index);
    /// we map <paramref name="names"/>[0] to the lowest-indexed
    /// element, [1] to the next, and so on.</summary>
    public async Task SetFilterNamesAsync(string[] names, CancellationToken ct = default) {
        if (names == null) throw new ArgumentNullException(nameof(names));
        var prop = _client.GetProperty(DeviceName, "FILTER_NAME") as Protocol.IndiTextProperty;
        if (prop == null) {
            throw new NotSupportedException(
                $"Filter wheel '{DeviceName}' does not expose FILTER_NAME -- driver doesn't support name push.");
        }
        // Preserve the driver's actual element ordering by using the
        // same numeric sort as the FilterNames getter -- that ensures
        // names[0] lands on slot 1, names[1] on slot 2, etc., even
        // when the driver advertises the elements in a non-numeric
        // dictionary order.
        var orderedKeys = prop.Values
            .OrderBy(kv => ExtractIndex(kv.Key))
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key)
            .ToArray();
        if (names.Length != orderedKeys.Length) {
            throw new ArgumentException(
                $"Expected {orderedKeys.Length} filter names (driver advertises {orderedKeys.Length} slots), got {names.Length}.",
                nameof(names));
        }
        var payload = new Dictionary<string, string>(orderedKeys.Length);
        for (int i = 0; i < orderedKeys.Length; i++) {
            payload[orderedKeys[i]] = names[i] ?? "";
        }
        await _client.SetTextAsync(DeviceName, "FILTER_NAME", payload, ct);
        // Writing FILTER_NAME only changes the RUNNING driver instance. Without
        // a CONFIG_SAVE nothing reaches ~/.indi/{driver}_config.xml, so the
        // names survive client reconnects (the driver process is still up) but
        // are silently lost the moment the driver restarts -- a reboot, an
        // indiserver restart, a profile switch. The user hits this as "I named
        // my filters yesterday and today they are back to Red/Green/Blue".
        // The connect path already issues CONFIG_LOAD after CONNECT, so saving
        // here is the whole other half of the round trip.
        //
        // The save is IMMEDIATE, not the 3 s debounced variant the INDI panel
        // uses, because of a race proven in the field log: a CONFIG_LOAD is
        // auto-dispatched on every device connect, and one landed in the SAME
        // SECOND as a name write, reloading the on-disk values over the user's
        // fresh edit. With a debounce the order becomes
        //     write new -> LOAD reverts -> save fires 3 s later
        // which would persist the REVERTED names, cementing the bug instead of
        // fixing it. Saving synchronously also makes any later CONFIG_LOAD
        // harmless: it now reloads exactly what we just wrote.
        await _client.SaveDeviceConfigAsync(DeviceName, ct);
    }
}