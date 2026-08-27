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

using NINA.Polaris.Services;

namespace NINA.Polaris.WebSocket.Status;

/// <summary>
/// Devices, the camera stream, and what the host can see on its USB bus.
///
/// Blocks owned: equipment, cameraStream, keepCentered, simulator, network, usbDrive, usbRemoved.
/// </summary>
public sealed class EquipmentStatusContributor : IStatusContributor {
    private readonly CameraStreamService _cameraStream;
    private readonly EquipmentManager _equip;
    private readonly NINA.Polaris.Services.Planetary.KeepCenteredService _keepCentered;
    private readonly NetworkManagerService _network;
    private readonly NINA.Polaris.Services.Simulator.SimulatorService _simulator;
    private readonly UsbDriveWatcherService _usbWatcher;

    public EquipmentStatusContributor(CameraStreamService cameraStream, EquipmentManager equip, NINA.Polaris.Services.Planetary.KeepCenteredService keepCentered, NetworkManagerService network, NINA.Polaris.Services.Simulator.SimulatorService simulator, UsbDriveWatcherService usbWatcher) {
        _cameraStream = cameraStream;
        _equip = equip;
        _keepCentered = keepCentered;
        _network = network;
        _simulator = simulator;
        _usbWatcher = usbWatcher;
    }

    public IReadOnlyCollection<string> Keys { get; } = new[] { "equipment", "cameraStream", "keepCentered", "simulator", "network", "usbDrive", "usbRemoved" };

    public void Contribute(StatusTick tick) {
        var cameraStream = _cameraStream;
        var equip = _equip;
        var keepCentered = _keepCentered;
        var network = _network;
        var simulator = _simulator;
        var usbWatcher = _usbWatcher;

            tick.Blocks["equipment"] = equip.GetEquipmentStatus();

            // Auxiliary camera capture loop status (running + frames
            // saved this session + a no-output-folder warning).
            tick.Blocks["cameraStream"] = new {
                running = cameraStream.IsRunning,
                mode = cameraStream.Mode,
                exposure = cameraStream.ExposureSeconds,
                gain = cameraStream.Gain,
                frames = cameraStream.FrameCount,
                fps = cameraStream.Fps,
                captureFps = cameraStream.Fps,
                transmitFps = cameraStream.TransmitFps,
                // PLAN8: the streamed frame geometry, so the VIDEO
                // panel can work out the byte rate a recording
                // would produce (width*height*bytes*fps) and say it
                // BEFORE the disk finds out.
                width = cameraStream.LastFrameWidth,
                height = cameraStream.LastFrameHeight,
                // PLAN8-2: focus aid. `sharpness` is the current
                // contrast reading, `sharpnessBest` the best since
                // the stream started (the yardstick to maximise),
                // and the history is sampled at 2 Hz for the trend
                // line. Absolute values mean nothing across
                // targets, which is why the UI plots a ratio.
                sharpness = cameraStream.Sharpness,
                sharpnessBest = cameraStream.SharpnessBest,
                sharpnessHistory = cameraStream.SharpnessHistory,
                // Honest exposure read: the preview auto-stretches, so a blown
                // highlight looks fine on screen; this is the % actually clipped.
                clipPercent = cameraStream.ClipPercent,
                lastError = cameraStream.LastError,
                supportsNative = equip.Camera?.Capabilities.SupportsVideoStream ?? false
            };

            // KC-1: Keep Centered control loop. Top-level
            // sibling of cameraStream so the VIDEO sidebar
            // toggle can read phase + offset readout every
            // tick without an extra REST poll. running=false
            // when idle; phase cycles idle->calibrating->
            // locked (with occasional lost in poor seeing).
            tick.Blocks["keepCentered"] = new {
                running = keepCentered.IsRunning,
                phase = keepCentered.Phase,
                lastOffsetPx = keepCentered.LastOffsetPx,
                lastCorrectionMs = keepCentered.LastCorrectionMs
            };

            // Planetary recording lifecycle (VIDEO tab Capture).
            tick.Blocks["simulator"] = simulator.GetStatus();

            // WIFI-3: host WiFi state (mode + ssid + ip +
            // signal). 501-class platforms (Windows /
            // macOS / no nmcli / no wifi iface) still send
            // the block, the supportedOs/nmcliInstalled/
            // hasWifi flags + unsupportedReason tell the
            // UI which banner to show.
            tick.Blocks["network"] = new {
                supportedOs       = network.IsSupportedOs,
                nmcliInstalled    = network.NmcliInstalled,
                hasWifi           = network.HasWifiInterface,
                wifiInterface     = network.WifiInterface,
                mode              = network.CurrentMode.ToString().ToLowerInvariant(),
                ssid              = network.CurrentSsid,
                ip                = network.CurrentIp,
                signal            = network.SignalStrength,
                hotspotSsid       = network.HotspotSsid,
                lastError         = network.LastError,
                unsupportedReason = network.UnsupportedReason,
                lastRefreshAt     = network.LastRefreshAt,
                // Auto AP fallback: when the rig is carried
                // out of range of every saved network the
                // watchdog starts the hotspot so it stays
                // reachable. fallbackEngaged tells the UI to
                // show "Hotspot started automatically".
                autoHotspotFallback = network.AutoHotspotFallback,
                fallbackEngaged     = network.HotspotFallbackEngaged
            };

            // Auto-push of saved images to network storage
            // (SMB / SFTP / mounted path). Drives the Settings
            // card's live status line. Password is never exposed.
            tick.Blocks["usbDrive"] = usbWatcher.Pending is { } usb ? new {
                path       = usb.Path,
                label      = usb.Label,
                freeBytes  = usb.FreeBytes,
                totalBytes = usb.TotalBytes
            } : null;

            // The drive holding the capture home was unplugged, offering
            // a revert to the default folder. null when nothing pending.
            tick.Blocks["usbRemoved"] = usbWatcher.RevertPending is { } rp ? new {
                label       = rp.RemovedLabel,
                defaultPath = rp.DefaultPath
            } : null;

            // BENCH: compact progress for the Settings card.
            // Full results are fetched over REST.
    }

}
