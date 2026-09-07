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

using System.Text;
using NINA.INDI.Client;
using NINA.INDI.Protocol;

namespace NINA.Polaris.Services;

/// <summary>
/// Keeps a camera that Polaris drives through a vendor SDK from ALSO being
/// connected inside INDI.
///
/// Field session 2026-09-05 (OPi5Pro): the rig drove an ASI585MC Pro through
/// the ZWO SDK while <c>indi_asi_ccd</c> had the same camera connected, because
/// that one driver binary publishes EVERY ASI camera on the bus and the guide
/// camera was an ASI too. <c>lsof /dev/bus/usb/002/002</c> listed both
/// NINA.Polaris and indi_asi_ccd; the kernel logged
/// <c>usbfs: interface 0 claimed by usbfs while '.NET TP Worker' sets config #1</c>.
/// The ZWO SDK allows one owner per camera, so exposures came back as
/// "ASI exposure failed" and the USB link reset, every time both stacks touched
/// the device at once (autofocus made it reliable).
///
/// Two things had to change. This service disconnects such a device as soon as
/// it sees it connect in INDI, and <see cref="IndiDriverWatchdogService"/> asks
/// <see cref="IsNativelyDriven"/> before reconnecting a dropped device, since
/// its whole job is to put devices back and it would otherwise undo this.
/// </summary>
public sealed class NativeCameraIndiGuard : IHostedService {
    private readonly IndiClient _indi;
    private readonly EquipmentManager _equipment;
    private readonly NotificationService _notify;
    private readonly ILogger<NativeCameraIndiGuard> _logger;

    public NativeCameraIndiGuard(IndiClient indi, EquipmentManager equipment,
                                 NotificationService notify,
                                 ILogger<NativeCameraIndiGuard> logger) {
        _indi = indi;
        _equipment = equipment;
        _notify = notify;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        _indi.PropertyChanged += OnPropertyChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        _indi.PropertyChanged -= OnPropertyChanged;
        return Task.CompletedTask;
    }

    private void OnPropertyChanged(string device, IndiProperty prop) {
        if (!string.Equals(prop.Name, "CONNECTION", StringComparison.OrdinalIgnoreCase)) return;
        if (prop is not IndiSwitchProperty sw) return;
        if (!sw.Values.TryGetValue("CONNECT", out var connected) || !connected) return;
        if (!IsNativelyDriven(device)) return;

        // Fire and forget: this runs on the INDI reader thread, and the
        // disconnect is a round trip.
        _ = Task.Run(async () => {
            try {
                _logger.LogWarning(
                    "INDI '{Device}' is the same camera Polaris drives through its vendor SDK; " +
                    "disconnecting it in INDI so the two do not fight over the USB device", device);
                _notify.Push("warn",
                    $"{device} was also connected in INDI. Polaris drives it through the vendor " +
                    "driver, so the INDI copy was disconnected to keep exposures working.", 9000);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await _indi.DisconnectDeviceAsync(device, cts.Token);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Could not disconnect the INDI copy of '{Device}'", device);
            }
        });
    }

    /// <summary>True when this INDI device is a camera the rig currently drives
    /// through a vendor SDK, so INDI must not hold it open.</summary>
    public bool IsNativelyDriven(string indiDevice) {
        if (string.IsNullOrWhiteSpace(indiDevice)) return false;
        foreach (var (driver, cam) in new[] {
                     (_equipment.CameraDriver, _equipment.Camera),
                     (_equipment.GuideCameraDriver, _equipment.GuideCamera),
                     (_equipment.AuxCameraDriver, _equipment.AuxCamera),
                 }) {
            if (cam == null) continue;
            if (IsIndiDriver(driver)) continue;                 // that one IS the INDI copy
            if (SameCamera(indiDevice, cam.DeviceName)) return true;
        }
        return false;
    }

    /// <summary>A driver id that means "this camera IS the INDI device", so it
    /// is not a conflict. Everything else (zwo-sdk, svbony-sdk, playerone-sdk,
    /// touptek-sdk, altair-sdk, alpaca, ascom, canon-edsdk, …) is a separate
    /// owner of the hardware.</summary>
    public static bool IsIndiDriver(string? driver) =>
        string.IsNullOrWhiteSpace(driver)
        || driver.Equals("indi", StringComparison.OrdinalIgnoreCase);

    /// <summary>Do an INDI device name and a vendor-SDK device name denote the
    /// same physical camera? The two stacks label it differently
    /// ("ZWO CCD ASI585MC Pro" vs "ZWO ASI585MC Pro"), so compare on the model
    /// alone: strip everything that is not a letter or a digit, and drop the
    /// vendor and bus words both sides sprinkle in.</summary>
    public static bool SameCamera(string? indiDevice, string? nativeDeviceName) {
        var a = Canonical(indiDevice);
        var b = Canonical(nativeDeviceName);
        return a.Length > 0 && a == b;
    }

    private static readonly string[] Noise = {
        "zwo", "ccd", "camera", "cam", "svbony", "sv", "playerone", "touptek",
        "altair", "qhy", "imaging", "usb",
    };

    private static string Canonical(string? name) {
        if (string.IsNullOrWhiteSpace(name)) return "";
        // Collapse to letters and digits FIRST, then drop the noise words. The
        // other order misses a vendor that spells itself with a space on one
        // side and without on the other ("Player One" vs "PlayerOne").
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.ToLowerInvariant()) {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        var flat = sb.ToString();
        foreach (var w in Noise) flat = flat.Replace(w, "");
        return flat;
    }
}
