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

using System.Collections.Concurrent;
using System.Collections.Generic;
using NUnit.Framework;
using NINA.INDI.Client;
using NINA.INDI.Devices;
using NINA.INDI.Protocol;

namespace NINA.Polaris.Test;

/// <summary>
/// Roll-off roof drivers (rolloffino) declare DOME_PARK and no DOME_SHUTTER:
/// unpark opens the roof, park closes it. The shutter status and commands go
/// through the park property for them.
/// </summary>
[TestFixture]
public class RollOffRoofDomeTests {
    private const string Roof = "RollOff ino";

    private static ConcurrentDictionary<string, IndiProperty> Props(IndiClient c, string device) =>
        c.Devices.GetOrAdd(device, _ => new ConcurrentDictionary<string, IndiProperty>());

    private static void SetPark(IndiClient c, bool parked, IndiPropertyState state = IndiPropertyState.Ok) {
        Props(c, Roof)["DOME_PARK"] = new IndiSwitchProperty {
            Device = Roof,
            Name = "DOME_PARK",
            State = state,
            Values = new Dictionary<string, bool> { ["PARK"] = parked, ["UNPARK"] = !parked }
        };
    }

    [Test]
    public void ParkWithoutShutter_IsARollOffRoof() {
        using var c = new IndiClient();
        SetPark(c, parked: true);
        Assert.That(IndiDome.IsRollOff(c, Roof), Is.True);
    }

    [Test]
    public void DomeWithShutter_IsNotARollOffRoof() {
        using var c = new IndiClient();
        SetPark(c, parked: true);
        Props(c, Roof)["DOME_SHUTTER"] = new IndiSwitchProperty {
            Device = Roof,
            Name = "DOME_SHUTTER",
            Values = new Dictionary<string, bool> { ["SHUTTER_OPEN"] = false, ["SHUTTER_CLOSE"] = true }
        };
        Assert.That(IndiDome.IsRollOff(c, Roof), Is.False);
    }

    [Test]
    public void DeviceWithoutProperties_IsNotARollOffRoof() {
        using var c = new IndiClient();
        Assert.That(IndiDome.IsRollOff(c, Roof), Is.False);
    }

    [TestCase(true, IndiPropertyState.Ok, IndiDome.ShutterState.Closed)]
    [TestCase(false, IndiPropertyState.Ok, IndiDome.ShutterState.Open)]
    [TestCase(true, IndiPropertyState.Busy, IndiDome.ShutterState.Closing)]
    [TestCase(false, IndiPropertyState.Busy, IndiDome.ShutterState.Opening)]
    public void RoofState_FollowsThePark(bool parked, IndiPropertyState state, IndiDome.ShutterState expected) {
        using var c = new IndiClient();
        SetPark(c, parked, state);
        using var dome = new IndiDome(c, Roof);
        Assert.That(dome.ShutterStatus, Is.EqualTo(expected));
    }
}
