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

using System;
using System.IO;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Joining a WiFi network from hotspot mode.
///
/// <para>One radio cannot beacon its own access point and associate with
/// another network at the same time. <c>ScanAsync</c> has always known this:
/// it takes the AP down for the scan, because otherwise the list comes back
/// holding nothing but the hotspot itself. The join next to it never learned
/// the same thing, so <c>nmcli connection up polaris-station</c> sat there
/// while the AP owned the interface until the 35 second timeout.</para>
///
/// <para>Measured on an OPi 4 Pro: with the hotspot up, a scan saw one network,
/// its own. With the hotspot down, the target was there at 100 percent signal
/// on two bands, and the station profile that had been failing associated and
/// took a DHCP lease in under two seconds. The operator had been told "likely
/// bad password / AP out of range" on every attempt.</para>
/// </summary>
[TestFixture]
public class WifiStationSwitchTests {

    private static string Here([CallerFilePath] string p = "") => p;

    private static string Service() {
        var here = Path.GetDirectoryName(Here())!;
        var path = Path.GetFullPath(Path.Combine(here, "..", "..",
            "src", "NINA.Polaris", "Services", "NetworkManagerService.cs"));
        Assert.That(File.Exists(path), $"nao achei {path}");
        return File.ReadAllText(path);
    }

    /// <summary>The order is the whole fix: the AP has to be gone before the
    /// station is asked to come up, not after it has timed out.</summary>
    [Test]
    public void TheHotspotGoesDownBeforeTheStationComesUp() {
        var src = Service();
        var method = src.IndexOf("public async Task<SwitchResult> SwitchToStationAsync",
            StringComparison.Ordinal);
        Assert.That(method, Is.GreaterThanOrEqualTo(0), "SwitchToStationAsync moved");

        var apDown = src.IndexOf("\"connection down polaris-hotspot\"", method, StringComparison.Ordinal);
        var stationUp = src.IndexOf("\"connection up polaris-station\"", method, StringComparison.Ordinal);

        Assert.Multiple(() => {
            Assert.That(apDown, Is.GreaterThanOrEqualTo(0),
                "the join never takes the AP down, so the radio is still beaconing "
                + "when the station is asked to associate");
            Assert.That(stationUp, Is.GreaterThan(apDown),
                "the AP has to go down FIRST");
        });
    }

    /// <summary>Only when there is an AP to take down. A rig already in station
    /// mode, or one that has never been a hotspot, has nothing to drop.</summary>
    [Test]
    public void TheApIsOnlyDroppedWhenItWasActuallyUp() {
        Assert.That(Service(), Does.Contain("if (hotspotWasUp) {"));
    }

    /// <summary>"Process timed out" is not a wrong password, and telling the
    /// operator it might be sends them to re-type a password that was right.
    /// This is the message that cost a field session.</summary>
    [Test]
    public void ATimeoutDoesNotReportItselfAsABadPassword() {
        var src = Service();
        Assert.Multiple(() => {
            Assert.That(src, Does.Contain("did not finish associating within 35s"));
            Assert.That(src, Does.Contain(@"err.Contains(""timed out"", StringComparison.OrdinalIgnoreCase)"));
        });
    }

    /// <summary>The scan half, which was already right, and has to stay that
    /// way: it is the other place a single radio cannot do two things at
    /// once.</summary>
    [Test]
    public void TheScanStillPausesTheHotspotToSeeAnything() {
        var src = Service();
        var scan = src.IndexOf("public async Task<List<WifiNetwork>> ScanAsync(bool pauseHotspot",
            StringComparison.Ordinal);
        Assert.That(scan, Is.GreaterThanOrEqualTo(0));
        Assert.That(src.IndexOf("\"connection down polaris-hotspot\"", scan, StringComparison.Ordinal),
            Is.GreaterThan(scan));
    }
}
