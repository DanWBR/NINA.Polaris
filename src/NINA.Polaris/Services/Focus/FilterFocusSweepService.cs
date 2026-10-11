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

/// <summary>
/// Learns the focus point of every selected filter in one pass, before the
/// night starts.
///
/// <para>Every piece of this already existed and was driven by hand: switch the
/// filter, run autofocus, read the position off the V-curve, work out the delta
/// against the reference filter, type it into the rig's filter-offsets table,
/// then do it again for the next filter. This walks the wheel instead, and what
/// it produces is what <see cref="FilterFocusMemoryService"/> already stores: an
/// absolute position per filter with its temperature, from which the relative
/// offsets the sequencer consumes are derived.</para>
///
/// <para>Nothing reaches the rig while the run is going. Each autofocus is
/// started with <c>RecordFilterMemory = false</c> so its own recording hook
/// stays quiet, the results live here, and <see cref="Apply"/> is what writes
/// them: one <see cref="FilterFocusMemoryService.RecordAndRecompute"/> per
/// accepted filter, so the offset math has exactly one implementation.</para>
///
/// <para>It never takes <see cref="CameraCaptureGate"/>. That semaphore is not
/// reentrant and <see cref="AutoFocusService"/> already takes it per frame, so
/// holding it around this loop would deadlock on the first exposure. Collisions
/// are refused up front in <see cref="Start"/> instead, which is also the
/// friendlier answer.</para>
/// </summary>
public sealed class FilterFocusSweepService {
    private readonly EquipmentManager _equip;
    private readonly ProfileService _profiles;
    private readonly AutoFocusService _autoFocus;
    private readonly SequenceEngine _sequence;
    private readonly LiveStackingService _liveStack;
    private readonly ILogger<FilterFocusSweepService> _logger;

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private readonly object _stateLock = new();

    public FilterFocusSweepState State { get; private set; } = FilterFocusSweepState.Idle;
    public FilterFocusSweepProgress Progress { get; private set; } = new();
    public string? LastError { get; private set; }
    /// <summary>When the last <see cref="Apply"/> wrote to the rig. Null while
    /// the current results are still only held here.</summary>
    public DateTime? AppliedAt { get; private set; }

    /// <summary>Measured results that have not been written to the rig. The
    /// operator has spent real sky time on these, so the host reports itself
    /// busy while they are pending.</summary>
    public bool HasPendingResults =>
        Progress.Results.Any(r => r.Measured && !r.Applied);

    /// <summary>Autofocus runs per filter: the first, plus one retry. A narrow
    /// filter under thin cloud fails often enough that one retry is worth the
    /// couple of minutes; a second retry usually reproduces the same failure,
    /// so the filter is recorded as failed and the run moves on instead of
    /// stalling the whole set on one slot.</summary>
    internal const int AttemptsPerFilter = 2;

    /// <summary>Fit quality below which a successful run is still flagged.
    /// <see cref="AutoFocusService"/> normally enforces the rig's own
    /// <c>RSquaredThreshold</c> and fails the run itself, so this only bites on
    /// a rig that set the threshold to 0 to disable that gate.</summary>
    internal const double DefaultRSquaredGate = 0.7;

    /// <summary>How long to wait for the wheel to report the requested filter
    /// before going ahead anyway. Mechanical wheels take 1 to 3 s, and a driver
    /// that never clears IsMoving is not a reason to abandon the run.</summary>
    internal static readonly TimeSpan FilterSettleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Ceiling on one autofocus run. Generous on purpose: a 9-point
    /// sweep with confirmation frames at 4 s on a slow focuser is minutes of
    /// work, and the rig's own Attempts setting can double it.</summary>
    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(20);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    public FilterFocusSweepService(EquipmentManager equip, ProfileService profiles,
                                   AutoFocusService autoFocus, SequenceEngine sequence,
                                   LiveStackingService liveStack,
                                   ILogger<FilterFocusSweepService> logger) {
        _equip = equip;
        _profiles = profiles;
        _autoFocus = autoFocus;
        _sequence = sequence;
        _liveStack = liveStack;
        _logger = logger;
    }

