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
/// The guard that notices a night has been shot through a crop.
///
/// The case it was rebuilt from: 60 subs of M31 off an ASI183MM Pro on
/// indi_asi_ccd came out 5472x3672 where the IMX183 is 5496x3672, and nothing
/// warned. The check compared the frame against the driver's own CCD_INFO, so
/// if that number is the region rather than the sensor, the reference agrees
/// with the crop and the guard is silent by construction. It now takes the
/// larger of the driver's reading and the sensor size configured on the rig,
/// which is the operator's statement about the hardware and does not move when
/// a region does.
/// </summary>
[TestFixture]
public class CroppedSensorGuardTests {

    // The real numbers from that night.
    private const int SensorW = 5496, SensorH = 3672;
    private const int ShotW = 5472, ShotH = 3672;

    [Test]
    public void TheFieldCaseIsCaught() {
        Assert.That(ImageWriterService.IsCroppedSensorFrame(ShotW, ShotH, SensorW, SensorH),
            Is.True, "24 px missing in x is still a crop");
    }

    /// <summary>Why the guard was quiet: with the driver's number equal to the
    /// crop there is nothing to compare against, which is precisely why the
    /// rig's configured size now takes part.</summary>
    [Test]
    public void AReferenceThatAgreesWithTheCropCannotCatchIt() {
        Assert.That(ImageWriterService.IsCroppedSensorFrame(ShotW, ShotH, ShotW, ShotH),
            Is.False, "this is the blind spot, stated so it is not mistaken for a pass");

        // ...and the fix: the rig's number wins, so the comparison happens.
        var reference = ImageWriterService.LargerSensorSide(ShotW, SensorW);
        Assert.That(reference, Is.EqualTo(SensorW));
        Assert.That(ImageWriterService.IsCroppedSensorFrame(ShotW, ShotH, reference, SensorH),
            Is.True);
    }

    [Test]
    public void AFullFrameIsNotAWarning() {
        Assert.That(ImageWriterService.IsCroppedSensorFrame(SensorW, SensorH, SensorW, SensorH),
            Is.False);
    }

    /// <summary>A binned frame is smaller on purpose. The caller divides the
    /// reference by the binning before asking, so the guard only ever sees
    /// like-for-like; this pins that a 2x2 read of the same sensor is not
    /// reported once that division has happened.</summary>
    [Test]
    public void BinningIsNotACrop() {
        Assert.That(ImageWriterService.IsCroppedSensorFrame(
            SensorW / 2, SensorH / 2, SensorW / 2, SensorH / 2), Is.False);
    }

    /// <summary>A driver that has not published its geometry yet is not
    /// evidence of anything, and neither is a rig field left empty.</summary>
    [TestCase(0, 0)]
    [TestCase(-1, -1)]
    public void AnUnknownSensorSizeIsNotEvidence(int w, int h) {
        Assert.That(ImageWriterService.IsCroppedSensorFrame(ShotW, ShotH, w, h), Is.False);
    }

    [TestCase(0, 5496, 5496)]
    [TestCase(5496, 0, 5496)]
    [TestCase(0, 0, 0)]
    [TestCase(-5, 5496, 5496)]
    [TestCase(5472, 5496, 5496)]
    [TestCase(5496, 5472, 5496)]
    public void LargerSensorSideIgnoresMissingReadings(int a, int b, int expected) {
        Assert.That(ImageWriterService.LargerSensorSide(a, b), Is.EqualTo(expected));
    }

    /// <summary>Calibration frames are a full read of the sensor too, so a crop
    /// on a flat or a dark matters as much as on a light. A stacked master is
    /// not, since it can legitimately be any size.</summary>
    [TestCase("LIGHT", true)]
    [TestCase("Light Frame", true)]
    [TestCase("DARK", true)]
    [TestCase("FLAT", true)]
    [TestCase("BIAS", true)]
    [TestCase("Flat Field", true)]
    [TestCase("SNAP", false)]
    [TestCase("MASTERLIGHT", false)]
    [TestCase("", true)]
    [TestCase(null, true)]
    public void OnlyFullSensorFrameTypesAreChecked(string? imageType, bool expected) {
        Assert.That(ImageWriterService.IsFullSensorFrameType(imageType), Is.EqualTo(expected));
    }
}
