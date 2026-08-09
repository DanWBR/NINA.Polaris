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
/// Restarts guiding when the error runs away and stops coming back.
///
/// <para>Field report, SV503 in wind, 2026-08-07: guiding "swung up and down
/// and never came back to stability", and the fix each time was to stop and
/// start it by hand. <see cref="GuideRunawayDetector"/> is what recognises that
/// state, calibrated against that night's 32 guide logs; this is what does the
/// stopping and starting.</para>
///
/// <para>Restarting is enough because a fresh start re-locks on the star where
/// it is now, zeroing an error the loop was never going to close with its
/// per-correction limits in the way. Calibration is deliberately NOT redone,
/// which would take minutes and is not what is wrong.</para>
///
/// <para>The budget matters as much as the detection. A gust front lasting an
/// hour would otherwise produce an hour of restarts, each costing settle time,
/// which is worse than riding it out. After
/// <see cref="MaxRestartsPerHour"/> the guard gives up, says so, and leaves the
/// session alone for the operator to judge.</para>
/// </summary>
public sealed class GuideRunawayGuard : BackgroundService {

    private readonly ActiveGuiderProvider _guiders;
    private readonly ProfileService _profiles;
    private readonly NotificationService? _notify;
    private readonly ILogger<GuideRunawayGuard> _logger;

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

    /// <summary>The warning half. The guard restarts on collapse; this only
    /// says "worse than this session's normal for a while", which is the case
    /// the restart threshold deliberately does not cover.</summary>
    private readonly GuideDegradationTracker _degradation = new();

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
    public double LastTrendArcsecPerFrame { get; private set; }

    // ---- the warning, read by the status payload ----
    public bool Degraded => _degradation.Degraded;

    /// <summary>Frames written while degraded. Counted here rather than
    /// derived later because only the writer knows a frame was actually kept:
    /// the elapsed minutes do not say how many subs fell inside them.</summary>
    public int DegradedFrames { get; private set; }

    /// <summary>Total time spent degraded this session.</summary>
    public TimeSpan DegradedTotal { get; private set; }

    /// <summary>Called by the image writer for each frame it stamps.</summary>
    public void NoteDegradedFrame() => DegradedFrames++;
    public DateTime? DegradedSinceUtc => _degradation.DegradedSinceUtc;
    public double? BaselineRmsArcsec => _degradation.BaselineArcsec;
    public double CurrentRmsArcsec => _degradation.CurrentArcsec;

    public GuideRunawayGuard(ActiveGuiderProvider guiders, ProfileService profiles,
                             ILogger<GuideRunawayGuard> logger,
                             NotificationService? notify = null) {
        _guiders = guiders;
        _profiles = profiles;
        _logger = logger;
        _notify = notify;
    }

    private bool Enabled =>
        _profiles.ActiveEquipmentProfile?.GuideRunawayRestart ?? true;

