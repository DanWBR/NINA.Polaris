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
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Reading the device list out of nmcli.
///
/// <para>This exists because of a field report from a clean Debian install: a
/// static stanza in /etc/network/interfaces handed the wireless adapter to
/// ifupdown, NetworkManager stepped aside, and Polaris counted the adapter as
/// available. Every hotspot attempt then failed with "no suitable device", the
/// log filled with nmcli errors, and the network card reported the host as
/// wired. Present is not the same as usable.</para>
/// </summary>
[TestFixture]
public class WifiUnmanagedTests {

    // Real output, nmcli 1.42 on Debian 13, with the adapter claimed by
    // ifupdown.
    private const string DebianUnmanaged = """
        lo:loopback:unmanaged
        enp1s0:ethernet:connected
        wlp2s0:wifi:unmanaged
        """;

    private const string Healthy = """
        wlan0:wifi:disconnected
        eth0:ethernet:connected
        lo:loopback:unmanaged
        """;

    [Test]
    public void AnUnmanagedAdapterIsSeenAndNamed() {
        var devices = NetworkManagerService.ParseWifiDevices(DebianUnmanaged);
        Assert.That(devices, Has.Count.EqualTo(1));
        Assert.That(devices[0].Name, Is.EqualTo("wlp2s0"));
        Assert.That(devices[0].IsUnmanaged, Is.True);
    }

    [Test]
    public void TheLoopbackBeingUnmanagedIsNotAWifiProblem() {
        // lo is unmanaged on every machine ever. Only wifi rows count.
        var devices = NetworkManagerService.ParseWifiDevices(Healthy);
        Assert.That(devices, Has.Count.EqualTo(1));
        Assert.That(devices[0].Name, Is.EqualTo("wlan0"));
        Assert.That(devices[0].IsUnmanaged, Is.False);
    }

    [Test]
    public void TheStateCanCarryItsReasonInBrackets() {
        // nmcli writes "unmanaged (explicitly unmanaged)" when the device is
        // listed in NetworkManager.conf, and the parser must not miss it for
        // the extra words.
        var devices = NetworkManagerService.ParseWifiDevices("wlan0:wifi:unmanaged (explicitly unmanaged)");
        Assert.That(devices[0].IsUnmanaged, Is.True);
    }

    [Test]
    public void AConnectedAdapterIsNotUnmanaged() {
        foreach (var state in new[] { "connected", "disconnected", "connecting", "unavailable" }) {
            var d = NetworkManagerService.ParseWifiDevices($"wlan0:wifi:{state}");
            Assert.That(d[0].IsUnmanaged, Is.False, state);
        }
    }

    [Test]
    public void AnAdapterListedTwiceIsOneAdapter() {
        var devices = NetworkManagerService.ParseWifiDevices("wlan0:wifi:disconnected\nwlan0:wifi:disconnected");
        Assert.That(devices, Has.Count.EqualTo(1));
    }

    [Test]
    public void OutputWithNoStateColumnStillYieldsTheAdapter() {
        // An older nmcli, or a call that asked for two fields. Missing the
        // state must not be read as unmanaged: that would disable WiFi on a
        // perfectly good host.
        var devices = NetworkManagerService.ParseWifiDevices("wlan0:wifi");
        Assert.That(devices, Has.Count.EqualTo(1));
        Assert.That(devices[0].IsUnmanaged, Is.False);
    }

    [Test]
    public void NothingAtAllIsAnEmptyList() {
        Assert.That(NetworkManagerService.ParseWifiDevices(null), Is.Empty);
        Assert.That(NetworkManagerService.ParseWifiDevices(""), Is.Empty);
        Assert.That(NetworkManagerService.ParseWifiDevices("lo:loopback:unmanaged"), Is.Empty);
    }

    [Test]
    public void AnEscapedColonInTheNameSurvives() {
        // The terse format escapes a colon inside a field, which is how a
        // device name with one arrives.
        var devices = NetworkManagerService.ParseWifiDevices(@"wl\:0:wifi:connected");
        Assert.That(devices, Has.Count.EqualTo(1));
        Assert.That(devices[0].Name, Does.Contain("wl"));
        Assert.That(devices[0].IsUnmanaged, Is.False);
    }
}
