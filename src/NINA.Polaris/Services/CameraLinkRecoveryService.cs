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
using NINA.Polaris.Endpoints;

namespace NINA.Polaris.Services;

/// <summary>
/// Reopens a native-SDK camera that dropped off the USB bus on its own.
///
/// <para>A USB reset on a shared port, a loose cable or a brown-out takes the
/// camera off the bus, and the kernel usually lists it again within a second.
/// The SDK handle Polaris holds is dead by then, so without this the camera
/// stayed disconnected (guiding lost its star, captures timed out) until
/// someone selected it again by hand. Field case: on an Orange Pi 4 Pro the
/// guide camera shared the xHCI port with an SV605CC, whose every frame resets
/// that port, and the guide camera fell off with it a few times an hour.</para>
///
/// <para>Only a drop the adapter reported (a <see cref="ICamera.DisconnectReason"/>)
/// starts the retries; a disconnect the operator asked for does not, and one
/// asked for while retrying ends them (<see cref="Stop"/>). The same instance
/// is reopened in place, so the guider, the capture loops and the stream keep
/// the camera they already hold. INDI and Alpaca are left to their own
/// watchdogs.</para>
/// </summary>
public sealed class CameraLinkRecoveryService : BackgroundService {
    /// <summary>Waits between attempts. The first is short: the camera is
    /// usually back on the bus before Polaris notices it went.</summary>
    internal static readonly TimeSpan[] Backoff = {
        TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30)
    };

    /// <summary>After this long without the camera coming back, stop and tell
    /// the operator: it is unplugged or off, not glitching.</summary>
    internal static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    internal static bool IsNativeSdk(string? driver) => driver is
        "svbony-sdk" or "zwo-sdk" or "playerone-sdk" or "qhy-sdk" or "touptek-sdk" or "altair-sdk";

    internal readonly record struct Slot(string Role, ICamera? Camera, string? Driver);

    private sealed class Pending {
        public required ICamera Camera { get; init; }
        public required DateTime Since { get; init; }
        public DateTime NextTry { get; set; }
        public int Attempts { get; set; }
        public bool GaveUp { get; set; }
    }

    private readonly Func<IEnumerable<Slot>> _slots;
    private readonly Func<ICamera, CancellationToken, Task<bool>> _reopen;
    private readonly NotificationService? _notifications;
    private readonly ILogger _logger;
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    // Cameras the operator disconnected: not reopened until they connect again.
    // The endpoint calls Stop before its DisconnectAsync clears the adapter's
    // reason, and a tick in between must not arm the retries again.
    private readonly HashSet<ICamera> _stopped = new(ReferenceEqualityComparer.Instance);
    private readonly object _gate = new();

    public CameraLinkRecoveryService(EquipmentManager equip, NativeCameraControlStore controls,
                                     NotificationService notifications,
                                     ILogger<CameraLinkRecoveryService> logger)
        : this(() => new[] {
                   new Slot("camera", equip.Camera, equip.CameraDriver),
                   new Slot("guide camera", equip.GuideCamera, equip.GuideCameraDriver),
                   new Slot("aux camera", equip.AuxCamera, equip.AuxCameraDriver) },
               null, notifications, logger) {
        _reopen = (cam, ct) => ReopenAsync(cam, controls, ct);
    }

    internal CameraLinkRecoveryService(Func<IEnumerable<Slot>> slots,
                                       Func<ICamera, CancellationToken, Task<bool>>? reopen,
                                       NotificationService? notifications, ILogger logger) {
        _slots = slots;
        _reopen = reopen ?? ((_, _) => Task.FromResult(false));
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>The operator disconnected this camera: a camera that was
    /// waiting to be reopened stays down.</summary>
    public void Stop(ICamera? camera) {
        if (camera == null) return;
        lock (_gate) {
            _stopped.Add(camera);
            foreach (var role in _pending.Where(p => ReferenceEquals(p.Value.Camera, camera))
                                         .Select(p => p.Key).ToList())
                _pending.Remove(role);
        }
    }

    /// <summary>True while <paramref name="role"/> is waiting to be reopened.</summary>
    internal bool IsPending(string role) {
        lock (_gate) return _pending.TryGetValue(role, out var p) && !p.GaveUp;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        while (!stoppingToken.IsCancellationRequested) {
            try { await TickAsync(DateTime.UtcNow, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogDebug(ex, "Camera link recovery tick failed"); }
            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task TickAsync(DateTime now, CancellationToken ct) {
        foreach (var (role, cam, driver) in _slots()) {
            Pending? p;
            lock (_gate) {
                _pending.TryGetValue(role, out p);
                // A new selection, a camera removed from the slot, a backend
                // that is not ours, or a camera that is up again: nothing to do.
                if (p != null && (cam == null || !ReferenceEquals(p.Camera, cam) || cam.IsConnected)) {
                    _pending.Remove(role);
                    p = null;
                }
                if (cam != null && cam.IsConnected) _stopped.Remove(cam);
                if (cam == null || cam.IsConnected || !IsNativeSdk(driver)) continue;
                if (p == null) {
                    if (string.IsNullOrWhiteSpace(cam.DisconnectReason) || _stopped.Contains(cam)) continue;
                    p = new Pending { Camera = cam, Since = now, NextTry = now + Backoff[0] };
                    _pending[role] = p;
                    _logger.LogInformation("{Camera} dropped off ({Reason}); Polaris will try to reopen it",
                        cam.DeviceName, cam.DisconnectReason);
                    continue;
                }
                if (p.GaveUp || now < p.NextTry) continue;
                if (now - p.Since > GiveUpAfter) {
                    p.GaveUp = true;
                    var msg = $"{cam.DeviceName} did not come back within {GiveUpAfter.TotalMinutes:F0} minutes. "
                            + "Check its cable and power, then connect it again in RIGS.";
                    _logger.LogWarning("{Message}", msg);
                    _notifications?.Push("error", msg, 0);
                    continue;
                }
                p.Attempts++;
            }

            bool ok;
            try { ok = await _reopen(cam!, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) {
                _logger.LogDebug(ex, "Reopening {Camera} failed", cam!.DeviceName);
                ok = false;
            }

            lock (_gate) {
                // The operator may have disconnected it, or picked another
                // camera, while the attempt ran.
                if (!_pending.TryGetValue(role, out var still) || !ReferenceEquals(still, p)) {
                    continue;
                }
                if (ok) {
                    _pending.Remove(role);
                    var msg = $"{cam!.DeviceName} is back: Polaris reopened it after it dropped off the USB bus.";
                    _logger.LogWarning("{Message} ({Attempts} attempt(s), {Seconds:F0}s after the drop)",
                        msg, p.Attempts, (DateTime.UtcNow - p.Since).TotalSeconds);
                    _notifications?.Push("info", msg, 8000);
                } else {
                    p.NextTry = now + Backoff[Math.Min(p.Attempts, Backoff.Length - 1)];
                }
            }
        }
    }

    /// <summary>Close the dead handle, open the camera again and put its saved
    /// SDK controls (gain, offset, cooler...) back, as the Connect button does.</summary>
    private async Task<bool> ReopenAsync(ICamera cam, NativeCameraControlStore controls, CancellationToken ct) {
        try { await cam.DisconnectAsync(ct); } catch { /* the handle is gone already */ }
        await DeviceConnectGuard.BoundedAsync("reconnect", cam.DeviceName,
            t => cam.ConnectAsync(t), TimeSpan.FromSeconds(30));
        if (!cam.IsConnected) return false;
        try { controls.ApplySaved(cam); } catch { /* non-fatal, as in the endpoints */ }
        return true;
    }
}
