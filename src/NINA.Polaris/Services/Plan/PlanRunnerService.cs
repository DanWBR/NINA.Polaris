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
    private readonly ILogger<PlanRunnerService> _logger;

    private readonly object _lock = new();
    private ImagingPlan? _active;
    private DateTime? _startedAtUtc;
    private DateTime? _endsAtUtc;       // resolved end instant for Dawn / AtTime
    private Phase _phase = Phase.Idle;
    private bool _userAborted;

    private CancellationTokenSource? _cts;
    private Task? _monitor;

    private enum Phase { Idle, Main, Ending }

    public PlanRunnerService(AdvancedSequenceEngine engine, PlanCompilerService compiler,
        AltitudeService altitude, PowerService power, ILogger<PlanRunnerService> logger) {
        _engine = engine;
        _compiler = compiler;
        _altitude = altitude;
        _power = power;
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

            var doc = _compiler.Compile(plan);
            _engine.Load(doc);
            _engine.Start();
            if (_engine.State != AdvancedSequenceState.Running) {
                return (false, _engine.LastError ?? "The engine refused to start.");
            }

            _active = plan;
            _userAborted = false;
            _startedAtUtc = DateTime.UtcNow;
            _endsAtUtc = ResolveEnd(plan);
            _phase = Phase.Main;
            _logger.LogInformation("Plan '{Name}' started ({N} targets), ends {Ends}",
                plan.Name, plan.Targets.Count(t => t.Enabled),
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
            return new PlanStatus(
                Active: _active != null,
                PlanId: _active?.Id,
                PlanName: _active?.Name,
                Phase: _phase.ToString().ToLowerInvariant(),
                EngineState: _engine.State.ToString().ToLowerInvariant(),
                CurrentTarget: _active != null ? CurrentRunningName() : null,
                StartedAtUtc: _startedAtUtc,
                EndsAtUtc: _endsAtUtc);
        }
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
        _active = null;
        _phase = Phase.Idle;
        _endsAtUtc = null;
        _startedAtUtc = null;
        _userAborted = false;

        if (plan == null) return;
        _logger.LogInformation("Plan '{Name}' finished ({How})",
            plan.Name, aborted ? "stopped by user" : "completed");

        if (plan.EndShutdownHost && !aborted) {
            _logger.LogWarning("Plan '{Name}' requested host shutdown; powering off", plan.Name);
            try { _power.ScheduleShutdown(); }
            catch (Exception ex) { _logger.LogError(ex, "Host shutdown failed"); }
        }
    }

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
    DateTime? EndsAtUtc);