    private double RmsThreshold =>
        _profiles.ActiveEquipmentProfile?.GuideRunawayRmsArcsec ?? 30.0;

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
                _logger.LogDebug(ex, "Runaway guard could not resolve the active guider");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ContinueWith(_ => { });
        }
        if (_hooked != null) _hooked.GuideStepReceived -= OnStep;
    }

    private void Reset() {
        lock (_gate) { _ra.Clear(); _dec.Clear(); _blank = 0; }
        // A new session or a backend switch: the old normal describes nothing.
        _degradation.Reset();
    }

    private void OnStep(GuideStep step) {
        if (!Enabled || step == null) return;
        // A dither is a deliberate jump, and settling after one is large and
        // alternating: exactly the shape being watched for, for a reason that
        // is not wind.
        if (step.Dither) { Reset(); lock (_gate) { _blank = BlankAfterRestart; } return; }

        GuideRunawayDetector.Verdict verdict;
        lock (_gate) {
            if (_blank > 0) { _blank--; return; }
            if (_restarting) return;

            Push(_ra, step.RaArcsec);
            Push(_dec, step.DecArcsec);
            if (_ra.Count < WindowFrames) return;

            verdict = GuideRunawayDetector.JudgeWorst(
                _ra.ToArray(), _dec.ToArray(),
                minSamples: WindowFrames, rmsThresholdArcsec: RmsThreshold);
            // Feed the warning from the same window, so the two never disagree
            // about what the current error is.
            var wasDegraded = _degradation.Degraded;
            var wasSince = _degradation.DegradedSinceUtc;
            _degradation.Push(verdict.RmsArcsec, DateTime.UtcNow);
            if (wasDegraded && !_degradation.Degraded && wasSince != null) {
                // Bank the spell as it ends, so the session summary can report
                // total time rather than only whether it is bad right now.
                DegradedTotal += DateTime.UtcNow - wasSince.Value;
            }
            if (_degradation.Degraded && !wasDegraded) {
                _logger.LogWarning(
                    "Guiding has been {Factor:F1}x worse than this session's normal "
                    + "({Cur:F2}\" vs {Base:F2}\") for over two minutes. Not restarting: "
                    + "this is a heads-up, not a fault.",
                    _degradation.BaselineArcsec > 0
                        ? _degradation.CurrentArcsec / _degradation.BaselineArcsec.Value : 0,
                    _degradation.CurrentArcsec, _degradation.BaselineArcsec ?? 0);
                _notify?.Push("warn",
                    $"Guiding is running "
                    + $"{(_degradation.BaselineArcsec > 0 ? _degradation.CurrentArcsec / _degradation.BaselineArcsec.Value : 0):F1}x "
                    + $"worse than this session's normal. Frames are still being "
                    + $"saved and marked, nothing has been stopped.", 8000);
            }
            LastRmsArcsec = verdict.RmsArcsec;
            LastAlternation = verdict.AlternationRate;
            LastTrendArcsecPerFrame = verdict.TrendArcsecPerFrame;
            if (!verdict.RunAway) return;

            if (!TakeBudget()) {
                if (!BudgetExhausted) {
                    BudgetExhausted = true;
                    _logger.LogWarning(
                        "Guiding has run away again (RMS {Rms:F2}\") but the restart budget of "
                        + "{Max}/h is spent. Leaving it alone: past this point the settle time "
                        + "costs more frames than the weather does.",
                        verdict.RmsArcsec, MaxRestartsPerHour);
                }
                _ra.Clear(); _dec.Clear();
                return;
            }
            _restarting = true;
        }

        _ = RestartAsync(verdict);
    }

    /// <summary>One line on how guiding went, for the end of an unattended
    /// run. Null when there is nothing to report, so a clean night says
    /// nothing rather than saying "no problems", which is noise.
    ///
    /// <para>Frame count and elapsed time are both here on purpose: twenty
    /// minutes rough during a gap between targets costs nothing, and the same
    /// twenty minutes across eight subs costs eight subs.</para></summary>
    public string? SummariseSession() {
        var total = DegradedTotal;
        if (_degradation.Degraded && _degradation.DegradedSinceUtc is { } since)
            total += DateTime.UtcNow - since;

        if (total <= TimeSpan.Zero && RestartsThisSession == 0) return null;

        var parts = new List<string>();
        if (total > TimeSpan.Zero) {
            parts.Add($"guiding was rough for {total.TotalMinutes:F0} min");
            if (DegradedFrames > 0)
                parts.Add($"{DegradedFrames} frame(s) marked for review");
        }
        if (RestartsThisSession > 0)
            parts.Add($"{RestartsThisSession} automatic restart(s)");
        if (BudgetExhausted)
            parts.Add("the restart budget ran out");
        return string.Join(", ", parts) + ".";
    }

    /// <summary>Start a fresh accounting period. Called when a sequence run
    /// begins, so the summary describes THAT run and not the whole uptime of
    /// the host.</summary>
    public void BeginSession() {
        DegradedFrames = 0;
        DegradedTotal = TimeSpan.Zero;
        RestartsThisSession = 0;
        BudgetExhausted = false;
        _degradation.Reset();
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

    private async Task RestartAsync(GuideRunawayDetector.Verdict verdict) {
        var guider = _hooked;
        try {
            _logger.LogWarning(
                "Guide error has run away (RMS {Rms:F2}\", trend {Trend:+0.00;-0.00}\"/frame, "
                + "{Alt:P0} reversals). Stopping and restarting: it is not pulling back and a "
                + "fresh lock is what closes an error this size.",
                verdict.RmsArcsec, verdict.TrendArcsecPerFrame, verdict.AlternationRate);

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
            _logger.LogInformation("Guiding restarted after a runaway ({N} this session)",
                                   RestartsThisSession);
        } catch (Exception ex) {
            // A guard that throws must not take the session with it: the
            // operator still has a running sequence, just an unguided one, and
            // that is a strictly better place to be than a crashed host.
            _logger.LogError(ex, "Could not restart guiding after detecting a runaway");
        } finally {
            lock (_gate) {
                _ra.Clear(); _dec.Clear();
                _blank = BlankAfterRestart;
                _restarting = false;
            }
        }
    }
}
