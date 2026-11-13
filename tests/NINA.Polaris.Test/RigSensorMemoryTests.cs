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
/// Planning with nothing connected takes the sensor the active rig remembers,
/// not whichever camera was connected last on any rig.
/// </summary>
[TestFixture]
public class RigSensorMemoryTests {

    [Test]
    public void SensorMm_FromResolutionAndPixelSize() {
        // ASI2600: 6248 x 4176 at 3.76 um
        var (w, h) = RigSensorMemoryService.SensorMm(6248, 4176, 3.76, 3.76);
        Assert.That(w, Is.EqualTo(23.49).Within(0.01));
        Assert.That(h, Is.EqualTo(15.70).Within(0.01));
        Assert.That(RigSensorMemoryService.SensorMm(6248, 4176, 0, 0), Is.EqualTo((0.0, 0.0)));
        Assert.That(RigSensorMemoryService.SensorMm(0, 0, 3.76, 3.76), Is.EqualTo((0.0, 0.0)));
        // A driver that reports only the X pixel size: square pixels.
        Assert.That(RigSensorMemoryService.SensorMm(1000, 500, 4.0, 0).h, Is.EqualTo(2.0).Within(1e-9));
    }

    [Test]
    public void NeedsUpdate_IgnoresDriverRounding() {
        Assert.That(RigSensorMemoryService.NeedsUpdate(23.49, 15.70, 23.4925, 15.7018), Is.False);
        Assert.That(RigSensorMemoryService.NeedsUpdate(0, 0, 23.49, 15.70), Is.True);
        Assert.That(RigSensorMemoryService.NeedsUpdate(23.49, 15.70, 11.13, 6.26), Is.True);
    }

    [Test]
    public void Resolve_NoCamera_UsesTheRig_ThenTheProfile() {
        var profile = new UserProfile { SensorWidthMm = 36, SensorHeightMm = 24 };
        var rig = new EquipmentProfile { SensorWidthMm = 11.13, SensorHeightMm = 6.26, CameraPixelSizeUm = 2.9 };
        var s = RigSensorMemoryService.Resolve(null, rig, profile);
        Assert.That((s.WidthMm, s.HeightMm, s.PixelUm, s.Source), Is.EqualTo((11.13, 6.26, 2.9, "rig")));

        var unknown = new EquipmentProfile();
        s = RigSensorMemoryService.Resolve(null, unknown, profile);
        Assert.That((s.WidthMm, s.HeightMm, s.Source), Is.EqualTo((36.0, 24.0, "default")));
    }

    [Test]
    public void Fov_FromSensorAndFocalLength() {
        var s = new PlanningSensor(23.49, 15.70, 3.76, "rig");
        var (w, h) = s.FovDeg(480);
        Assert.That(w, Is.EqualTo(2.80).Within(0.01));
        Assert.That(h, Is.EqualTo(1.87).Within(0.01));
    }
}
