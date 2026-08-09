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

namespace NINA.Polaris.Services;

/// <summary>
/// Restarts guiding when it falls into wind-driven runaway oscillation.
///
/// <para>Field report, SV503 in wind, 2026-08-07: guiding "swung up and down
/// and never came back to stability", and the fix each time was to stop and
/// start it by hand. <see cref="GuideOscillationDetector"/> is what recognises
/// that state; this is what does the stopping and starting.</para>
///
/// <para>Restarting is enough because the oscillation lives in the guider's
/// own feedback loop, not in the mount: a fresh start drops the accumulated
/// history and the loop re-converges from the current position. Calibration is
/// deliberately NOT redone, which would take minutes and is not what is
/// wrong.</para>
///
/// <para>The budget matters as much as the detection. A gust front lasting an
/// hour would otherwise produce an hour of restarts, each costing settle time,
/// which is worse than riding it out. After
/// <see cref="MaxRestartsPerHour"/> the guard gives up, says so, and leaves the
/// session alone for the operator to judge.</para>
/// </summary>
public sealed class GuideOscillationGuard : BackgroundService {

    private readonly ActiveGuiderProvider _guiders;
    private readonly ProfileService _profiles;
    private readonly ILogger<GuideOscillationGuard> _logger;

    /// <summary>Frames kept per axis. Long enough that a single gust cannot
    /// fill it, short enough that the guard reacts inside a sub rather than
    /// after it.</summary>
    private const int WindowFrames = 12;

    /// <summary>Restarting costs settle time, so there is a point past which
    /// riding out the wind loses fewer frames than fighting it.</summary>
    private const int MaxRestartsPerHour = 4;

    /// <summary>Frames to ignore after a restart. Settling is large and
    /// alternating by nature and would read as the very thing we just fixed.</summary>
    private const int BlankAfterRestart = 20;

    private readonly object _gate = new();
    private readonly Queue<double> _ra = new();
    private readonly Queue<double> _dec = new();
    private readonly Queue<DateTime> _restarts = new();
    private int _blank;
    private bool _restarting;
    private IGuider? _hooked;

    // ---- state the status payload reads ----
    public DateTime? LastRestartUtc { get; private set; }
    public int RestartsThisSession { get; private set; }
    public bool BudgetExhausted { get; private set; }
    public double LastRmsArcsec { get; private set; }
    public double LastAlternation { get; private set; }

    public GuideOscillationGuard(ActiveGuiderProvider guiders, ProfileService profiles,
                                 ILogger<GuideOscillationGuard> logger) {
        _guiders = guiders;
        _profiles = profiles;
        _logger = logger;
    }

    private bool Enabled =>
        _profiles.ActiveEquipmentProfile?.GuideOscillationRestart ?? true;

    private double RmsThreshold =>
        _profiles.ActiveEquipmentProfile?.GuideOscillationRmsArcsec ?? 2.0;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        // The active guider can change (PHD2 <-> native) when the operator
        // switches backends, so re-hook rather than subscribing once at
        // construction: a guard listening to the backend nobody is using is
        // indistinguishable from a guard that is switched off.
        while (!stoppingToken.IsCancellationRequested) {
            try {
                var active = _guiders.Active;
                if (!ReferenceEquals(active, _hooked)) {
                    if (_hooked != null) _hooked.GuideStepReceived -= OnStep;
                    _hooked = active;
                    if (_hooked != null) _hooked.GuideStepReceived += OnStep;
                    Reset();
                }
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Oscillation guard could not resolve the active guider");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ContinueWith(_ => { });
        }
        if (_hooked != null) _hooked.GuideStepReceived -= OnStep;
    }

    private void Reset() {
        lock (_gate) { _ra.Clear(); _dec.Clear(); _blank = 0; }
    }

    private void OnStep(GuideStep step) {
        if (!Enabled || step == null) return;
        // A dither is a deliberate jump, and settling after one is large and
        // alternating: exactly the shape being watched for, for a reason that
        // is not wind.
        if (step.Dither) { Reset(); lock (_gate) { _blank = BlankAfterRestart; } return; }

        GuideOscillationDetector.Verdict verdict;
        lock (_gate) {
            if (_blank > 0) { _blank--; return; }
            if (_restarting) return;

            Push(_ra, step.RaArcsec);
            Push(_dec, step.DecArcsec);
            if (_ra.Count < WindowFrames) return;

            verdict = GuideOscillationDetector.JudgeWorst(
                _ra.ToArray(), _dec.ToArray(),
                minSamples: WindowFrames, rmsThresholdArcsec: RmsThreshold);
            LastRmsArcsec = verdict.RmsArcsec;
            LastAlternation = verdict.AlternationRate;
            if (!verdict.Oscillating) return;

            if (!TakeBudget()) {
                if (!BudgetExhausted) {
                    BudgetExhausted = true;
                    _logger.LogWarning(
                        "Guiding is oscillating again (RMS {Rms:F2}\", {Alt:P0} reversals) but "
                        + "the restart budget of {Max}/h is spent. Leaving it alone: past this "
                        + "point the settle time costs more frames than the wind does.",
                        verdict.RmsArcsec, verdict.AlternationRate, MaxRestartsPerHour);
                }
                _ra.Clear(); _dec.Clear();
                return;
            }
            _restarting = true;
        }

        _ = RestartAsync(verdict);
    }

    private static void Push(Queue<double> q, double v) {
        q.Enqueue(v);
        while (q.Count > WindowFrames) q.Dequeue();
    }

    /// <summary>Spend one restart if the last hour has room. Called under the
    /// lock.</summary>
    private bool TakeBudget() {
        var now = DateTime.UtcNow;
        while (_restarts.Count > 0 && (now - _restarts.Peek()).TotalHours >= 1)
            _restarts.Dequeue();
        if (_restarts.Count >= MaxRestartsPerHour) return false;
        _restarts.Enqueue(now);
        return true;
    }

    private async Task RestartAsync(GuideOscillationDetector.Verdict verdict) {
        var guider = _hooked;
        try {
            _logger.LogWarning(
                "Guiding is oscillating (RMS {Rms:F2}\", {Alt:P0} of frames reverse sign). "
                + "Stopping and restarting: the loop is chasing itself and does not recover "
                + "on its own.",
                verdict.RmsArcsec, verdict.AlternationRate);

            if (guider != null) {
                await guider.StopAsync();
                // Let the tube settle before handing the loop a fresh start. A
                // restart into the same gust just re-enters the oscillation,
                // and the budget above would then be spent in a minute.
                await Task.Delay(TimeSpan.FromSeconds(5));
                await guider.StartGuidingAsync();
            }

            LastRestartUtc = DateTime.UtcNow;
            RestartsThisSession++;
            _logger.LogInformation("Guiding restarted after oscillation ({N} this session)",
                                   RestartsThisSession);
        } catch (Exception ex) {
            // A guard that throws must not take the session with it: the
            // operator still has a running sequence, just an unguided one, and
            // that is a strictly better place to be than a crashed host.
            _logger.LogError(ex, "Could not restart guiding after detecting oscillation");
        } finally {
            lock (_gate) {
                _ra.Clear(); _dec.Clear();
                _blank = BlankAfterRestart;
                _restarting = false;
            }
        }
    }
}