    /// <summary>Begin a run over the request's filters (every filter on the
    /// wheel when the list is absent). Returns once the run task is spawned;
    /// progress is polled off <see cref="Progress"/>, the same way autofocus
    /// and the flat wizard are.
    ///
    /// <para><paramref name="request"/>.Merge keeps the results already
    /// measured, which is what the tab's "re-run failed" does.</para></summary>
    public void Start(FilterFocusSweepRequest? request) {
        List<string> filters;
        lock (_stateLock) {
            if (State == FilterFocusSweepState.Running)
                throw new InvalidOperationException("A per-filter focus run is already going");
            if (_autoFocus.State == AutoFocusState.Running)
                throw new InvalidOperationException("Auto-focus is already running");
            if (_sequence.State == SequenceState.Running)
                throw new InvalidOperationException("A sequence is running, stop it first");
            if (_liveStack.IsRunning)
                throw new InvalidOperationException("Live stacking is running, stop it first");

            var wheel = _equip.FilterWheel;
            if (wheel == null)
                throw new InvalidOperationException("No filter wheel selected");
            if (!wheel.IsConnected)
                throw new InvalidOperationException("Connect the filter wheel first");
            if (_equip.Focuser is not { IsConnected: true })
                throw new InvalidOperationException("No focuser connected");
            if (_equip.Camera is not { IsConnected: true })
                throw new InvalidOperationException("No imaging camera connected");

            filters = FilterSweepMath.PlanFilters(wheel.FilterNames, request?.Filters);

            var kept = request?.Merge == true
                ? Progress.Results.Where(
                    r => !filters.Contains(r.Filter, StringComparer.OrdinalIgnoreCase)).ToList()
                : new List<FilterFocusSweepResult>();

            _cts = new CancellationTokenSource();
            State = FilterFocusSweepState.Running;
            LastError = null;
            if (kept.Count == 0) AppliedAt = null;
            Progress = new FilterFocusSweepProgress {
                StartedAt = DateTime.UtcNow,
                TotalFilters = filters.Count,
                CurrentFilterIndex = -1,
                Filters = filters,
                Results = kept,
                Phase = "starting",
                ReferenceFilter = _profiles.ActiveEquipmentProfile?.AutoFocus?.FilterOffsetReference
            };
        }

        _runTask = Task.Run(() => RunAsync(filters, _cts!.Token));
        _logger.LogInformation("Per-filter focus run started over {N} filters: {Filters}",
            filters.Count, string.Join(", ", filters));
    }

    /// <summary>Cancel the run. The autofocus in flight is aborted as well, and
    /// it puts the focuser back where that one run started. Results already
    /// measured are kept: clouds arriving after four of six filters should not
    /// throw away the twelve minutes that worked.
    ///
    /// <para>Nothing is parked on an abort. The operator stopped this for a
    /// reason, and a surprise wheel plus focuser move right afterwards is the
    /// kind of thing that gets reported as a fault.</para></summary>
    public void Abort() {
        lock (_stateLock) {
            if (State != FilterFocusSweepState.Running) return;
            _cts?.Cancel();
        }
        if (_autoFocus.State == AutoFocusState.Running) _autoFocus.Abort();
    }

    /// <summary>Forget the held results without writing them.</summary>
    public int Discard() {
        lock (_stateLock) {
            if (State == FilterFocusSweepState.Running)
                throw new InvalidOperationException("The run is still going, stop it first");
        }
        int n = Progress.Results.Count;
        Progress = Progress with { Results = new List<FilterFocusSweepResult>(), Phase = "idle" };
        AppliedAt = null;
        return n;
    }

