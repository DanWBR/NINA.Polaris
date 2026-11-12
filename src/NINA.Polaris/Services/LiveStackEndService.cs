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

/// <summary>What to do when a LIVE session ends. Saved on the rig.</summary>
public class LiveStackEndActions {
    /// <summary>Write the stack as a MASTER into integrated/{target}/ first.
    /// On by default: a host that powers off loses a stack held only in RAM.</summary>
    public bool SaveStack { get; set; } = true;
    public bool StopCapture { get; set; }
    public bool StopGuiding { get; set; }
    public bool ParkMount { get; set; }
    public bool HomeMount { get; set; }
    public bool StopTracking { get; set; }
    public bool WarmCamera { get; set; }
    public bool ShutdownHost { get; set; }

    public RigTeardownOptions ToTeardown() => new() {
        StopCapture = StopCapture, StopGuiding = StopGuiding,
        ParkMount = ParkMount, HomeMount = HomeMount, StopTracking = StopTracking,
        WarmCamera = WarmCamera, ShutdownHost = ShutdownHost
    };
}

/// <summary>
/// Ends a LIVE session the way the rig asks: when the stack reaches its
/// duration limit (<see cref="LiveStackingService.MaxDurationSeconds"/>), or
/// when the target SNR stops the capture, it saves the stack and runs the
/// rig's <see cref="LiveStackEndActions"/> (stop capture, home or park, warm
/// the camera, power off the host).
///
/// <para>Once per stack: a Reset starts a new one. Only for a stack fed by the
/// LIVE capture loop; an AUTORUN or PLAN run that feeds the stack has its own
/// end actions, and those run instead.</para>
///
/// <para>It also hands the rig's saved duration to the stacker at start-up and
/// on a rig switch. Only the LIVE tab used to set it, so after a restart the
/// limit shown on the tab was not the one in force.</para>
/// </summary>
public sealed class LiveStackEndService : BackgroundService {
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly LiveStackingService _stack;
    private readonly LiveCaptureService _capture;
    private readonly LiveStackWatchdogService _watchdog;
    private readonly ProfileService _profiles;
    private readonly RigTeardownService _teardown;
    private readonly ImageWriterService _writer;
    private readonly NotificationService _notify;
    private readonly ILogger<LiveStackEndService> _logger;

    // The stack the end already ran for, by its start time. A Reset gives the
    // next stack a new one.
    private DateTime? _endedStack;
    private int _running;

    public LiveStackEndService(LiveStackingService stack, LiveCaptureService capture,
            LiveStackWatchdogService watchdog, ProfileService profiles, RigTeardownService teardown,
            ImageWriterService writer, NotificationService notify, ILogger<LiveStackEndService> logger) {
        _stack = stack;
        _capture = capture;
        _watchdog = watchdog;
        _profiles = profiles;
        _teardown = teardown;
        _writer = writer;
        _notify = notify;
        _logger = logger;
        ApplyRigDuration();
        _profiles.EquipmentProfileActivated += _ => ApplyRigDuration();
        _watchdog.TargetSnrReached += () => Fire("the stack reached its target SNR", targetSnr: true);
    }

    private void ApplyRigDuration() {
        try {
            var secs = _profiles.ActiveEquipmentProfile?.LiveStackMaxDurationSeconds ?? 0;
            _stack.MaxDurationSeconds = Math.Max(0, secs);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Could not apply the rig's live stack duration");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        while (!stoppingToken.IsCancellationRequested) {
            try {
                if (_stack.MaxDurationSeconds > 0 && _stack.DurationCapReached)
                    Fire($"the stack reached its {FormatMinutes(_stack.MaxDurationSeconds)} limit");
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Live stack end check failed (non-fatal)");
            }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal static string FormatMinutes(int seconds) {
        var m = (int)Math.Round(seconds / 60.0);
        return m >= 60 && m % 60 == 0 ? $"{m / 60} h" : m >= 60 ? $"{m / 60} h {m % 60} min" : $"{m} min";
    }

    /// <summary>Whether a session end should run the rig's actions: once per
    /// stack, and only while the LIVE capture loop feeds the stack (or just
    /// stopped it, which is how the target SNR ends a session).</summary>
    internal static bool ShouldRun(DateTime? stackStart, DateTime? alreadyEnded, bool liveLoopFed,
            LiveStackEndActions? actions) =>
        stackStart != null && stackStart != alreadyEnded && liveLoopFed
        && actions != null && (actions.SaveStack || actions.ToTeardown().Any);

    private void Fire(string why, bool targetSnr = false) {
        var started = _stack.StartedAt;
        var actions = _profiles.ActiveEquipmentProfile?.LiveStackEndActions;
        // The target SNR stops the loop itself just before raising its event,
        // so "fed by LIVE" there means "was running a moment ago".
        bool liveFed = _capture.IsRunning || targetSnr;
        if (!ShouldRun(started, _endedStack, liveFed, actions)) return;
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        _endedStack = started;
        _ = Task.Run(async () => {
            try { await RunAsync(actions!, why); }
            catch (Exception ex) { _logger.LogWarning(ex, "Live stack end actions failed"); }
            finally { Interlocked.Exchange(ref _running, 0); }
        });
    }

    private async Task RunAsync(LiveStackEndActions actions, string why) {
        _logger.LogInformation("Live stack session ended: {Why}; running the end actions", why);
        var teardown = actions.ToTeardown();
        // Stop first so the saved master and the rig moves see no new frames.
        if (teardown.NeedsCaptureStopped && _capture.IsRunning) _capture.Stop();

        string? savedTo = null;
        if (actions.SaveStack) {
            try {
                var image = _stack.GetCurrentStackImage();
                if (image != null) {
                    var name = image.MetaData?.Target?.Name;
                    savedTo = _writer.SaveImage(image, targetName: string.IsNullOrWhiteSpace(name) ? "live-stack" : name,
                        imageType: "MASTER", gain: 0, stacked: true);
                    _logger.LogInformation("Live stack end: stack saved to {Path}", savedTo ?? "(no output folder)");
                }
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Live stack end: saving the stack failed");
                _notify.Push("warn", $"Live stack end: the stack could not be saved ({ex.Message}).", 10000);
            }
        }

        var summary = $"Live stack ended: {why}."
            + (savedTo != null ? " Stack saved." : "")
            + (teardown.ShutdownHost ? " Powering the host off." : "");
        if (!teardown.Any) {
            _notify.Push("ok", summary, 10000);
            return;
        }
        await _teardown.RunAsync(teardown, "Live stack end",
            beforePowerOff: () => _notify.Push("ok", summary, 12000));
    }
}
