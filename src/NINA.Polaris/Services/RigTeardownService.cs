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

/// <summary>What to do to the rig when a session ends. Every step is
/// optional; they run in the order of the properties.</summary>
public sealed record RigTeardownOptions {
    /// <summary>Stop every capture loop (AUTORUN, PLAN, LIVE) and abort the
    /// exposure in flight.</summary>
    public bool StopCapture { get; init; }
    public bool StopGuiding { get; init; }
    /// <summary>Park the mount. Takes the place of home and stop tracking.</summary>
    public bool ParkMount { get; init; }
    /// <summary>Send the mount to its home position.</summary>
    public bool HomeMount { get; init; }
    public bool StopTracking { get; init; }
    /// <summary>Ramp the camera up to ambient, then turn the cooler off.</summary>
    public bool WarmCamera { get; init; }
    /// <summary>Power the host off last. The warm-up is waited for first.</summary>
    public bool ShutdownHost { get; init; }

    public bool Any => StopCapture || StopGuiding || ParkMount || HomeMount
        || StopTracking || WarmCamera || ShutdownHost;

    /// <summary>A step that moves the mount or ends the host needs the capture
    /// stopped first: frames taken during the slew home are not wanted.</summary>
    public bool NeedsCaptureStopped => StopCapture || ParkMount || HomeMount || ShutdownHost;
}

/// <summary>
/// Runs a <see cref="RigTeardownOptions"/> on the rig. Shared by the scheduled
/// shutdown and the LIVE end actions, so "park, warm, power off" means the same
/// thing wherever it is offered.
///
/// <para>Each step is best-effort and isolated: one that fails is logged and
/// reported, and the next still runs (a parked mount still matters when the
/// cooler did not answer). Services are resolved at run time so this does not
/// pull the capture, guide and power graph into its constructor.</para>
/// </summary>
public sealed class RigTeardownService {
    private readonly IServiceProvider _sp;
    private readonly ProfileService _profiles;
    private readonly NotificationService _notify;
    private readonly ILogger<RigTeardownService> _logger;

    public RigTeardownService(IServiceProvider sp, ProfileService profiles,
            NotificationService notify, ILogger<RigTeardownService> logger) {
        _sp = sp;
        _profiles = profiles;
        _notify = notify;
        _logger = logger;
    }

    /// <summary>Run the steps. <paramref name="label"/> names the caller in the
    /// log and in a failure notice ("Scheduled shutdown", "Live stack end").
    /// Returns whether the host power-off was requested and accepted.
    /// <paramref name="beforePowerOff"/> runs after the rig steps and before
    /// the power-off request, for a closing notice that has to reach the
    /// clients while the host is still up.</summary>
    public async Task<bool> RunAsync(RigTeardownOptions o, string label, CancellationToken ct = default,
            Action? beforePowerOff = null) {
        if (!o.Any) return false;
        _logger.LogInformation("{Label}: tearing the rig down ({Options})", label, o);

        if (o.NeedsCaptureStopped) await Step(label, "stop capture", StopCaptureAsync);
        if (o.StopGuiding) await Step(label, "stop guiding", StopGuidingAsync);

        var scope = _sp.GetService<EquipmentManager>()?.Telescope;
        if (scope is { IsConnected: true }) {
            if (o.ParkMount) {
                await Step(label, "park mount", () => scope.ParkAsync(ct));
            } else {
                if (o.HomeMount) await Step(label, "send the mount home", () => HomeAsync(scope, label, ct));
                if (o.StopTracking) await Step(label, "stop tracking", () => scope.SetTrackingAsync(false, ct));
            }
        }

        if (o.WarmCamera) await Step(label, "warm + cooler off", () => WarmAndCoolerOffAsync(o.ShutdownHost, ct));

        try { beforePowerOff?.Invoke(); } catch (Exception ex) { _logger.LogDebug(ex, "{Label}: closing callback failed", label); }
        if (!o.ShutdownHost) return false;
        try {
            var r = _sp.GetService<PowerService>()?.ScheduleShutdown();
            if (r is { Ok: true }) {
                _logger.LogWarning("{Label}: host power-off requested", label);
                return true;
            }
            _logger.LogWarning("{Label}: host power-off refused: {Msg}", label, r?.Message);
            _notify.Push("warn", $"{label}: the host was not powered off ({r?.Message ?? "no power service"}).", 10000);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "{Label}: host power-off failed", label);
        }
        return false;
    }

    private async Task Step(string label, string name, Func<Task> action) {
        try { await action(); }
        catch (Exception ex) {
            _logger.LogWarning(ex, "{Label}: step '{Step}' failed", label, name);
            _notify.Push("warn", $"{label}: '{name}' failed ({ex.Message})", 8000);
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

    private async Task HomeAsync(NINA.Image.Interfaces.ITelescope scope, string label, CancellationToken ct) {
        if (!scope.Capabilities.SupportsFindHome) {
            _logger.LogWarning("{Label}: the mount does not report a home command; home skipped", label);
            return;
        }
        if (scope.IsParked) {
            _logger.LogWarning("{Label}: the mount is parked; home skipped", label);
            return;
        }
        await scope.FindHomeAsync(ct);
        await Sequencer.Instructions.MountWaits.UntilAsync(() => scope.IsSlewing, TimeSpan.FromSeconds(3), ct);
        if (!await Sequencer.Instructions.MountWaits.UntilAsync(() => !scope.IsSlewing, TimeSpan.FromSeconds(180), ct))
            _logger.LogWarning("{Label}: the mount was still slewing home after 180 s", label);
    }

    private async Task WarmAndCoolerOffAsync(bool waitForWarm, CancellationToken ct) {
        var cam = _sp.GetService<EquipmentManager>()?.Camera;
        if (cam is not { IsConnected: true }) return;
        var ramp = _sp.GetService<CoolingRampService>();
        // Gentle warm-up (ramp the setpoint up, then cooler off) rather than an
        // abrupt cut, which protects the sensor from thermal shock and dew.
        if (ramp != null) {
            var rate = _profiles.ActiveEquipmentProfile.CoolerRampDegPerMinute ?? 2.0;
            ramp.Start(cam, targetC: 15.0, ratePerMinute: rate <= 0 ? 2.0 : rate,
                       coolerOnFirst: false, coolerOffWhenDone: true, source: "teardown");
            // Only wait when the host is about to lose power; otherwise the ramp
            // finishes in the background and the teardown returns promptly.
            if (waitForWarm) {
                var deadline = DateTime.UtcNow.AddMinutes(20);
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested) {
                    if (ramp.Snapshot()?.Running != true) break;
                    await Task.Delay(2000, ct);
                }
            }
        } else {
            await cam.SetCoolerAsync(false, ct);
        }
    }
}