    /// <summary>Write the measured points into the active rig.
    ///
    /// <para>One profile mutation for the whole set, so one save and one
    /// edited-event. Each filter goes through
    /// <see cref="FilterFocusMemoryService.RecordAndRecompute"/>, the same call
    /// a single autofocus run uses, which keeps one implementation of the
    /// offset math.</para>
    ///
    /// <para>It also pins the reference filter on the rig, and that is not
    /// incidental: <c>RecomputeOffsets</c> resolves the reference itself and,
    /// with none configured and no filter named L, falls back to the FRESHEST
    /// memory entry. Applying a whole sweep at once would make that the last
    /// filter written, so the stored offsets would be measured against a
    /// different filter than the one the tab previewed. Pinning makes the
    /// preview a promise; the tab shows the field so this is a visible choice
    /// and not a silent rig edit.</para>
    ///
    /// <para><paramref name="filters"/> limits it to a subset (null applies
    /// every measured filter). Returns what was written.</para></summary>
    public FilterFocusSweepApplyOutcome Apply(IReadOnlyList<string>? filters = null,
                                              string? reference = null) {
        lock (_stateLock) {
            if (State == FilterFocusSweepState.Running)
                throw new InvalidOperationException("The run is still going, wait for it to finish");
        }
        var rig = _profiles.ActiveEquipmentProfile
            ?? throw new InvalidOperationException("No active rig");

        var wanted = filters is { Count: > 0 }
            ? new HashSet<string>(filters, StringComparer.OrdinalIgnoreCase)
            : null;
        var accepted = Progress.Results
            .Where(r => r.Measured && (wanted == null || wanted.Contains(r.Filter)))
            .ToList();
        if (accepted.Count == 0)
            throw new ArgumentException("Nothing to apply: no measured filter was selected");

        var refKey = !string.IsNullOrWhiteSpace(reference) ? reference
            : FilterSweepMath.ResolveSweepReference(accepted, Progress.ReferenceFilter);
        double tol = rig.AutoFocus?.FilterMemoryTempToleranceC ?? 1.5;
        var focuserName = _equip.Focuser?.DeviceName;

        Dictionary<string, int> offsets = new();
        _profiles.UpdateEquipmentProfile(rig.Id, r => {
            if (!string.IsNullOrWhiteSpace(refKey)) {
                r.AutoFocus ??= new AutoFocusSettings();
                r.AutoFocus.FilterOffsetReference = refKey;
            }
            foreach (var a in accepted)
                FilterFocusMemoryService.RecordAndRecompute(
                    r, a.Filter, a.Position, a.TemperatureC,
                    a.FocuserName ?? focuserName, a.Hfr, tol, a.MeasuredAtUtc);
            offsets = new Dictionary<string, int>(r.FilterOffsets, StringComparer.Ordinal);
        });

        foreach (var a in accepted) a.Applied = true;
        AppliedAt = DateTime.UtcNow;
        Progress = Progress with { ReferenceFilter = refKey, Results = Progress.Results };
        _logger.LogInformation(
            "Per-filter focus applied to rig '{Rig}': {N} filters, reference {Ref}, offsets {Offsets}",
            rig.Name, accepted.Count, refKey ?? "(none)",
            string.Join(", ", offsets.Select(kv => kv.Key + "=" + kv.Value)));
        return new FilterFocusSweepApplyOutcome(
            accepted.Select(a => a.Filter).ToList(), offsets, refKey);
    }

    // ── the run ──────────────────────────────────────────────────────

