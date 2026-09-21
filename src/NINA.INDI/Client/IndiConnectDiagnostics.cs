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

namespace NINA.INDI.Client;

/// <summary>
/// Turns a driver's own refusal text into something the operator can act
/// on. INDI drivers say what went wrong in their vocabulary, which is
/// accurate and useless: "Camera open error (-53): Could not claim the USB
/// device" is libgphoto2 for "something else already has this camera", and
/// on an appliance image that something is almost always the desktop's
/// gphoto volume monitor.
/// </summary>
public static class IndiConnectDiagnostics {

    /// <summary>One extra sentence to append to a driver's refusal, or null
    /// when we have nothing better to add than what the driver said. Kept
    /// pure so the mapping is testable without an INDI server.</summary>
    public static string? HintFor(string? driverReason) {
        if (string.IsNullOrWhiteSpace(driverReason)) return null;
        var r = driverReason.ToLowerInvariant();

        // libgphoto2 GP_ERROR_IO_USB_CLAIM. The camera is on the bus and
        // answering, another process holds the PTP interface. On a desktop
        // based image that is gvfsd-gphoto2 / gvfs-gphoto2-volume-monitor,
        // which claims any camera the moment it is plugged in.
        if (r.Contains("claim the usb") || r.Contains("-53")
                || r.Contains("auto-mounted as external disk storage")) {
            return "Another process on the host is holding the camera. The usual culprit is the "
                 + "desktop's gphoto volume monitor: run 'pkill -f gvfsd-gphoto2' and connect "
                 + "again, then 'sudo systemctl --global mask gvfs-gphoto2-volume-monitor' to "
                 + "stop it coming back. See docs/dslr-linux.md.";
        }

        // The body is on the bus but in a mode that does not speak PTP, or
        // the card is locked. Worth saying, because the driver's text sends
        // people looking for cable faults.
        if (r.Contains("could not detect") || r.Contains("no camera found")
                || r.Contains("unknown model")) {
            return "Check that the camera is powered on, its mode dial is not on a movie or "
                 + "wifi mode, and that it is set to PC connection rather than mass storage.";
        }

        // Serial devices: the port moved (ttyUSB renumbering) or another
        // process owns it.
        if (r.Contains("failed to connect to port") || r.Contains("permission denied")) {
            return "The driver could not open its serial port. Check the port in the INDI "
                 + "control panel (a by-id path survives renumbering) and that nothing else "
                 + "is using it.";
        }

        return null;
    }

    /// <summary>The sentence Polaris throws when a device answers a CONNECT
    /// with an alert. Built here so every call site words it the same way,
    /// and so the driver's own text is never dropped on the floor.</summary>
    public static string RefusalMessage(string device, string? driverReason) {
        var reason = string.IsNullOrWhiteSpace(driverReason)
            ? "the driver reported an alert on CONNECTION but gave no reason"
            : driverReason!.Trim();
        var hint = HintFor(driverReason);
        return hint == null
            ? $"'{device}' refused to connect: {reason}"
            : $"'{device}' refused to connect: {reason} {hint}";
    }
}
