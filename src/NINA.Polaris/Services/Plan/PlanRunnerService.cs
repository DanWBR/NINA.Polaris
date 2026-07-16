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

using NINA.Polaris.Services.Sequencer;
using NINA.Polaris.Services.Sequencer.Containers;
using NINA.Polaris.Services.Sequencer.Instructions;

namespace NINA.Polaris.Services.Plan;

/// <summary>
/// Drives a PLAN-mode run on top of the shared <see cref="AdvancedSequenceEngine"/>.
/// Responsibilities:
///   - compile + start a plan's main document on <see cref="StartPlan"/>;
///   - watch for the end condition (astronomical dawn / a set time) and stop the
///     engine when it's reached;
///   - once the main run ends (naturally, on the end condition, or on manual
///     stop), run the plan's end-actions document (warm + cooler off, park,
///     focuser → 0) so the rig is left safe even when the main run was cut short;
///   - power the host off afterwards when the plan asked for it and wasn't
///     manually aborted.
///
/// The engine is a single shared resource (the ADV tab uses it too), so PLAN
/// refuses to start while a sequence is already running, and vice-versa.
/// A persistent monitor loop (started with the host) polls the engine state.
/// </summary>
public class PlanRunnerService : IHostedService {
    private readonly AdvancedSequenceEngine _engine;
    private readonly PlanCompilerService _compiler;
    private readonly AltitudeService _altitude;
    private readonly PowerService _power;
    private readonly MeridianFlipService _flip;
    private readonly ILogger<PlanRunnerService> _logger;

    private readonly object _lock = new();
    private ImagingPlan? _active;
    private DateTime? _startedAtUtc;
    private DateTime? _endsAtUtc;       // resolved end instant for Dawn / AtTime
    private Phase _phase = Phase.Idle;
    private bool _userAborted;
    // FIELD7-3: did the MAIN run end in a failure (an instruction threw past its
    // error policy) rather than completing or being stopped? Captured at the
    // Main->end transition, because by FinishPlan the engine has run the
    // end-actions document too and its own status no longer reflects the main run.
    // Destructive end actions (host shutdown) must never fire on a failed run.
    private bool _mainRunFailed;
    private int _mainPlannedFrames;   // total light frames the main doc will capture

    private CancellationTokenSource? _cts;
    private Task? _monitor;

    // Resume stash: the main document (with its runtime statuses + per-
    // instruction frame counters) of a plan whose main phase ended EARLY
    // (user stop or end-time), kept so ResumePlan can pick up where it
    // stopped — completed targets skip, the interrupted one re-runs its
    // setup and fast-forwards past frames already captured. In-memory only;
    // cleared by a natural full completion or by starting another plan.
    private ImagingPlan? _resumePlan;
    private SequenceDocument? _resumeDoc;

    private enum Phase { Idle, Main, Ending }

