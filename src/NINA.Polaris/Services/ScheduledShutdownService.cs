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

using Microsoft.Extensions.DependencyInjection;
using NINA.Polaris.Services.Plan;

namespace NINA.Polaris.Services;

/// <summary>
/// Fires a rig teardown at a scheduled wall-clock time — the "fell asleep, forgot
/// the rig is running" safety net. When the time arrives it stops capture, stops
/// guiding, parks the mount, gently warms the camera and turns cooling off, and
/// optionally powers the host down.
///
/// The schedule persists on the <see cref="UserProfile"/> so it survives an app
/// restart, and it is a ONE-SHOT: it clears itself after firing (or is cancelled
/// from the UI). Services are resolved from the provider at fire time so this
/// orchestrator doesn't pull the whole capture/guide/power graph into its own
/// constructor.
/// </summary>
public sealed class ScheduledShutdownService : BackgroundService {
    private readonly IServiceProvider _sp;
    private readonly ProfileService _profiles;
    private readonly NotificationService _notify;
    private readonly ILogger<ScheduledShutdownService> _logger;
    private readonly object _gate = new();

    public DateTime? ScheduledUtc { get; private set; }
    public bool ShutdownHost { get; private set; }
    public bool Running { get; private set; }
    public string? LastResult { get; private set; }

    public ScheduledShutdownService(IServiceProvider sp, ProfileService profiles,
            NotificationService notify, ILogger<ScheduledShutdownService> logger) {
        _sp = sp;
        _profiles = profiles;
        _notify = notify;
        _logger = logger;
        // Re-arm from the persisted schedule (survives a restart).
        var p = _profiles.Active;
        if (p.ScheduledShutdownUtc is { } u) {
            ScheduledUtc = DateTime.SpecifyKind(u, DateTimeKind.Utc);
            ShutdownHost = p.ScheduledShutdownHost;
        }
    }

    /// <summary>Arm (or replace) the schedule. <paramref name="utc"/> must be in
    /// the future; a past time is rejected so an accidental stale value can't
    /// tear the rig down the instant it's set.</summary>
    public bool Arm(DateTime utc, bool shutdownHost) {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (utc <= DateTime.UtcNow.AddSeconds(5)) return false;
        lock (_gate) { ScheduledUtc = utc; ShutdownHost = shutdownHost; }
        _profiles.UpdateSettings(p => { p.ScheduledShutdownUtc = utc; p.ScheduledShutdownHost = shutdownHost; });
        _logger.LogInformation("Scheduled shutdown armed for {Utc} (host={Host})", utc, shutdownHost);
        return true;
    }

    public void Cancel() {
        lock (_gate) { ScheduledUtc = null; }
        _profiles.UpdateSettings(p => { p.ScheduledShutdownUtc = null; });
        _logger.LogInformation("Scheduled shutdown cancelled");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        while (!stoppingToken.IsCancellationRequested) {
            try {
                DateTime? due;
                lock (_gate) due = ScheduledUtc;
                if (!Running && due is { } d && DateTime.UtcNow >= d) {
                    await RunSequenceAsync(stoppingToken);
                }
            } catch (Exception ex) {
                _logger.LogError(ex, "Scheduled-shutdown tick failed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task RunSequenceAsync(CancellationToken ct) {
        bool host;
        lock (_gate) { Running = true; host = ShutdownHost; ScheduledUtc = null; }
        // Clear the persisted schedule up front so a crash mid-teardown doesn't
        // re-fire it on the next boot.
        _profiles.UpdateSettings(p => { p.ScheduledShutdownUtc = null; });
        _notify.Push("info", "Scheduled shutdown reached — tearing the rig down.", 8000);
        _logger.LogInformation("Scheduled shutdown firing (host={Host})", host);

        // Each step is best-effort and isolated: one failure must not stop the
        // rest (a parked mount still matters even if cooling-off failed).
        await Step("stop capture", StopCaptureAsync, ct);
        await Step("stop guiding", StopGuidingAsync, ct);
        await Step("park mount", ParkMountAsync, ct);
        await Step("warm + cooler off", () => WarmAndCoolerOffAsync(host, ct), ct);

        LastResult = $"Rig shut down at {DateTime.UtcNow:HH:mm} UTC" + (host ? " (host powering off)" : "");
        _notify.Push("ok", LastResult, 10000);

        if (host) {
            try {
                var power = _sp.GetService<PowerService>();
                var r = power?.ScheduleShutdown();
                _logger.LogInformation("Scheduled shutdown: host power-off requested ({Ok})", r?.Ok);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Scheduled shutdown: host power-off failed");
            }
        }
        lock (_gate) Running = false;
    }

    private async Task Step(string name, Func<Task> action, CancellationToken ct) {
        try { await action(); }
        catch (Exception ex) {
            _logger.LogWarning(ex, "Scheduled shutdown step '{Step}' failed", name);
            _notify.Push("warn", $"Scheduled shutdown: '{name}' failed ({ex.Message})", 8000);
        }
    }

    private Task StopCaptureAsync() {
        try { _sp.GetService<SequenceEngine>()?.Stop(); } catch { }
        try { _sp.GetService<PlanRunnerService>()?.StopPlan(); } catch { }
        try { _sp.GetService<LiveCaptureService>()?.Stop(); } catch { }
        var cam = _sp.GetService<EquipmentManager>()?.Camera;
        if (cam is { IsConnected: true }) { try { return cam.AbortExposureAsync(); } catch { } }
        return Task.CompletedTask;
    }

    private async Task StopGuidingAsync() {
        var g = _sp.GetService<ActiveGuiderProvider>()?.Active;
        if (g != null) await g.StopAsync();
    }

    private async Task ParkMountAsync() {
        var scope = _sp.GetService<EquipmentManager>()?.Telescope;
        if (scope is { IsConnected: true }) await scope.ParkAsync();
    }

    private async Task WarmAndCoolerOffAsync(bool waitForWarm, CancellationToken ct) {
        var equip = _sp.GetService<EquipmentManager>();
        var ramp = _sp.GetService<CoolingRampService>();
        var cam = equip?.Camera;
        if (cam is not { IsConnected: true }) return;
        // Gentle warm-up (ramp the setpoint up, then cooler off) rather than an
        // abrupt cut — protects the sensor from thermal shock/condensation.
        if (ramp != null) {
            var rate = _profiles.ActiveEquipmentProfile.CoolerRampDegPerMinute ?? 2.0;
            ramp.Start(cam, targetC: 15.0, ratePerMinute: rate <= 0 ? 2.0 : rate,
                       coolerOnFirst: false, coolerOffWhenDone: true, source: "scheduled-shutdown");
            // Only wait for the ramp when we're about to cut host power — otherwise
            // let it finish in the background so the teardown returns promptly.
            if (waitForWarm) {
                var deadline = DateTime.UtcNow.AddMinutes(20);
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested) {
                    if (ramp.Snapshot()?.Running != true) break;
                    await Task.Delay(2000, ct);
                }
            }
        } else {
            try { await cam.SetCoolerAsync(false, ct); } catch { }
        }
    }
}
