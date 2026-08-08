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

using System.Runtime.InteropServices;

namespace NINA.Polaris.Services;

/// <summary>One USB device as the kernel sees it.</summary>
/// <param name="Path">sysfs node name, e.g. <c>3-1</c>. Encodes the bus and
/// port chain, so it also tells you WHICH hub a device hangs off.</param>
/// <param name="VendorId">4 hex digits, lower case, no 0x. e.g. <c>03c3</c>.</param>
/// <param name="ProductId">Same shape. e.g. <c>183e</c>.</param>
/// <param name="Manufacturer">USB iManufacturer string, when the device
/// sets one. Often the only thing that distinguishes OEM rebadges of
/// identical hardware (a ToupTek reports <c>TT</c>).</param>
/// <param name="Product">USB iProduct string. Sometimes names the device
/// outright (<c>ZWO EFW</c>, <c>ASI183MM Pro</c>), sometimes useless
/// (<c>USB2.0 Camera</c>).</param>
/// <param name="SpeedMbps">Link speed as reported by the kernel. 5000 means
/// the device negotiated SuperSpeed; a USB3 camera sitting at 480 is a strong
/// hint of a USB2 cable or port.</param>
public sealed record UsbDeviceInfo(
    string Path,
    string VendorId,
    string ProductId,
    string? Manufacturer,
    string? Product,
    string? Serial,
    int SpeedMbps,
    /// <summary>False when the kernel bound no driver to ANY of this device's
    /// interfaces, i.e. it enumerated on the bus and then nothing claimed it.
    /// For a USB-serial bridge that is precisely why no /dev/ttyUSB* appears,
    /// and the symptom the operator sees is a port picker that simply does not
    /// list their focuser. Field report 2026-08-06, Orange Pi 4 Pro on the
    /// shipped image: a Gemini focuser was plugged in and the only serial port
    /// on the whole system was the ZWO mount's.</summary>
    bool DriverBound = true,
    /// <summary>Kernel module that would claim this device, when it is a
    /// USB-serial bridge we recognise and nothing is bound. Null otherwise.</summary>
    string? MissingModuleHint = null);

/// <summary>A serial port and the stable by-id name that points at it.</summary>
/// <param name="ByIdName">Entry under <c>/dev/serial/by-id/</c>. Built from the
/// USB descriptor strings, so for a generic bridge it is something like
/// <c>usb-1a86_USB_Serial-if00-port0</c> which identifies the BRIDGE CHIP and
/// tells you nothing about the mount/focuser behind it.</param>
/// <param name="Device">Resolved target, e.g. <c>/dev/ttyUSB0</c>.</param>
public sealed record SerialPortInfo(string ByIdName, string Device);

public sealed class UsbScanResult {
    public bool Supported { get; init; }
    public string? UnsupportedReason { get; init; }
    public List<UsbDeviceInfo> Devices { get; init; } = new();
    public List<SerialPortInfo> SerialPorts { get; init; } = new();
}

/// <summary>
/// Enumerates attached USB devices and serial ports so Polaris can propose an
/// INDI profile instead of making the user hunt through 400+ drivers by hand.
///
/// <para>Reads sysfs directly rather than shelling out to <c>lsusb</c> (not
/// installed everywhere, and parsing its output is worse than reading the
/// files it reads) and rather than taking a libusb dependency (which would
/// need device permissions we do not otherwise require -- this only needs to
/// read public attributes).</para>
///
/// <para>Linux only, deliberately: the consumer is the INDI profile assistant,
/// and indiserver itself is not packaged for Windows (see
/// <see cref="IndiWebManagerService.UnsupportedReason"/>). On other platforms
/// this returns an empty, clearly-labelled result instead of throwing.</para>
/// </summary>
public sealed class UsbScanService {
    private const string UsbRoot = "/sys/bus/usb/devices";
    private const string SerialByIdDir = "/dev/serial/by-id";

    /// <summary>Linux virtual root hubs. Present on every machine, never
    /// something the user plugged in.</summary>
    private const string LinuxFoundationVid = "1d6b";

    /// <summary>USB device class 09 = hub. Filtered out because a hub is
    /// plumbing, not equipment -- note that astro cameras frequently EMBED
    /// one (the ZWO cooled bodies expose a 2-port hub for EFW/EAF), so hubs
    /// do appear in the middle of a legitimate rig.</summary>
    private const string HubDeviceClass = "09";

    private readonly ILogger<UsbScanService> _logger;

    public UsbScanService(ILogger<UsbScanService> logger) {
        _logger = logger;
    }