    public PlanRunnerService(AdvancedSequenceEngine engine, PlanCompilerService compiler,
        AltitudeService altitude, PowerService power, MeridianFlipService flip,
        ILogger<PlanRunnerService> logger) {
        _engine = engine;
        _compiler = compiler;
        _altitude = altitude;
        _power = power;
        _flip = flip;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _monitor = Task.Run(() => MonitorLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        _cts?.Cancel();
        return _monitor ?? Task.CompletedTask;
    }

    /// <summary>Begin a plan. Returns (false, reason) when it can't start.</summary>
    public (bool ok, string? error) StartPlan(ImagingPlan plan) {
        lock (_lock) {
            if (_active != null)
                return (false, "A plan is already running. Stop it first.");
            if (_engine.State == AdvancedSequenceState.Running)
                return (false, "A sequence is already running (Advanced/Autorun). Stop it first.");
            if (plan.Targets.All(t => !t.Enabled))
                return (false, "The plan has no enabled targets.");

            // Apply the plan's meridian-flip tuning to the shared service the
            // MeridianFlipTrigger reads at runtime. Preserve the service's other
            // settings (pause-before, tolerance, settle, live-stack flag) and
            // only override the three knobs exposed in the PLAN panel.
            if (plan.AutoMeridianFlip) {
                var s = _flip.Settings;
                _flip.UpdateSettings(new MeridianFlipSettings {
                    Enabled = true,
                    MinutesAfterMeridian = plan.MeridianFlipMinutesAfter,
                    RecenterAfterFlip = plan.MeridianFlipRecenter,
                    AutoFocusAfterFlip = plan.MeridianFlipAutoFocus,
                    PauseBeforeMeridianMinutes = s.PauseBeforeMeridianMinutes,
                    RecenterToleranceArcsec = s.RecenterToleranceArcsec,
                    SettleSecondsAfterFlip = s.SettleSecondsAfterFlip,
                    AutoFlipDuringLiveStack = s.AutoFlipDuringLiveStack
                });
            }

            var doc = _compiler.Compile(plan);
            _mainPlannedFrames = CountFrames(doc.Root);
            _engine.Load(doc);
            _engine.Start();
            if (_engine.State != AdvancedSequenceState.Running) {
                return (false, _engine.LastError ?? "The engine refused to start.");
            }

            // A fresh plan supersedes any resumable leftovers.
            _resumePlan = null;
            _resumeDoc = null;

            _active = plan;
            _userAborted = false;
            _mainRunFailed = false;
            _startedAtUtc = DateTime.UtcNow;
            _endsAtUtc = ResolveEnd(plan);
            _phase = Phase.Main;
            _logger.LogInformation("Plan '{Name}' started ({N} targets), ends {Ends}",
                plan.Name, plan.Targets.Count(t => t.Enabled),
                _endsAtUtc?.ToString("u") ?? "when all frames are done");
            return (true, null);
        }
    }

    /// <summary>
    /// Resume the last prematurely-ended plan from its retained progress.
    /// Completed targets are skipped; the interrupted target re-runs its
    /// setup (slew / center / guide — the end actions may have parked the
    /// mount) and its exposure sets continue at the next frame. The end
    /// condition is re-resolved (Dawn = the coming dawn).
    /// </summary>
    public (bool ok, string? error) ResumePlan() {
        lock (_lock) {
            if (_active != null)
                return (false, "A plan is already running. Stop it first.");
            if (_engine.State == AdvancedSequenceState.Running)
                return (false, "A sequence is already running (Advanced/Autorun). Stop it first.");
            if (_resumePlan == null || _resumeDoc == null)
                return (false, "There is no interrupted plan to resume.");

            var plan = _resumePlan;
            if (plan.AutoMeridianFlip) {
                var s = _flip.Settings;
                _flip.UpdateSettings(new MeridianFlipSettings {
                    Enabled = true,
                    MinutesAfterMeridian = plan.MeridianFlipMinutesAfter,
                    RecenterAfterFlip = plan.MeridianFlipRecenter,
                    AutoFocusAfterFlip = plan.MeridianFlipAutoFocus,
                    PauseBeforeMeridianMinutes = s.PauseBeforeMeridianMinutes,
                    RecenterToleranceArcsec = s.RecenterToleranceArcsec,
                    SettleSecondsAfterFlip = s.SettleSecondsAfterFlip,
                    AutoFlipDuringLiveStack = s.AutoFlipDuringLiveStack
                });
            }

            _engine.LoadForResume(_resumeDoc);
            _engine.Start(resume: true);
            if (_engine.State != AdvancedSequenceState.Running) {
                return (false, _engine.LastError ?? "The engine refused to resume.");
            }

            _mainPlannedFrames = CountFrames(_resumeDoc.Root);
            _active = plan;
            _userAborted = false;
            _mainRunFailed = false;
            _startedAtUtc = DateTime.UtcNow;
            _endsAtUtc = ResolveEnd(plan);
            _phase = Phase.Main;
            _logger.LogInformation("Plan '{Name}' resumed ({Done}/{Total} frames already captured), ends {Ends}",
                plan.Name, CountDone(_resumeDoc.Root), _mainPlannedFrames,
                _endsAtUtc?.ToString("u") ?? "when all frames are done");
            return (true, null);
        }
    }

    /// <summary>Manually stop the active plan. End actions still run; host shutdown does not.</summary>
    public void StopPlan() {
        lock (_lock) {
            if (_active == null) return;
            _userAborted = true;
            _logger.LogInformation("Plan '{Name}' stop requested by user", _active.Name);
            _engine.Stop();
        }
    }

    public PlanStatus GetStatus() {
        lock (_lock) {
            // Progress: walk the live engine tree (Main phase) so the two
            // progress bars can show current-target + whole-plan completion.
            // The plan runs on AdvancedSequenceEngine, whose global frame
            // counter increments per captured light frame.
            int totalFrames = _mainPlannedFrames;
            int totalDone = 0, curDone = 0, curTotal = 0;
            if (_active != null) {
                if (_phase == Phase.Main) {
                    // Progress from the DOCUMENT TREE (per-instruction
                    // completed-frame counters), not the engine context's
                    // per-run counter: a resumed run's context starts at 0
                    // while the tree keeps the frames captured before the
                    // interruption, so the bars stay truthful across resume.
                    int fc = Math.Min(CountDone(_engine.Document.Root), totalFrames);
                    totalDone = fc;
                    if (_engine.Document.Root is SequenceContainer root) {
                        foreach (var item in root.Items) {
                            if (item is not DeepSkyObjectContainer dso) continue;
                            if (dso.Status == SequenceEntityStatus.Running) {
                                curTotal = CountFrames(dso);
                                curDone = CountDone(dso);
                            }
                        }
                    }
                } else if (_phase == Phase.Ending) {
                    // Main capture is over; show the imaging bars complete while
                    // the end-of-session actions document runs.
                    totalDone = totalFrames;
                    curDone = curTotal = 0;
                }
            }

            bool canResume = _active == null
                && _resumePlan != null && _resumeDoc != null
                && _engine.State == AdvancedSequenceState.Idle;

            return new PlanStatus(
                Active: _active != null,
                PlanId: _active?.Id,
                PlanName: _active?.Name,
                Phase: _phase.ToString().ToLowerInvariant(),
                EngineState: _engine.State.ToString().ToLowerInvariant(),
                CurrentTarget: _active != null ? CurrentRunningName() : null,
                StartedAtUtc: _startedAtUtc,
                EndsAtUtc: _endsAtUtc,
                CurrentItemCompleted: curDone,
                CurrentItemTotal: curTotal,
                TotalCompleted: totalDone,
                TotalFrames: totalFrames,
                CanResume: canResume,
                ResumePlanName: canResume ? _resumePlan!.Name : null,
                ResumeDoneFrames: canResume && _resumeDoc != null ? CountDone(_resumeDoc.Root) : 0,
                ResumeTotalFrames: canResume && _resumeDoc != null ? CountFrames(_resumeDoc.Root) : 0);
        }
    }

    /// <summary>Recursively sum the frame counts of every TakeExposure
    /// instruction under an entity (used for PLAN progress totals).</summary>
    private static int CountFrames(ISequenceEntity entity) {
        int sum = 0;
        if (entity is TakeExposureInstruction tx) sum += Math.Max(0, tx.Count);
        if (entity is SequenceContainer c) {
            foreach (var child in c.Items) sum += CountFrames(child);
        }
        return sum;
    }

    /// <summary>Frames already captured, summed from each TakeExposure's
    /// retained progress counter (survives stop + resume).</summary>
    private static int CountDone(ISequenceEntity entity) {
        int sum = 0;
        if (entity is TakeExposureInstruction tx)
            sum += Math.Clamp(tx.CompletedCount, 0, Math.Max(0, tx.Count));
        if (entity is SequenceContainer c) {
            foreach (var child in c.Items) sum += CountDone(child);
        }
        return sum;
    }

    // ---- internals ----

    private async Task MonitorLoopAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try {
                Tick();
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Plan monitor tick failed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick() {
        lock (_lock) {
            if (_active == null) return;

            // 1) End-condition check during the main run.
            if (_phase == Phase.Main && _endsAtUtc.HasValue
                && DateTime.UtcNow >= _endsAtUtc.Value
                && _engine.State == AdvancedSequenceState.Running) {
                _logger.LogInformation("Plan '{Name}' end time reached ({Ends:u}); stopping",
                    _active.Name, _endsAtUtc.Value);
                _engine.Stop();
                // Fall through; engine becomes Idle on a later tick.
            }

            // 2) React to the engine returning to idle.
            if (_engine.State != AdvancedSequenceState.Idle) return;

            if (_phase == Phase.Main) {
                // Capture the main run's outcome NOW, before the end-actions
                // document replaces it on the engine. State is Idle for a normal
                // finish, a user stop AND a failure — LastRunFailed is the only
                // thing that separates a crash from the other two, and it's about
                // to become unreadable once end actions load. A transient BLOB
                // timeout during a driver restart reaches here as a failure; without
                // this capture it was indistinguishable from a clean completion, and
                // the plan went on to run end actions and power off the host while
                // the watchdog was mid-recovery. (See FIELD7-3.)
                _mainRunFailed = _engine.LastRunFailed;
                if (_mainRunFailed) {
                    _logger.LogWarning(
                        "Plan '{Name}' main run FAILED ({Err}); end actions will run but host " +
                        "shutdown is suppressed", _active.Name, _engine.LastError ?? "unknown");
                }

                // Main phase over. If it ended EARLY (user stop / end-time)
                // with partial progress, stash the main document BEFORE the
                // end-actions document replaces it on the engine, so the
                // user can resume tomorrow evening (or after the stop) from
                // exactly where it left off. A natural full completion has
                // nothing to resume and clears any older stash.
                if (_engine.HasResumableProgress) {
                    _resumePlan = _active;
                    _resumeDoc = _engine.Document;
                    _logger.LogInformation(
                        "Plan '{Name}' main phase ended with partial progress; resume is available",
                        _active.Name);
                } else {
                    _resumePlan = null;
                    _resumeDoc = null;
                }

                var endDoc = _compiler.CompileEndActions(_active);
                if (endDoc != null) {
                    _phase = Phase.Ending;
                    try {
                        _engine.Load(endDoc);
                        _engine.Start();
                        _logger.LogInformation("Plan '{Name}' main run finished; running end actions", _active.Name);
                        return;
                    } catch (Exception ex) {
                        _logger.LogWarning(ex, "Could not start end-actions document; finishing plan");
                    }
                }
                FinishPlan();
            } else if (_phase == Phase.Ending) {
                FinishPlan();
            }
        }
    }

    private void FinishPlan() {
        var plan = _active;
        var aborted = _userAborted;
        var failed = _mainRunFailed;
        _active = null;
        _phase = Phase.Idle;
        _endsAtUtc = null;
        _startedAtUtc = null;
        _userAborted = false;
        _mainRunFailed = false;
        _mainPlannedFrames = 0;

        if (plan == null) return;

        var how = aborted ? "stopped by user" : failed ? "FAILED" : "completed";
        _logger.LogInformation("Plan '{Name}' finished ({How})", plan.Name, how);

        if (plan.EndShutdownHost) {
            if (ShouldShutdownHost(true, aborted, failed)) {
                _logger.LogWarning("Plan '{Name}' requested host shutdown; powering off", plan.Name);
                try { _power.ScheduleShutdown(); }
                catch (Exception ex) { _logger.LogError(ex, "Host shutdown failed"); }
            } else {
                _logger.LogWarning(
                    "Plan '{Name}' requested host shutdown, but the run {How} — NOT powering off " +
                    "so the session can be recovered", plan.Name, how);
            }
        }
    }

    /// <summary>FIELD7-3: whether a plan's "shut down the host at the end" action
    /// may fire. Only a run that COMPLETED NORMALLY qualifies — a user stop and a
    /// run FAILURE are both "did not finish as planned".
    ///
    /// The bug this pins: the old code gated only on <paramref name="userAborted"/>,
    /// so a FAILED run (e.g. one recoverable BLOB timeout during a driver restart)
    /// read as a completion and powered off the SBC mid-session while the watchdog
    /// was about to hand back a healthy camera. End-time expiry is deliberately NOT
    /// a failure (the engine reports Skipped, not Failed), so a genuine "image until
    /// dawn, then shut down" plan still shuts down.</summary>
    internal static bool ShouldShutdownHost(bool endShutdownHost, bool userAborted, bool mainRunFailed)
        => endShutdownHost && !userAborted && !mainRunFailed;

    /// <summary>Resolve the absolute UTC end instant for the plan, or null for AllDone.</summary>
    private DateTime? ResolveEnd(ImagingPlan plan) {
        switch (plan.EndMode) {
            case PlanEndMode.Dawn:
                return _altitude.ComputeNightWindow().AstronomicalDawnUtc;
            case PlanEndMode.AtTime:
                return NextUtcTimeOfDay(plan.EndAtUtc);
            default:
                return null;
        }
    }

    /// <summary>Next occurrence (today or tomorrow) of a "HH:mm[:ss]" UTC time of day.</summary>
    private static DateTime? NextUtcTimeOfDay(string hhmm) {
        if (!TimeSpan.TryParse(hhmm, out var tod)) return null;
        var now = DateTime.UtcNow;
        var target = now.Date + tod;
        if (target <= now) target = target.AddDays(1);
        return target;
    }

    /// <summary>Name of the currently-running top-level child (target) in the loaded document.</summary>
    private string? CurrentRunningName() {
        if (_engine.Document.Root is SequenceContainer root) {
            foreach (var item in root.Items) {
                if (item.Status == SequenceEntityStatus.Running) return item.Name;
            }
        }
        return null;
    }
}

public record PlanStatus(
    bool Active,
    string? PlanId,
    string? PlanName,
    string Phase,
    string EngineState,
    string? CurrentTarget,
    DateTime? StartedAtUtc,
    DateTime? EndsAtUtc,
    int CurrentItemCompleted = 0,
    int CurrentItemTotal = 0,
    int TotalCompleted = 0,
    int TotalFrames = 0,
    bool CanResume = false,
    string? ResumePlanName = null,
    int ResumeDoneFrames = 0,
    int ResumeTotalFrames = 0);
