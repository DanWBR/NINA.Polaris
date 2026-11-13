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

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.INDI.Client;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// A vendor SDK is not rescanned while one of its cameras is open. The
/// picker of the other camera must still offer it: the cameras of the last
/// scan and the rig's saved ones are listed with the live one.
/// </summary>
[TestFixture]
public class SeenCamerasTests {

    private static EquipmentManager Make() => new(new IndiClient("localhost", 7624),
        NullLogger<EquipmentManager>.Instance,
        new NINA.Polaris.Services.Alpaca.AlpacaDiscoveryCache(),
        new NINA.Polaris.Services.Simulator.Gear.SimGearService());

    [Test]
    public void GuideConnected_MainCameraOfTheLastScanIsStillListed() {
        var equip = Make();
        equip.RememberSeen("svbony-sdk", new[] {
            new DiscoveredCamera("SN-MAIN", "SV405CC", "SN-MAIN"),
            new DiscoveredCamera("SN-GUIDE", "SV305", "SN-GUIDE"),
        });
        var live = new List<DiscoveredCamera> { new("SN-GUIDE", "SV305", "connected") };

        var list = equip.KnownCameras("svbony-sdk", live, new string?[] { "SN-MAIN", "SN-GUIDE" });

        Assert.That(list.Select(c => c.Id), Is.EquivalentTo(new[] { "SN-GUIDE", "SN-MAIN" }));
        Assert.That(list.Single(c => c.Id == "SN-GUIDE").Detail, Is.EqualTo("connected"));
        Assert.That(list.Single(c => c.Id == "SN-MAIN").Model, Is.EqualTo("SV405CC"));
    }

    [Test]
    public void NoScanYet_TheRigsSavedCameraIsListed() {
        var equip = Make();
        var live = new List<DiscoveredCamera> { new("SN-GUIDE", "SV305", "connected") };

        var list = equip.KnownCameras("svbony-sdk", live, new string?[] { "SN-MAIN", null });

        Assert.That(list.Select(c => c.Id), Is.EqualTo(new[] { "SN-GUIDE", "SN-MAIN" }));
        Assert.That(list[1].Detail, Is.EqualTo("saved on this rig"));
    }

    [Test]
    public void ScansAreKeptPerDriver() {
        var equip = Make();
        equip.RememberSeen("playerone-sdk", new[] { new DiscoveredCamera("P1", "Neptune", "") });
        var list = equip.KnownCameras("svbony-sdk", new List<DiscoveredCamera>(), new string?[0]);
        Assert.That(list, Is.Empty);
    }

    [TestCase("svbony-sdk", true)]
    [TestCase("touptek-sdk", true)]
    [TestCase("zwo-sdk", false)]
    [TestCase("indi", false)]
    [TestCase("alpaca", false)]
    public void OnlyVendorSdksNeedTheMemory(string driver, bool expected) {
        Assert.That(EquipmentManager.RemembersSeenCameras(driver), Is.EqualTo(expected));
    }
}
