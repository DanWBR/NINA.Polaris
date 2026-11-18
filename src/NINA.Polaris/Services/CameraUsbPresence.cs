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
/// Finds a native-SDK camera's USB device in Linux sysfs and tells when it
/// leaves the bus.
///
/// <para>The SDK removal codes were the only disconnect detector, and the
/// ZWO SDK does not return them for an unplugged camera: its control reads
/// went on succeeding, so a camera pulled out by the cable stayed
/// "connected" (FRA400, 2026-11-18). The kernel removes the device directory
/// the moment the cable comes out, whatever the SDK thinks.</para>
///
/// <para>The camera is matched by its USB product string against the model
/// name the SDK reported ("ASI2600MC Pro" in "ZWO ASI2600MC Pro"). The
/// vendor id alone is not enough: ZWO mounts, focusers and filter wheels
/// share 03c3 with the cameras. A device that comes back with a new device
/// number was re-enumerated, and the SDK handle on the old one is dead too.</para>
/// </summary>
internal static class CameraUsbPresence {
    internal const string SysfsRoot = "/sys/bus/usb/devices";

    /// <summary>One USB device as sysfs lists it. <paramref name="Name"/> is
    /// the directory ("1-1.2"), the port path on its bus.</summary>
    internal sealed record UsbNode(string Name, string Vendor, string Product, string ProductName, string DevNum);

    /// <summary>The USB vendor ids a backend's cameras use, and whether a lone
    /// device of that vendor can be taken for the camera when its product
    /// string does not name the model. Only SVBony and QHY: ZWO and PlayerOne
    /// make filter wheels and other accessories on the same vendor id, and
    /// 0547 (ToupTek, Altair) is a generic Cypress id.</summary>
    internal readonly record struct Spec(string[] Vendors, bool LoneDeviceIsTheCamera);

    internal static Spec? SpecFor(string? driver) => driver switch {
        "zwo-sdk" => new Spec(new[] { "03c3" }, false),
        "svbony-sdk" => new Spec(new[] { "f266" }, true),
        "playerone-sdk" => new Spec(new[] { "a0a0" }, false),
        "qhy-sdk" => new Spec(new[] { "1618" }, true),
        "touptek-sdk" => new Spec(new[] { "0547" }, false),
        "altair-sdk" => new Spec(new[] { "0547", "16d0" }, false),
        _ => null
    };

    /// <summary>The spec for a camera on <paramref name="driver"/>. An INDI
    /// camera is placed by the vendor in its device name ("ZWO CCD ASI120MM
    /// Mini", "SVBONY SV705C"); one the name does not place is not watched.</summary>
    internal static Spec? SpecFor(string? driver, string? deviceName) {
        if (driver != "indi") return SpecFor(driver);
        var n = (deviceName ?? "").ToLowerInvariant();
        if (n.Contains("zwo") || n.StartsWith("asi")) return SpecFor("zwo-sdk");
        if (n.Contains("svbony")) return SpecFor("svbony-sdk");
        if (n.Contains("playerone") || n.Contains("player one")) return SpecFor("playerone-sdk");
        if (n.Contains("qhy")) return SpecFor("qhy-sdk");
        if (n.Contains("toup")) return SpecFor("touptek-sdk");
        if (n.Contains("altair")) return SpecFor("altair-sdk");
        return null;
    }

    /// <summary>Every USB device under <paramref name="root"/>. Interface
    /// entries ("1-1.2:1.0") and anything unreadable are skipped.</summary>
    internal static List<UsbNode> List(string root) {
        var list = new List<UsbNode>();
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(root); }
        catch { return list; }
        foreach (var path in entries) {
            var node = Read(root, Path.GetFileName(path));
            if (node != null) list.Add(node);
        }
        return list;
    }

    /// <summary>The device at <paramref name="name"/>, or null when it is
    /// not there (unplugged) or is not a device.</summary>
    internal static UsbNode? Read(string root, string name) {
        if (name.Contains(':')) return null;
        var dir = Path.Combine(root, name);
        var vendor = ReadAttr(dir, "idVendor");
        if (vendor == null) return null;
        return new UsbNode(name, vendor.ToLowerInvariant(),
            (ReadAttr(dir, "idProduct") ?? "").ToLowerInvariant(),
            ReadAttr(dir, "product") ?? "", ReadAttr(dir, "devnum") ?? "");
    }

    private static string? ReadAttr(string dir, string attr) {
        try {
            var f = Path.Combine(dir, attr);
            return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
        } catch { return null; }
    }

    /// <summary>The device that is this camera: the one of the backend's
    /// vendors whose product string names the model, or, where the backend
    /// allows it, the only device of that vendor. Devices another camera
    /// already holds are left out. Null when it cannot be told apart, and
    /// then nothing is watched (which is what happened before).</summary>
    internal static UsbNode? Resolve(Spec spec, string? cameraName, IEnumerable<UsbNode> nodes,
                                     ICollection<string> claimed) {
        var candidates = nodes.Where(n => spec.Vendors.Contains(n.Vendor) && !claimed.Contains(n.Name)).ToList();
        var named = candidates.Where(n => NamesTheModel(n.ProductName, cameraName)).ToList();
        if (named.Count == 1) return named[0];
        if (named.Count == 0 && spec.LoneDeviceIsTheCamera && candidates.Count == 1) return candidates[0];
        return null;
    }

    /// <summary>True when the USB product string is the camera model: it
    /// appears in the SDK's name once both are reduced to letters and digits.
    /// The product string must carry a digit outside "usb2"/"usb3", so the
    /// generic ones ("ZWO Device", "USB3.0 Camera") never match.</summary>
    internal static bool NamesTheModel(string? productName, string? cameraName) {
        var p = Norm(productName);
        var c = Norm(cameraName);
        if (p.Length < 5 || c.Length == 0) return false;
        var model = p.Replace("usb30", "").Replace("usb20", "").Replace("usb3", "").Replace("usb2", "");
        if (!model.Any(char.IsDigit)) return false;
        return c.Contains(p, StringComparison.Ordinal) || p.Contains(c, StringComparison.Ordinal);
    }

    private static string Norm(string? s) =>
        s == null ? "" : new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Still the same device: present, same ids, same device number.</summary>
    internal static bool StillThere(string root, UsbNode node) {
        var now = Read(root, node.Name);
        return now != null && now.Vendor == node.Vendor && now.Product == node.Product && now.DevNum == node.DevNum;
    }
}
