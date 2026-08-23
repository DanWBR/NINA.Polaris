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

namespace NINA.Polaris.Services.Focus;

/// <summary>What happened (or was suggested) for a filter change.</summary>
public enum FocusMemoryStatus {
    Disabled,             // feature off for this rig
    Skipped,              // a run owns the focuser (sequence/AUTORUN/live-stack/AF)
    NoFocuser,            // no focuser connected
    Restored,            // moved to the filter's own valid stored point
    OffsetApplied,        // derived from another fresh filter via a learned offset
    Suggested,            // a valid point exists but auto-apply is off
    StaleAfStarted,       // stored point stale, autofocus triggered
    StaleAfRecommended,   // stored point stale, autofocus recommended (not run)
    NoMemory              // nothing learned for this filter yet
}

public sealed record FocusMemoryOutcome(
    FocusMemoryStatus Status,
    string Filter,
    int? Position = null,
    string? DerivedFrom = null,
    double? TempDeltaC = null,
    string? Reason = null);

/// <summary>
/// Learns the optimal focuser position per filter (recorded by autofocus) and,
/// on a manual filter change, reuses a still-valid point instead of forcing a
/// fresh sweep — moving straight there, deriving it from another freshly
/// focused filter via a learned offset, or recommending autofocus when nothing
/// can be trusted. Scoped per rig via <see cref="EquipmentProfile.FilterFocusMemory"/>.
/// </summary>
public sealed class FilterFocusMemoryService {
    private readonly EquipmentManager _equip;
    private readonly ProfileService _profiles;
    private readonly AutoFocusService _autoFocus;
    private readonly SequenceEngine _sequence;
    private readonly LiveStackingService _liveStack;
    private readonly ILogger<FilterFocusMemoryService> _logger;

    public FilterFocusMemoryService(EquipmentManager equip, ProfileService profiles,
                                    AutoFocusService autoFocus, SequenceEngine sequence,
                                    LiveStackingService liveStack,
                                    ILogger<FilterFocusMemoryService> logger) {
        _equip = equip;
        _profiles = profiles;
        _autoFocus = autoFocus;
        _sequence = sequence;
        _liveStack = liveStack;
        _logger = logger;
    }

    /// <summary>True while some run owns the focuser and this service must keep
    /// its hands off (the sequencer/AUTORUN/live-stack apply their own offsets;
    /// AF is sweeping).</summary>
    private bool RunActive =>
        _autoFocus.State == AutoFocusState.Running
        || _sequence.State == SequenceState.Running
        || _liveStack.IsRunning;