    private async Task RunAsync(List<string> filters, CancellationToken ct) {
        var wheel = _equip.FilterWheel!;
        var focuser = _equip.Focuser!;
        try {
            for (int i = 0; i < filters.Count; i++) {
                ct.ThrowIfCancellationRequested();
                var filter = filters[i];
                Progress = Progress with {
                    CurrentFilterIndex = i, CurrentFilter = filter, Phase = "switching", Attempt = 0
                };
                await SwitchToAsync(wheel, filter, ct);

                // No move to a remembered position before the sweep: on success
                // autofocus leaves the focuser on the point it just found, and
                // on failure it restores where that run started, so the next
                // filter always begins from the last good focus either way.
                FilterFocusSweepResult entry = new() { Filter = filter };
                for (int attempt = 1; attempt <= AttemptsPerFilter; attempt++) {
                    ct.ThrowIfCancellationRequested();
                    Progress = Progress with {
                        Phase = attempt == 1 ? "focusing" : "retrying", Attempt = attempt
                    };
                    var (result, error, timedOut) = await RunAutoFocusAsync(ct);
                    entry = FilterSweepMath.BuildResult(
                        filter, result, error, timedOut, attempt,
                        SafeTemperature(focuser), focuser.DeviceName,
                        RigRSquaredGate());
                    if (!FilterSweepMath.ShouldRetry(entry, attempt, AttemptsPerFilter)) break;
                    _logger.LogWarning(
                        "Per-filter focus: {Filter} attempt {A}/{Max} failed: {Err}",
                        filter, attempt, AttemptsPerFilter, entry.Error);
                }

                // A new list rather than Add on the live one: the status
                // broadcaster serialises Results once a second and would
                // otherwise be able to read it mid-append.
                var results = Progress.Results
                    .Where(r => !string.Equals(r.Filter, filter, StringComparison.OrdinalIgnoreCase))
                    .Append(entry).ToList();
                FilterSweepMath.DeriveOffsets(
                    results,
                    FilterSweepMath.ResolveSweepReference(results, Progress.ReferenceFilter),
                    RigTemperatureTolerance());
                Progress = Progress with { Results = results };

                if (entry.Measured)
                    _logger.LogInformation(
                        "Per-filter focus: {Filter} -> {Pos} (HFR {Hfr:F2}, R2 {R2:F2}, {T:F1} C)",
                        filter, entry.Position, entry.Hfr ?? 0, entry.RSquared ?? 0,
                        entry.TemperatureC);
            }

            Progress = Progress with { Phase = "parking", CurrentFilter = "" };
            await ParkOnReferenceAsync(wheel, focuser);

            lock (_stateLock) { State = FilterFocusSweepState.Idle; }
            Progress = Progress with { Phase = "done" };
            _logger.LogInformation("Per-filter focus run finished: {Ok}/{Total} measured",
                Progress.Results.Count(r => r.Measured), filters.Count);
        } catch (OperationCanceledException) {
            lock (_stateLock) { State = FilterFocusSweepState.Idle; LastError = "Cancelled"; }
            Progress = Progress with { Phase = "aborted", CurrentFilter = "" };
            _logger.LogInformation("Per-filter focus run aborted with {N} filters measured",
                Progress.Results.Count(r => r.Measured));
        } catch (Exception ex) {
            lock (_stateLock) { State = FilterFocusSweepState.Idle; LastError = ex.Message; }
            Progress = Progress with { Phase = "failed", CurrentFilter = "" };
            _logger.LogError(ex, "Per-filter focus run failed");
        }
    }

    /// <summary>One autofocus run, start to finish. The poll delay deliberately
    /// does NOT carry the run's token: a cancellation has to abort the autofocus
    /// first, or the sweep would walk away leaving a sweep in progress on the
    /// focuser.</summary>
    private async Task<(AutoFocusResult? Result, string? Error, bool TimedOut)> RunAutoFocusAsync(
            CancellationToken ct) {
        try {
            _autoFocus.Start(new AutoFocusRequest {
                FocuserSource = "main",
                RecordFilterMemory = false
            });
        } catch (Exception ex) {
            return (null, ex.Message, false);
        }

        var deadline = DateTime.UtcNow + AttemptTimeout;
        while (_autoFocus.State == AutoFocusState.Running) {
            if (ct.IsCancellationRequested) {
                _autoFocus.Abort();
                await WaitForAutoFocusIdleAsync();
                ct.ThrowIfCancellationRequested();
            }
            if (DateTime.UtcNow > deadline) {
                _autoFocus.Abort();
                await WaitForAutoFocusIdleAsync();
                return (null,
                    $"auto-focus did not finish within {AttemptTimeout.TotalMinutes:0} minutes",
                    true);
            }
            await Task.Delay(PollInterval, CancellationToken.None);
        }
        return (_autoFocus.LastResult, _autoFocus.LastError, false);
    }

    private async Task WaitForAutoFocusIdleAsync() {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (_autoFocus.State == AutoFocusState.Running && DateTime.UtcNow < deadline)
            await Task.Delay(PollInterval, CancellationToken.None);
    }

