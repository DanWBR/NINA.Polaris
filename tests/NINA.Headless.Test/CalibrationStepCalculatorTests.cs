using NUnit.Framework;
using NINA.Headless.Services;

namespace NINA.Headless.Test;

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
