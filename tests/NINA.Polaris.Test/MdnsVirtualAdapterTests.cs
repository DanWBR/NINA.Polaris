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

using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// mDNS leaves out the addresses of adapters a phone cannot reach, so the
/// app does not pick a WSL / Hyper-V / container address for the host.
/// </summary>
[TestFixture]
public class MdnsVirtualAdapterTests {

    [TestCase("vEthernet (WSL (Hyper-V firewall))", "Hyper-V Virtual Ethernet Adapter #2")]
    [TestCase("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter")]
    [TestCase("Ethernet 3", "VirtualBox Host-Only Ethernet Adapter")]
    [TestCase("VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter for VMnet8")]
    [TestCase("docker0", "docker0")]
    [TestCase("br-3f2a9c1d7e44", "br-3f2a9c1d7e44")]
    [TestCase("veth1a2b3c4", "veth1a2b3c4")]
    [TestCase("virbr0", "virbr0")]
    public void Virtual_IsLeftOut(string name, string description) {
        Assert.That(MdnsService.IsVirtualAdapter(name, description), Is.True);
    }

    [TestCase("Wi-Fi 3", "Intel(R) Wi-Fi 6E AX210 160MHz #2")]
    [TestCase("Ethernet", "Realtek PCIe GbE Family Controller")]
    [TestCase("wlan0", "wlan0")]
    [TestCase("eth0", "eth0")]
    [TestCase("end0", "end0")]
    [TestCase("br0", "br0")]
    [TestCase("tailscale0", "tailscale0")]
    [TestCase("ap0", "ap0")]
    public void Physical_OrVpn_IsKept(string name, string description) {
        Assert.That(MdnsService.IsVirtualAdapter(name, description), Is.False);
    }
}
