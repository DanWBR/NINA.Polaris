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

/// <summary>Hotspot or Station. A Raspberry Pi serving the hotspot reported
/// its own AP as "Infra" in the cached scan list, and the WiFi card said
/// Station on Polaris-Hotspot-91F3 at 10.42.0.1.</summary>
[TestFixture]
public class NetworkModeResolveTests {
    [Test]
    public void TheConfiguredApMode_WinsOverAnInfraScanRow() {
        Assert.That(NetworkManagerService.ResolveMode("polaris-hotspot", "Infra", "Polaris-Hotspot-91F3", "ap"),
            Is.EqualTo(WifiMode.Hotspot));
    }

    [Test]
    public void OurHotspotProfile_IsAHotspotEvenWhenTheScanSaysInfra() {
        Assert.That(NetworkManagerService.ResolveMode("polaris-hotspot", "Infra", "Polaris-Hotspot-91F3"),
            Is.EqualTo(WifiMode.Hotspot));
    }

    [Test]
    public void AStationProfile_IsAStation() {
        Assert.That(NetworkManagerService.ResolveMode("Garden AP", "Infra", "Garden AP", "infrastructure"),
            Is.EqualTo(WifiMode.Station));
    }

    [Test]
    public void AnApProfileNotNamedLikeOurs_IsStillAHotspot() {
        Assert.That(NetworkManagerService.ResolveMode("my-own-ap", null, "Scope", "ap"),
            Is.EqualTo(WifiMode.Hotspot));
    }

    [Test]
    public void WithoutConfigOrName_TheScanColumnDecides() {
        Assert.Multiple(() => {
            Assert.That(NetworkManagerService.ResolveMode(null, "Ap", "X"), Is.EqualTo(WifiMode.Hotspot));
            Assert.That(NetworkManagerService.ResolveMode(null, "Infra", "X"), Is.EqualTo(WifiMode.Station));
        });
    }

    [Test]
    public void NoSsidAtAll_IsDisconnected() {
        Assert.That(NetworkManagerService.ResolveMode("polaris-hotspot", null, null, "ap"),
            Is.EqualTo(WifiMode.Disconnected));
    }
}
