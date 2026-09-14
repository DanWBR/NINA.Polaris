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
/// The arithmetic behind driving a rotator from a plate solve inside the
/// slew-and-center loop: the sky-angle error folded onto (-90, 90] and the
/// mechanical angle that applies a sky delta in either direction.
/// </summary>
[TestFixture]
public class SlewCenterRotationTests {

    [TestCase(30.0, 20.0, 10.0)]
    [TestCase(20.0, 30.0, -10.0)]
    [TestCase(0.0, 350.0, 10.0)]
    [TestCase(350.0, 0.0, -10.0)]
    [TestCase(200.0, 20.0, 0.0)]      // the same framing, sensor upside down
    [TestCase(95.0, 0.0, -85.0)]      // 95 forwards is 85 back the other way round
    [TestCase(90.0, 0.0, 90.0)]       // exactly a quarter turn keeps the positive fold
    [TestCase(270.0, 0.0, 90.0)]
    [TestCase(45.5, 45.2, 0.3)]
    public void RotationError_FoldsOntoTheShorterWay(double target, double solved, double expected) {
        Assert.That(SlewCenterService.RotationErrorDeg(target, solved), Is.EqualTo(expected).Within(1e-9));
    }

    [TestCase(10.0, 5.0, 1, 15.0)]
    [TestCase(10.0, 5.0, -1, 5.0)]
    [TestCase(358.0, 5.0, 1, 3.0)]
    [TestCase(2.0, 5.0, -1, 357.0)]
    [TestCase(2.0, -5.0, 1, 357.0)]
    [TestCase(180.0, -180.0, 1, 0.0)]
    public void NextMechanicalAngle_AppliesTheDeltaAndWraps(double current, double delta, int sign, double expected) {
        Assert.That(SlewCenterService.NextMechanicalAngle(current, delta, sign), Is.EqualTo(expected).Within(1e-9));
    }

    [Test]
    public void ErrorAndAngle_Compose_ToLandOnTheTarget() {
        // Sky 20, want 30, rotator at 100 and moving with the sky: one move lands it.
        double err = SlewCenterService.RotationErrorDeg(30, 20);
        Assert.That(SlewCenterService.NextMechanicalAngle(100, err, 1), Is.EqualTo(110).Within(1e-9));
        // Opposed rotator: the same error moves the other way.
        Assert.That(SlewCenterService.NextMechanicalAngle(100, err, -1), Is.EqualTo(90).Within(1e-9));
    }
}
