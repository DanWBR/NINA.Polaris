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

using System.Net.NetworkInformation;

namespace NINA.Polaris.Services;

/// <summary>
/// The one place that decides what this device calls itself on the network.
///
/// <para>There used to be two. MdnsService advertised
/// <c>polaris-app-{shortId}.local</c> so that a cloned SD card would not
/// collide with its siblings, and SelfSignedCertService independently put a
/// hardcoded <c>polaris-app.local</c> into the certificate. The name a client
/// actually discovers was therefore never in the cert, and every connection
/// made through mDNS discovery failed validation. Two components deciding the
/// same fact separately is the bug; one function they both call is the fix.
/// </para>
///
/// <para>Note this is the NETWORK identity, which is not the same thing as the
/// label the operator types in Settings. That one
/// (<see cref="UserProfile.DeviceFriendlyName"/>) is cosmetic: a title for the
/// browser tab and for the device list in the mobile apps. It never changes the
/// host's hostname and never enters a certificate, so renaming a scope cannot
/// lock anybody out.</para>
/// </summary>
public static class DeviceIdentity {

    /// <summary>Four hex-ish characters that are stable for this board across
    /// reboots and reflashes, and different from its siblings. Tried in order
    /// of how strongly each source is tied to the physical device.</summary>
    public static string ShortId() {
        // Raspberry Pi exposes the board serial in the device tree.
        try {
            const string dt = "/sys/firmware/devicetree/base/serial-number";
            if (File.Exists(dt)) {
                var s = File.ReadAllText(dt).Trim('\0', ' ', '\n', '\r', '\t');
                if (s.Length >= 4) return s[^4..].ToLowerInvariant();
            }
        } catch { /* fall through */ }

        // /proc/cpuinfo "Serial" line (older Pi OS / other ARM boards).
        try {
            foreach (var line in File.ReadLines("/proc/cpuinfo")) {
                if (line.StartsWith("Serial", StringComparison.OrdinalIgnoreCase)) {
                    var v = line.Split(':').Last().Trim();
                    if (v.Length >= 4) return v[^4..].ToLowerInvariant();
                }
            }
        } catch { /* fall through */ }

        // Primary non-loopback MAC.
        try {
            var mac = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.GetPhysicalAddress().ToString())
                .FirstOrDefault(m => !string.IsNullOrEmpty(m) && m.Trim('0').Length > 0);
            if (!string.IsNullOrEmpty(mac) && mac.Length >= 4) return mac[^4..].ToLowerInvariant();
        } catch { /* fall through */ }

        // Last resort: stable hash of the machine name. Weak, because a fleet
        // flashed from one image shares a machine name, but by this point every
        // hardware source has already failed.
        var h = Math.Abs(Environment.MachineName.GetHashCode());
        return (h % 0x10000).ToString("x4");
    }

    /// <summary>The mDNS instance name this device advertises, e.g.
    /// <c>polaris-app-8b53</c>. An explicit <c>Mdns:InstanceName</c> in
    /// configuration wins, and the certificate honours that override too, so
    /// the two can never disagree.</summary>
    public static string InstanceName(IConfiguration? config = null) {
        var configured = config?["Mdns:InstanceName"];
        return !string.IsNullOrWhiteSpace(configured)
            ? configured!.Trim()
            : $"polaris-app-{ShortId()}";
    }

    /// <summary>Every DNS name that resolves to this device, in the order a
    /// person is likely to type them.
    ///
    /// <para>The per-device name comes first because it is the only one that
    /// stays correct when a second board joins the network. The shared aliases
    /// follow: they are convenient with one device and ambiguous with several,
    /// which is a property of mDNS rather than of this list. They stay in the
    /// certificate because whichever board answers to them still has to present
    /// a cert that validates.</para></summary>
    public static IEnumerable<string> DnsNames(IConfiguration? config = null) {
        var instance = InstanceName(config);
        yield return instance;
        yield return instance + ".local";

        var hostName = System.Net.Dns.GetHostName();
        if (!string.IsNullOrWhiteSpace(hostName)) {
            yield return hostName;
            yield return hostName + ".local";
        }

        yield return "localhost";
        yield return "polaris.local";
        yield return "polaris-app.local";
    }
}