    /// <summary>Move the wheel and wait until it reports the filter. Compares
    /// NAMES, not slot numbers: the effective-name wrapper already hides the
    /// per-driver 0-vs-1 position base, and a name is what the caller asked
    /// for.</summary>
    private async Task SwitchToAsync(IFilterWheel wheel, string filter, CancellationToken ct) {
        if (string.Equals(SafeCurrentFilter(wheel), filter, StringComparison.OrdinalIgnoreCase))
            return;
        try {
            await wheel.SetFilterByNameAsync(filter, ct);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Per-filter focus: switch to {Filter} failed", filter);
            return;
        }
        var deadline = DateTime.UtcNow + FilterSettleTimeout;
        while (DateTime.UtcNow < deadline) {
            ct.ThrowIfCancellationRequested();
            if (!wheel.IsMoving
                && string.Equals(SafeCurrentFilter(wheel), filter, StringComparison.OrdinalIgnoreCase))
                return;
            await Task.Delay(100, ct);
        }
        _logger.LogWarning(
            "Per-filter focus: the wheel did not report '{Filter}' within {S}s, focusing anyway",
            filter, FilterSettleTimeout.TotalSeconds);
    }

    /// <summary>End on the reference filter, at the point measured for it, so
    /// the night can start from there.
    ///
    /// <para>Runs without a token: this is the run's cleanup, and cancelling
    /// half of it leaves the rig in a worse place than finishing it. A plain
    /// absolute move, like <see cref="FilterFocusMemoryService"/> does, because
    /// backlash compensation belongs inside a sweep and the position being
    /// restored is the one a sweep just left the focuser on.</para></summary>
    private async Task ParkOnReferenceAsync(IFilterWheel wheel, IFocuser focuser) {
        var reference = FilterSweepMath.ResolveSweepReference(
            Progress.Results, _profiles.ActiveEquipmentProfile?.AutoFocus?.FilterOffsetReference);
        if (reference == null) return;
        Progress = Progress with { ReferenceFilter = reference };

        await SwitchToAsync(wheel, reference, CancellationToken.None);
        var point = Progress.Results.FirstOrDefault(
            r => r.Measured && string.Equals(r.Filter, reference, StringComparison.OrdinalIgnoreCase));
        if (point == null || !focuser.IsConnected) return;
        try {
            await focuser.MoveAbsoluteAsync(point.Position, CancellationToken.None);
            _logger.LogInformation("Per-filter focus: parked on {Filter} at {Pos}",
                reference, point.Position);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Per-filter focus: could not park the focuser on {Filter}",
                reference);
        }
    }

    private double RigRSquaredGate() {
        var t = _profiles.ActiveEquipmentProfile?.AutoFocus?.RSquaredThreshold ?? 0;
        return t > 0 ? t : DefaultRSquaredGate;
    }

    private double RigTemperatureTolerance() =>
        _profiles.ActiveEquipmentProfile?.AutoFocus?.FilterMemoryTempToleranceC ?? 1.5;

    private static string? SafeCurrentFilter(IFilterWheel wheel) {
        try { return wheel.CurrentFilterName; } catch { return null; }
    }

    private static double SafeTemperature(IFocuser focuser) {
        try { return focuser.Temperature; } catch { return double.NaN; }
    }
}