    /// <summary>Decide + act on a filter change to <paramref name="targetFilter"/>
    /// (the effective filter name). Best-effort: any hardware failure is logged
    /// and folded into the returned outcome, never thrown.</summary>
    public async Task<FocusMemoryOutcome> OnFilterSelectedAsync(string targetFilter, CancellationToken ct = default) {
        var rig = _profiles.ActiveEquipmentProfile;
        var s = rig?.AutoFocus;
        if (rig == null || s == null || !s.FilterMemoryEnabled)
            return new FocusMemoryOutcome(FocusMemoryStatus.Disabled, targetFilter);
        if (RunActive)
            return new FocusMemoryOutcome(FocusMemoryStatus.Skipped, targetFilter);

        var focuser = _equip.Focuser;
        if (focuser is not { IsConnected: true })
            return new FocusMemoryOutcome(FocusMemoryStatus.NoFocuser, targetFilter);

        double temp = focuser.Temperature;
        var plan = FilterFocusMath.PlanForFilter(rig, targetFilter, temp, focuser.DeviceName, s, DateTime.UtcNow);

        switch (plan.Kind) {
            case FocusPlanKind.RestoreAbsolute:
            case FocusPlanKind.OffsetTransfer:
                if (!s.FilterMemoryAutoApply)
                    return new FocusMemoryOutcome(FocusMemoryStatus.Suggested, targetFilter,
                        plan.Position, plan.DerivedFrom, plan.TempDeltaC);
                var moved = await MoveAsync(focuser, plan.Position, ct);
                if (!moved)
                    return new FocusMemoryOutcome(FocusMemoryStatus.Suggested, targetFilter,
                        plan.Position, plan.DerivedFrom, plan.TempDeltaC, "focuser move failed");
                var okStatus = plan.Kind == FocusPlanKind.RestoreAbsolute
                    ? FocusMemoryStatus.Restored : FocusMemoryStatus.OffsetApplied;
                return new FocusMemoryOutcome(okStatus, targetFilter, plan.Position, plan.DerivedFrom, plan.TempDeltaC);

            case FocusPlanKind.Stale:
                if (s.FilterMemoryAutoRunWhenStale) {
                    try { _autoFocus.Start(new AutoFocusRequest()); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Auto-run AF on stale filter memory failed"); }
                    return new FocusMemoryOutcome(FocusMemoryStatus.StaleAfStarted, targetFilter, plan.Position,
                        Reason: plan.Reason);
                }
                return new FocusMemoryOutcome(FocusMemoryStatus.StaleAfRecommended, targetFilter, plan.Position,
                    Reason: plan.Reason);

            default:
                return new FocusMemoryOutcome(FocusMemoryStatus.NoMemory, targetFilter);
        }
    }

    /// <summary>Force-apply the stored/derived point for a filter regardless of
    /// the auto-apply toggle (the UI "Apply" button). Returns the resulting
    /// outcome; a stale/missing point yields the same advisory status as the
    /// automatic path.</summary>
    public async Task<FocusMemoryOutcome> ApplyStoredAsync(string targetFilter, CancellationToken ct = default) {
        var rig = _profiles.ActiveEquipmentProfile;
        var s = rig?.AutoFocus;
        if (rig == null || s == null)
            return new FocusMemoryOutcome(FocusMemoryStatus.Disabled, targetFilter);
        if (RunActive)
            return new FocusMemoryOutcome(FocusMemoryStatus.Skipped, targetFilter);
        var focuser = _equip.Focuser;
        if (focuser is not { IsConnected: true })
            return new FocusMemoryOutcome(FocusMemoryStatus.NoFocuser, targetFilter);

        var plan = FilterFocusMath.PlanForFilter(rig, targetFilter, focuser.Temperature,
            focuser.DeviceName, s, DateTime.UtcNow);
        if (plan.Kind is FocusPlanKind.RestoreAbsolute or FocusPlanKind.OffsetTransfer) {
            var moved = await MoveAsync(focuser, plan.Position, ct);
            var status = !moved ? FocusMemoryStatus.Suggested
                : plan.Kind == FocusPlanKind.RestoreAbsolute ? FocusMemoryStatus.Restored
                : FocusMemoryStatus.OffsetApplied;
            return new FocusMemoryOutcome(status, targetFilter, plan.Position, plan.DerivedFrom, plan.TempDeltaC);
        }
        if (plan.Kind == FocusPlanKind.Stale)
            return new FocusMemoryOutcome(FocusMemoryStatus.StaleAfRecommended, targetFilter, plan.Position,
                Reason: plan.Reason);
        return new FocusMemoryOutcome(FocusMemoryStatus.NoMemory, targetFilter);
    }

    /// <summary>Forget the stored point for one filter and recompute offsets.</summary>
    public void ClearEntry(string filter) {
        var rig = _profiles.ActiveEquipmentProfile;
        if (rig == null) return;
        double tol = rig.AutoFocus?.FilterMemoryTempToleranceC ?? 1.5;
        _profiles.UpdateEquipmentProfile(rig.Id, r => {
            if (r.FilterFocusMemory.Remove(filter))
                FilterFocusMath.RecomputeOffsets(r, tol);
        });
    }

    private async Task<bool> MoveAsync(IFocuser focuser, int position, CancellationToken ct) {
        try {
            await focuser.MoveAbsoluteAsync(position, ct);
            _logger.LogInformation("Filter focus memory: focuser → {Pos}", position);
            return true;
        } catch (OperationCanceledException) { throw; } catch (Exception ex) {
            _logger.LogWarning(ex, "Filter focus memory: focuser move to {Pos} failed", position);
            return false;
        }
    }

    /// <summary>Record an autofocus result for a filter and refresh the derived
    /// relative offsets. Static so <see cref="AutoFocusService"/> can call it
    /// inside a profile mutation without taking a dependency on this service.</summary>
    public static void RecordAndRecompute(EquipmentProfile rig, string filter, int position,
                                          double temperatureC, string? focuserName, double? hfr, double tolC) {
        rig.FilterFocusMemory[filter] = new FilterFocusMemory {
            Position = position,
            TemperatureC = temperatureC,
            Utc = DateTime.UtcNow,
            FocuserName = focuserName,
            Hfr = hfr
        };
        FilterFocusMath.RecomputeOffsets(rig, tolC);
    }
}

/// <summary>What to do for a filter, decided from the stored memory alone (pure,
/// unit-testable).</summary>
public enum FocusPlanKind { RestoreAbsolute, OffsetTransfer, Stale, None }

public readonly record struct FocusPlan(
    FocusPlanKind Kind, int Position = 0, string? DerivedFrom = null,
    double? TempDeltaC = null, string? Reason = null);

/// <summary>Pure decision + offset-derivation logic, isolated from hardware and
/// DI so it can be tested directly.</summary>
public static class FilterFocusMath {
    /// <summary>Is a stored point still trustworthy at the current temperature /
    /// equipment / age?</summary>
    public static bool IsValid(FilterFocusMemory mem, double currentTempC, string? focuserName,
                               double tolC, double maxAgeHours, DateTime nowUtc) {
        if (mem == null) return false;
        if ((nowUtc - mem.Utc).TotalHours > maxAgeHours) return false;
        // Equipment-change proxy: a different focuser invalidates the absolute.
        if (!string.IsNullOrEmpty(mem.FocuserName) && !string.IsNullOrEmpty(focuserName)
            && !string.Equals(mem.FocuserName, focuserName, StringComparison.Ordinal)) return false;
        // Temperature: only enforced when both sides have a real probe reading.
        if (!double.IsNaN(currentTempC) && !double.IsNaN(mem.TemperatureC)
            && Math.Abs(currentTempC - mem.TemperatureC) > tolC) return false;
        return true;
    }

