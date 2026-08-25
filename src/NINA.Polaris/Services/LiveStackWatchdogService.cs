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
/// Watches the live-stack frame stream for two reactive behaviours:
/// <list type="bullet">
/// <item><b>Auto-stop at target</b>: when the rig opts in
/// (<see cref="LiveStackTriggers.AutoStopAtTargetSnr"/>) and the cumulative
/// stack SNR reaches the rig's Target SNR, stop the LIVE capture loop once.</item>
/// <item><b>Quality alerts</b>: run <see cref="LiveStackQualityAlerts"/> over the
/// timeline and surface a throttled toast when clouds or focus drift show up.</item>
/// </list>
/// Both react to state the stacker already computes, so this is a thin
/// subscriber; the frame handler is awaited inside AddFrameAsync.
/// </summary>
public sealed class LiveStackWatchdogService : System.IDisposable {
    private readonly LiveStackingService _stack;
    private readonly LiveCaptureService _capture;
    private readonly ProfileService _profiles;
    private readonly NotificationService _notify;
    private readonly ILogger<LiveStackWatchdogService> _logger;
    private readonly IDisposable _frameSub;

    private static readonly System.TimeSpan AlertThrottle = System.TimeSpan.FromMinutes(2);
    private int _lastSeenFrame;
    private bool _autoStopped;
    private System.DateTime _lastAlertAt = System.DateTime.MinValue;
    private LiveStackQualityAlerts.Kind _lastAlertKind = LiveStackQualityAlerts.Kind.None;

    public LiveStackWatchdogService(LiveStackingService stack,
                                    LiveCaptureService capture,
                                    ProfileService profiles,
                                    NotificationService notify,
                                    ILogger<LiveStackWatchdogService> logger) {
        _stack = stack;
        _capture = capture;
        _profiles = profiles;
        _notify = notify;
        _logger = logger;
        _frameSub = _stack.SubscribeFrameIntegrated(OnFrameIntegratedAsync);
        _profiles.EquipmentProfileActivated += _ => ResetState();
    }

    public void ResetState() {
        _lastSeenFrame = 0;
        _autoStopped = false;
        _lastAlertAt = System.DateTime.MinValue;
        _lastAlertKind = LiveStackQualityAlerts.Kind.None;
    }

    private Task OnFrameIntegratedAsync(LiveStackFrameInfo info) {
        try {
            if (info.FrameCount <= _lastSeenFrame) ResetState();
            _lastSeenFrame = info.FrameCount;

            var cfg = _profiles.ActiveEquipmentProfile?.LiveStackTriggers;
            if (cfg == null) return Task.CompletedTask;

            // Auto-stop at target SNR (once per session).
            if (!_autoStopped && cfg.AutoStopAtTargetSnr
                && _stack.TargetSnr is > 0
                && _stack.CumulativeSnr >= _stack.TargetSnr.Value
                && _capture.IsRunning) {
                _autoStopped = true;
                _capture.Stop();
                _notify.Push("ok",
                    $"Live stack reached target SNR {_stack.TargetSnr.Value:0.#} — capture stopped.", 8000);
                _logger.LogInformation("Live-stack watchdog: target SNR {Target:0.#} reached at frame {Frame}, capture stopped.",
                    _stack.TargetSnr.Value, info.FrameCount);
            }

            // Quality alerts (clouds / focus drift), throttled per kind.
            var alert = LiveStackQualityAlerts.Analyze(_stack.QualityHistory);
            if (alert.Kind != LiveStackQualityAlerts.Kind.None) {
                bool newKind = alert.Kind != _lastAlertKind;
                if (newKind || System.DateTime.UtcNow - _lastAlertAt >= AlertThrottle) {
                    _lastAlertAt = System.DateTime.UtcNow;
                    _lastAlertKind = alert.Kind;
                    _notify.Push("warn", alert.Message, 6000);
                }
            } else {
                _lastAlertKind = LiveStackQualityAlerts.Kind.None;
            }
        } catch (System.Exception ex) {
            _logger.LogWarning(ex, "Live-stack watchdog: frame handler failed (non-fatal)");
        }
        return Task.CompletedTask;
    }

    public void Dispose() => _frameSub?.Dispose();
}