/// <summary>Every decision the sweep makes, as pure functions, so the rules can
/// be tested without a wheel, a focuser or a sky. Sits beside the service the
/// way <c>FilterFocusMath</c> sits beside
/// <see cref="FilterFocusMemoryService"/>.</summary>
public static class FilterSweepMath {
    /// <summary>Which filters the run will walk, in WHEEL order.
    ///
    /// <para>Wheel order rather than the order the client happened to send, so
    /// the wheel turns one way through the set instead of jumping back and
    /// forth. A null or empty request means every filter the wheel publishes:
    /// the tab always sends an explicit list, but a bare POST should still do
    /// the obvious thing.</para></summary>
    public static List<string> PlanFilters(string[]? wheelFilters,
                                           IReadOnlyList<string>? requested) {
        var wheel = (wheelFilters ?? Array.Empty<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        if (wheel.Count == 0)
            throw new ArgumentException("The filter wheel has not published its filters yet");
        if (requested == null || requested.Count == 0) return wheel;

        var named = requested.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        if (named.Count == 0)
            throw new ArgumentException("Select at least one filter");

        var wanted = new HashSet<string>(named, StringComparer.OrdinalIgnoreCase);
        if (wanted.Count != named.Count)
            throw new ArgumentException("The same filter is listed twice");

        var unknown = wanted
            .Where(w => !wheel.Any(f => string.Equals(f, w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (unknown.Count > 0)
            throw new ArgumentException("Not on this filter wheel: " + string.Join(", ", unknown));

        return wheel.Where(f => wanted.Contains(f)).ToList();
    }

    /// <summary>Turn one autofocus outcome into the row the tab shows.
    ///
    /// <para><paramref name="rSquaredGate"/> only matters for a rig that set its
    /// own threshold to 0: autofocus enforces the threshold itself and fails the
    /// run, so a poor fit normally arrives here as a failure, never as a
    /// success. When the gate is disabled the run succeeds anyway, and the row
    /// is marked low quality so a marginal curve is visible rather than
    /// silently equal to a good one.</para></summary>
    public static FilterFocusSweepResult BuildResult(
            string filter, AutoFocusResult? result, string? error, bool timedOut,
            int attempt, double temperatureC, string? focuserName, double rSquaredGate) {
        var row = new FilterFocusSweepResult {
            Filter = filter,
            Attempts = attempt,
            TemperatureC = temperatureC,
            FocuserName = focuserName
        };
        if (result == null) {
            row.Error = timedOut
                ? (error ?? "auto-focus timed out")
                : (error ?? "auto-focus produced no result");
            return row;
        }
        row.RSquared = result.RSquared;
        row.Method = result.Method;
        if (!result.Success) {
            row.Error = error ?? result.Error ?? "auto-focus failed";
            return row;
        }

        row.Measured = true;
        row.Position = result.FinalPosition;
        row.BestPosition = result.BestPosition;
        row.Hfr = result.FinalMeasuredHfr > 0 ? result.FinalMeasuredHfr : result.BestPredictedHfr;
        row.StarCount = result.FinalStarCount;
        row.MeasuredAtUtc = result.CompletedAt == default ? DateTime.UtcNow : result.CompletedAt;
        if (rSquaredGate > 0 && result.RSquared < rSquaredGate) {
            row.LowQuality = true;
            row.Warning = $"fit quality R2 {result.RSquared:F2} is below {rSquaredGate:F2}";
        }
        return row;
    }

    /// <summary>Retry a filter once, and only a real failure. A low-quality fit
    /// costs minutes to repeat for a marginal gain, and the tab can re-run it on
    /// request.</summary>
    public static bool ShouldRetry(FilterFocusSweepResult row, int attempt, int maxAttempts)
        => !row.Measured && attempt < maxAttempts;

    /// <summary>The filter every offset is measured against. The rig's
    /// configured reference wins when the run actually measured it, then a
    /// filter named L, then the first filter measured in wheel order. Null when
    /// nothing was measured, which is also what says there is nothing to
    /// apply.</summary>
    public static string? ResolveSweepReference(IEnumerable<FilterFocusSweepResult> results,
                                                string? configured) {
        var measured = results.Where(r => r.Measured).ToList();
        if (measured.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(configured)) {
            var hit = measured.FirstOrDefault(
                r => string.Equals(r.Filter, configured, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit.Filter;
        }
        var l = measured.FirstOrDefault(
            r => string.Equals(r.Filter, "L", StringComparison.OrdinalIgnoreCase));
        return (l ?? measured[0]).Filter;
    }

    /// <summary>Fill in each row's offset against the reference, for the tab to
    /// show BEFORE anything is written.
    ///
    /// <para>Mirrors <c>FilterFocusMath.RecomputeOffsets</c>, including its
    /// temperature gate, so the preview says what Apply will do. A row measured
    /// outside the tolerance keeps the delta visible but is marked not derived:
    /// an eight-filter run on a cooling night can put its late filters outside
    /// the window, and the honest presentation is to show the number and say it
    /// will not be stored.</para></summary>
    public static void DeriveOffsets(IEnumerable<FilterFocusSweepResult> results,
                                     string? reference, double tolC) {
        var list = results.ToList();
        foreach (var r in list) {
            r.Offset = null;
            r.OffsetDerived = false;
            r.OffsetWarning = null;
        }
        if (string.IsNullOrWhiteSpace(reference)) return;
        var refRow = list.FirstOrDefault(
            r => r.Measured && string.Equals(r.Filter, reference, StringComparison.OrdinalIgnoreCase));
        if (refRow == null) return;

        refRow.Offset = 0;
        refRow.OffsetDerived = true;
        foreach (var r in list) {
            if (!r.Measured || ReferenceEquals(r, refRow)) continue;
            r.Offset = r.Position - refRow.Position;
            bool tempOk = double.IsNaN(r.TemperatureC) || double.IsNaN(refRow.TemperatureC)
                          || Math.Abs(r.TemperatureC - refRow.TemperatureC) <= tolC;
            r.OffsetDerived = tempOk;
            if (!tempOk)
                r.OffsetWarning =
                    $"measured {Math.Abs(r.TemperatureC - refRow.TemperatureC):F1} C from the "
                    + $"reference filter, outside the {tolC:F1} C window";
        }
    }
}

public enum FilterFocusSweepState { Idle, Running }

public class FilterFocusSweepRequest {
    /// <summary>Effective filter names to measure. Null or empty means every
    /// filter on the wheel.</summary>
    public List<string>? Filters { get; set; }
    /// <summary>Keep the results of filters this run does not cover. What the
    /// tab's "re-run failed" uses.</summary>
    public bool Merge { get; set; }
}

public record FilterFocusSweepProgress {
    public DateTime StartedAt { get; init; }
    public int TotalFilters { get; init; }
    public int CurrentFilterIndex { get; init; } = -1;
    public string CurrentFilter { get; init; } = "";
    /// <summary>idle | starting | switching | focusing | retrying | parking |
    /// done | aborted | failed.</summary>
    public string Phase { get; init; } = "idle";
    /// <summary>Which attempt of the current filter is running, 1 or 2.</summary>
    public int Attempt { get; init; }
    /// <summary>The filters this run walks, in wheel order.</summary>
    public List<string> Filters { get; init; } = new();
    public List<FilterFocusSweepResult> Results { get; init; } = new();
    /// <summary>What the offsets are measured against. Starts as the rig's
    /// configured reference, which may be null, and is resolved once something
    /// has been measured.</summary>
    public string? ReferenceFilter { get; init; }
}

public class FilterFocusSweepResult {
    public string Filter { get; set; } = "";
    /// <summary>False when every attempt failed; <see cref="Error"/> says why.</summary>
    public bool Measured { get; set; }
    /// <summary>Measured, but the fit is worse than the quality gate.</summary>
    public bool LowQuality { get; set; }
    /// <summary>Focuser steps the successful run finished on. This, rather than
    /// the fitted vertex, is what a single autofocus run records.</summary>
    public int Position { get; set; }
    /// <summary>The fitted vertex, kept so the tab can show the refinement
    /// delta.</summary>
    public int BestPosition { get; set; }
    public double? Hfr { get; set; }
    public double? RSquared { get; set; }
    /// <summary>Stars in the confirmation frame. Null when the run took no
    /// confirmation frame.</summary>
    public int? StarCount { get; set; }
    public double TemperatureC { get; set; } = double.NaN;
    /// <summary>The focuser that measured it: a different focuser invalidates
    /// an absolute position, which is why the memory keeps the name.</summary>
    public string? FocuserName { get; set; }
    public string? Method { get; set; }
    public int Attempts { get; set; }
    public DateTime? MeasuredAtUtc { get; set; }
    public string? Error { get; set; }
    /// <summary>Set when the row is usable but worth a second look.</summary>
    public string? Warning { get; set; }
    /// <summary>Steps against the reference filter, for display before Apply.</summary>
    public int? Offset { get; set; }
    /// <summary>False when Apply will NOT store this offset, with
    /// <see cref="OffsetWarning"/> saying why.</summary>
    public bool OffsetDerived { get; set; }
    public string? OffsetWarning { get; set; }
    /// <summary>Set once this filter has been written into the rig.</summary>
    public bool Applied { get; set; }
}

public record FilterFocusSweepApplyOutcome(
    List<string> Applied, Dictionary<string, int> Offsets, string? Reference);