    /// <summary>Decide how to reach focus for <paramref name="target"/>.</summary>
    public static FocusPlan PlanForFilter(EquipmentProfile rig, string target, double currentTempC,
                                          string? focuserName, AutoFocusSettings s, DateTime nowUtc) {
        var mems = rig.FilterFocusMemory;
        double tol = s.FilterMemoryTempToleranceC;
        double maxAge = s.FilterMemoryMaxAgeHours;

        FilterFocusMemory? mem = mems.TryGetValue(target, out var m) ? m : null;

        // 1. The filter's own point, if still valid → jump straight there.
        if (mem != null && IsValid(mem, currentTempC, focuserName, tol, maxAge, nowUtc)) {
            double? dt = (!double.IsNaN(currentTempC) && !double.IsNaN(mem.TemperatureC))
                ? currentTempC - mem.TemperatureC : (double?)null;
            return new FocusPlan(FocusPlanKind.RestoreAbsolute, mem.Position, null, dt);
        }

        // 2. Offset transfer: anchor on the FRESHEST valid point of any other
        //    filter and shift by the temperature-stable learned offset delta.
        var anchorKey = FreshestValidAnchor(rig, target, currentTempC, focuserName, tol, maxAge, nowUtc);
        if (anchorKey != null
            && rig.FilterOffsets.TryGetValue(target, out var offT)
            && rig.FilterOffsets.TryGetValue(anchorKey, out var offA)) {
            var anchorMem = mems[anchorKey];
            int pos = anchorMem.Position + (offT - offA);
            return new FocusPlan(FocusPlanKind.OffsetTransfer, pos, anchorKey);
        }

        // 3. We have an old point but can't trust it → recommend AF.
        if (mem != null)
            return new FocusPlan(FocusPlanKind.Stale, mem.Position, null, null, StaleReason(mem, currentTempC, tol, maxAge, nowUtc));

        return new FocusPlan(FocusPlanKind.None);
    }

    private static string? FreshestValidAnchor(EquipmentProfile rig, string exclude, double currentTempC,
                                               string? focuserName, double tol, double maxAge, DateTime nowUtc) {
        string? best = null;
        DateTime bestUtc = DateTime.MinValue;
        foreach (var (name, mem) in rig.FilterFocusMemory) {
            if (string.Equals(name, exclude, StringComparison.OrdinalIgnoreCase)) continue;
            if (!rig.FilterOffsets.ContainsKey(name)) continue;
            if (!IsValid(mem, currentTempC, focuserName, tol, maxAge, nowUtc)) continue;
            if (mem.Utc > bestUtc) { bestUtc = mem.Utc; best = name; }
        }
        return best;
    }

    private static string StaleReason(FilterFocusMemory mem, double currentTempC, double tol,
                                      double maxAge, DateTime nowUtc) {
        if ((nowUtc - mem.Utc).TotalHours > maxAge) return "too old";
        if (!double.IsNaN(currentTempC) && !double.IsNaN(mem.TemperatureC)
            && Math.Abs(currentTempC - mem.TemperatureC) > tol) return "temperature changed";
        return "equipment changed";
    }

    /// <summary>Rebuild <see cref="EquipmentProfile.FilterOffsets"/> from the
    /// learned memory: offsets relative to a reference filter, using only points
    /// recorded at a comparable temperature. Filters without a comparable point
    /// keep any hand-entered offset.</summary>
    public static void RecomputeOffsets(EquipmentProfile rig, double tolC) {
        var mems = rig.FilterFocusMemory;
        if (mems.Count == 0) return;

        string? refKey = ResolveReference(rig);
        if (refKey == null || !mems.TryGetValue(refKey, out var refMem)) return;

        rig.FilterOffsets[refKey] = 0;
        foreach (var (name, mem) in mems) {
            if (string.Equals(name, refKey, StringComparison.Ordinal)) continue;
            bool tempOk = double.IsNaN(mem.TemperatureC) || double.IsNaN(refMem.TemperatureC)
                          || Math.Abs(mem.TemperatureC - refMem.TemperatureC) <= tolC;
            if (tempOk) rig.FilterOffsets[name] = mem.Position - refMem.Position;
        }
    }

    private static string? ResolveReference(EquipmentProfile rig) {
        var mems = rig.FilterFocusMemory;
        var configured = rig.AutoFocus?.FilterOffsetReference;
        if (!string.IsNullOrWhiteSpace(configured) && mems.ContainsKey(configured!)) return configured;
        if (mems.ContainsKey("L")) return "L";
        // else the filter with the newest memory
        string? best = null; DateTime bestUtc = DateTime.MinValue;
        foreach (var (name, mem) in mems)
            if (mem.Utc > bestUtc) { bestUtc = mem.Utc; best = name; }
        return best;
    }
}