    public static bool IsSupportedOs => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    public UsbScanResult Scan() {
        if (!IsSupportedOs) {
            return new UsbScanResult {
                Supported = false,
                UnsupportedReason =
                    "USB detection reads Linux sysfs and is only available on Linux hosts. " +
                    $"This host is {RuntimeInformation.OSDescription}.",
            };
        }
        if (!Directory.Exists(UsbRoot)) {
            return new UsbScanResult {
                Supported = false,
                UnsupportedReason = $"{UsbRoot} is not present on this system.",
            };
        }

        var devices = new List<UsbDeviceInfo>();
        foreach (var dir in SafeEnumerateDirectories(UsbRoot)) {
            // Interface nodes (e.g. "1-1:1.0") carry no idVendor, so the
            // presence of that file is exactly the "this is a device, not an
            // interface" test.
            var vid = ReadAttr(dir, "idVendor");
            if (string.IsNullOrEmpty(vid)) continue;
            if (string.Equals(vid, LinuxFoundationVid, StringComparison.OrdinalIgnoreCase)) continue;
            if (ReadAttr(dir, "bDeviceClass") == HubDeviceClass) continue;

            var pid = ReadAttr(dir, "idProduct") ?? "";
            _ = int.TryParse(ReadAttr(dir, "speed"), out int speed);
            bool bound = AnyInterfaceHasDriver(dir);
            devices.Add(new UsbDeviceInfo(
                Path: System.IO.Path.GetFileName(dir),
                VendorId: vid.ToLowerInvariant(),
                ProductId: pid.ToLowerInvariant(),
                Manufacturer: ReadAttr(dir, "manufacturer"),
                Product: ReadAttr(dir, "product"),
                Serial: ReadAttr(dir, "serial"),
                SpeedMbps: speed,
                DriverBound: bound,
                MissingModuleHint: bound ? null : SerialBridgeModule(vid.ToLowerInvariant())));
        }
        devices.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        var result = new UsbScanResult {
            Supported = true,
            Devices = devices,
            SerialPorts = ScanSerialPorts(),
        };
        _logger.LogInformation(
            "USB scan: {DeviceCount} device(s), {PortCount} serial port(s)",
            result.Devices.Count, result.SerialPorts.Count);
        return result;
    }

    /// <summary>Did the kernel bind a driver to any interface of this device?
    ///
    /// A USB device's interfaces appear as sub-directories named
    /// <c>&lt;dev&gt;:&lt;config&gt;.&lt;interface&gt;</c>, and each grows a
    /// <c>driver</c> symlink once something claims it. No symlink anywhere
    /// means the device enumerated and then nothing took it - the module for
    /// it is not loaded, or not present in this kernel at all.
    ///
    /// Checking for a bound driver rather than matching a list of chips is
    /// deliberate: it catches any device the image cannot drive, including
    /// bridges nobody has added to the table yet.</summary>
    private static bool AnyInterfaceHasDriver(string deviceDir) {
        try {
            var self = System.IO.Path.GetFileName(deviceDir);
            foreach (var sub in Directory.EnumerateDirectories(deviceDir)) {
                var name = System.IO.Path.GetFileName(sub);
                // Interfaces are "1-1:1.0"; skip the device's own children
                // ("1-1.4" is a downstream device on a hub, not an interface).
                if (!name.StartsWith(self + ":", StringComparison.Ordinal)) continue;
                if (Directory.Exists(System.IO.Path.Combine(sub, "driver"))
                        || File.Exists(System.IO.Path.Combine(sub, "driver"))) {
                    return true;
                }
            }
            // A hub or a device we could not read interfaces for: do not cry
            // wolf. Only an explicit "interfaces exist and none is claimed"
            // counts as unbound.
            return !Directory.EnumerateDirectories(deviceDir)
                .Any(d => System.IO.Path.GetFileName(d)
                    .StartsWith(self + ":", StringComparison.Ordinal));
        } catch {
            return true;
        }
    }

    /// <summary>Kernel module for the USB-serial bridges that turn up on this
    /// kind of gear, keyed by vendor id. Only used to make an unbound device
    /// actionable ("install/load ch341"), never to decide anything.</summary>
    private static string? SerialBridgeModule(string vendorId) => vendorId switch {
        "1a86" => "ch341",    // QinHeng CH340 / CH341, the cheap bridge in most focusers
        "10c4" => "cp210x",   // Silicon Labs CP2102 / CP2104
        "0403" => "ftdi_sio", // FTDI FT232 and friends
        "067b" => "pl2303",   // Prolific PL2303
        _ => null,
    };

    private List<SerialPortInfo> ScanSerialPorts() {
        var ports = new List<SerialPortInfo>();
        if (!Directory.Exists(SerialByIdDir)) return ports;
        foreach (var link in SafeEnumerateFileSystemEntries(SerialByIdDir)) {
            try {
                // by-id entries are symlinks into ../../ttyUSB0 and friends.
                // ResolveLinkTarget(true) walks them to the real node so the
                // caller gets something it can hand to a driver.
                var target = File.ResolveLinkTarget(link, returnFinalTarget: true)?.FullName;
                ports.Add(new SerialPortInfo(
                    System.IO.Path.GetFileName(link),
                    target ?? link));
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Could not resolve serial by-id entry {Link}", link);
            }
        }
        ports.Sort((a, b) => string.CompareOrdinal(a.ByIdName, b.ByIdName));
        return ports;
    }

    /// <summary>Read one sysfs attribute, or null when it is absent or
    /// unreadable. Missing attributes are normal (plenty of devices set no
    /// manufacturer string), so this never throws.</summary>
    private static string? ReadAttr(string dir, string name) {
        try {
            var path = System.IO.Path.Combine(dir, name);
            if (!File.Exists(path)) return null;
            var value = File.ReadAllText(path).Trim();
            return value.Length == 0 ? null : value;
        } catch {
            return null;
        }
    }

    private IEnumerable<string> SafeEnumerateDirectories(string path) {
        try {
            return Directory.EnumerateDirectories(path);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Could not enumerate {Path}", path);
            return Array.Empty<string>();
        }
    }

    private IEnumerable<string> SafeEnumerateFileSystemEntries(string path) {
        try {
            return Directory.EnumerateFileSystemEntries(path);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Could not enumerate {Path}", path);
            return Array.Empty<string>();
        }
    }
}
