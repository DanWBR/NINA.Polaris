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
using NUnit.Framework;
using NINA.Guider.Portable;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// The orthogonality of a guide calibration. RA and Dec are perpendicular on
/// the sky, so the two axes a calibration measures have to come out about 90
/// degrees apart on the sensor. When they do not, the transform decomposes
/// every measured error into the wrong pair of pulses and the guider fights
/// itself.
///
/// These numbers are from a real session on an AM5 with an ASI678MC guide
/// camera: the calibration came out 26 degrees off orthogonal, was accepted
/// without a word, and then guided at 3.8 arcsec RMS in RA with 15 arcsec
/// peaks. The error was already being computed and displayed in Calibration
/// details; nothing looked at it.
/// </summary>
[TestFixture]
public class GuideCalibrationOrthogonalityTests {

    private static GuideCalibration Cal(double xAngleRad, double yAngleRad, double backlashMs = 0)
        => new(xAngleRad, yAngleRad, 0.0032, 0.0042, -0.7377, true, backlashMs);

    [Test]
    public void PerpendicularAxesHaveNoError() {
        Assert.That(Cal(0, Math.PI / 2).OrthogonalityErrorDeg, Is.EqualTo(0).Within(1e-9));
        Assert.That(Cal(Math.PI / 2, 0).OrthogonalityErrorDeg, Is.EqualTo(0).Within(1e-9));
        // A camera rotated by any amount is still orthogonal.
        Assert.That(Cal(1.234, 1.234 + Math.PI / 2).OrthogonalityErrorDeg,
            Is.EqualTo(0).Within(1e-9));
    }

    /// <summary>The session that prompted the check: xAngle -1.9744 rad
    /// (-113 deg) and yAngle -3.0907 rad (-177 deg), so the axes came out 64
    /// degrees apart.</summary>
    [Test]
    public void TheSkewedCalibrationFromTheBoardIsFlagged() {
        var cal = Cal(-1.9743879561329036, -3.0906759879385968, backlashMs: 2000);
        Assert.That(cal.OrthogonalityErrorDeg, Is.EqualTo(26.0).Within(0.2));
        Assert.That(cal.OrthogonalityErrorDeg,
            Is.GreaterThan(NativeGuider.MaxOrthogonalityErrorDeg),
            "this is the calibration that guided at 4 arcsec RMS in silence");
    }

    /// <summary>The one restored from file earlier the same night was skewed
    /// too, which is why reusing it did not help either.</summary>
    [Test]
    public void TheRestoredCalibrationWasAlsoSkewed() {
        Assert.That(Cal(-2.649, 2.372).OrthogonalityErrorDeg, Is.EqualTo(17.7).Within(0.2));
    }

    /// <summary>The error is a magnitude: which way the axes lean does not
    /// matter, and it must not jump when the difference wraps past 180.</summary>
    [Test]
    public void ErrorIsSymmetricAndWrapsCleanly() {
        double lean = Cal(0, Math.PI / 2 + 0.3).OrthogonalityErrorDeg;
        Assert.That(Cal(0, Math.PI / 2 - 0.3).OrthogonalityErrorDeg,
            Is.EqualTo(lean).Within(1e-9));
        // Axes either side of the +/-180 boundary: 170 and -100 are 90 apart.
        Assert.That(Cal(170 * Math.PI / 180, -100 * Math.PI / 180).OrthogonalityErrorDeg,
            Is.EqualTo(0).Within(1e-9));
    }

    /// <summary>A threshold worth having: tight enough to catch a calibration
    /// that cross-talks, loose enough not to cry over a good one. PHD2 uses
    /// the same 10 degrees.</summary>
    [Test]
    public void ThresholdLeavesRoomForAGoodCalibration() {
        Assert.That(NativeGuider.MaxOrthogonalityErrorDeg, Is.InRange(5.0, 15.0));
        Assert.That(Cal(0, Math.PI / 2 + 0.05).OrthogonalityErrorDeg,
            Is.LessThan(NativeGuider.MaxOrthogonalityErrorDeg),
            "3 degrees off is a normal measurement, not a fault");
    }
}
