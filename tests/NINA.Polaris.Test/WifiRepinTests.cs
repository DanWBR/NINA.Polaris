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

/// <summary>An external USB WiFi adapter chosen for the hotspot pins the
/// hotspot and the saved networks to it. When it did not come up at boot, the
/// fallback failed with "No suitable device found ... mismatching interface
/// name" and the rig had no network, with the onboard radio free.</summary>
[TestFixture]
public class WifiRepinTests {
    [Test]
    public void APinToTheMissingAdapterIsMovedToTheOneInUse() {
        Assert.That(NetworkManagerService.NeedsRepin("wlan1", "wlan0"), Is.True);
    }

    [Test]
    public void APinToTheAdapterInUseIsLeftAlone() {
        Assert.That(NetworkManagerService.NeedsRepin("wlan0", "wlan0"), Is.False);
        Assert.That(NetworkManagerService.NeedsRepin("WLAN0", "wlan0"), Is.False);
    }

    [Test]
    public void AnUnpinnedConnectionAlreadyFollowsAnyRadio() {
        Assert.That(NetworkManagerService.NeedsRepin("", "wlan0"), Is.False);
        Assert.That(NetworkManagerService.NeedsRepin("  ", "wlan0"), Is.False);
    }

    [Test]
    public void WhenTheAdapterIsBackTheConnectionsGoBackToIt() {
        // The saved choice is still wlan1, so detection selects it again and
        // the connections moved to wlan0 at boot are moved back.
        Assert.That(NetworkManagerService.NeedsRepin("wlan0", "wlan1"), Is.True);
    }
}
