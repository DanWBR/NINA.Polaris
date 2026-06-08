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

[TestFixture]
public class CalibrationStepCalculatorTests {

    [Test]
    public void Compute_TypicalRig_GivesReasonableStep() {
        // Pixel scale 2.1"/px, guide rate 7.5"/s (0.5x sidereal),
        // distance 25 px → 7000 ms wanted? Let's see:
        // step = round(25 * 2.1 / 7.5 * 1000) = round(7000) = 7000
        // but our cap is 3000 ms. So we expect 3000.
        var step = CalibrationStepCalculator.Compute(
            pixelScaleArcsecPerPx: 2.1, guideRateArcsecPerSec: 7.5);
        Assert.That(step, Is.EqualTo(CalibrationStepCalculator.MaxStepMs));
    }

    [Test]
    public void Compute_ShortFlSmallPxScale_GivesShortStep() {
        // Pixel scale 1.0"/px, guide rate 7.5"/s, 25px = ~3333ms capped to 3000
        // Smaller: 0.5"/px, 7.5"/s, 25px = ~1666ms (not capped)
        var step = CalibrationStepCalculator.Compute(0.5, 7.5);
        Assert.That(step, Is.EqualTo(1667));
    }

    [Test]
    public void Compute_TinyPxScale_HitsMinFloor() {
        // 0.05"/px guide rate 7.5"/s 25px = 166ms → clamped up to 250
        var step = CalibrationStepCalculator.Compute(0.05, 7.5);
        Assert.That(step, Is.EqualTo(CalibrationStepCalculator.MinStepMs));
    }

    [TestCase(0.0, 7.5)]
    [TestCase(2.0, 0.0)]
    [TestCase(-1.0, 7.5)]
    public void Compute_InvalidInputs_FallsBackToMin(double pxScale, double guideRate) {
        var step = CalibrationStepCalculator.Compute(pxScale, guideRate);
        Assert.That(step, Is.EqualTo(CalibrationStepCalculator.MinStepMs));
    }

    [Test]
    public void Compute_CustomDistance_ScalesLinearly() {
        // Doubling the distance should ~double the step (subject to caps).
        var a = CalibrationStepCalculator.Compute(0.3, 7.5, 10);  // 400ms
        var b = CalibrationStepCalculator.Compute(0.3, 7.5, 20);  // 800ms
        Assert.That(b, Is.EqualTo(a * 2).Within(2));
    }
}