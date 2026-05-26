using NUnit.Framework;
using NINA.Image.ImageAnalysis;

namespace NINA.Polaris.Test.Editor;

/// <summary>
/// Pins ColorSpace conversions. The pipeline uses these on every pixel
/// for vibrance/saturation/hue, regressions silently shift the whole
/// colour palette of every preview the user sees, so these are worth
/// hard-pinning.
/// </summary>
[TestFixture]
public class ColorSpaceTests {

    [Test]
    public void RgbToHsl_Grayscale_ZeroSaturation() {
        var (h, s, l) = ColorSpace.RgbToHsl(0.5, 0.5, 0.5);
        Assert.That(s, Is.EqualTo(0).Within(1e-6));
        Assert.That(l, Is.EqualTo(0.5).Within(1e-6));
    }

    [Test]
    public void RgbToHsl_PureRed_Hue0Sat1() {
        var (h, s, l) = ColorSpace.RgbToHsl(1, 0, 0);
        Assert.That(h, Is.EqualTo(0).Within(1e-4));
        Assert.That(s, Is.EqualTo(1).Within(1e-6));
        Assert.That(l, Is.EqualTo(0.5).Within(1e-6));
    }

    [Test]
    public void RgbToHsl_PureGreen_Hue120() {
        var (h, _, _) = ColorSpace.RgbToHsl(0, 1, 0);
        Assert.That(h, Is.EqualTo(120).Within(1e-4));
    }

    [Test]
    public void RgbToHsl_PureBlue_Hue240() {
        var (h, _, _) = ColorSpace.RgbToHsl(0, 0, 1);
        Assert.That(h, Is.EqualTo(240).Within(1e-4));
    }

    [Test]
    public void HslToRgb_Identity_PureColours() {
        var (r, g, b) = ColorSpace.HslToRgb(0, 1, 0.5);
        Assert.That(r, Is.EqualTo(1).Within(1e-6));
        Assert.That(g, Is.EqualTo(0).Within(1e-6));
        Assert.That(b, Is.EqualTo(0).Within(1e-6));
    }

    [Test]
    public void Roundtrip_PreservesColour_Within8BitTolerance() {
        // 8-bit roundtrip (the editor's pipeline lives in 8-bit). Tolerance
        // of ±1 covers the worst floating-point + clamp drift.
        var rng = new Random(42);
        for (int trial = 0; trial < 200; trial++) {
            double r0 = rng.NextDouble();
            double g0 = rng.NextDouble();
            double b0 = rng.NextDouble();
            var (h, s, l) = ColorSpace.RgbToHsl(r0, g0, b0);
            var (r1, g1, b1) = ColorSpace.HslToRgb(h, s, l);
            Assert.That(Math.Abs(r0 - r1) * 255, Is.LessThanOrEqualTo(1),
                $"R drift > 1 for ({r0:F3},{g0:F3},{b0:F3})");
            Assert.That(Math.Abs(g0 - g1) * 255, Is.LessThanOrEqualTo(1));
            Assert.That(Math.Abs(b0 - b1) * 255, Is.LessThanOrEqualTo(1));
        }
    }

    [Test]
    public void TempTintToGain_Neutral6500_NearUnity() {
        var (rG, gG, bG) = ColorSpace.TempTintToGain(6500, 0);
        // Within 5% of 1.0, the McCamy fit isn't exactly 6500K=D65 but
        // close enough that the slider feels neutral at the default.
        Assert.That(rG, Is.EqualTo(1).Within(0.05));
        Assert.That(gG, Is.EqualTo(1).Within(1e-6), "Green always normalised to 1");
        Assert.That(bG, Is.EqualTo(1).Within(0.05));
    }

    [Test]
    public void TempTintToGain_ColdSkyBoost_BlueGainHigh() {
        // 10000K (cool / blue), blue gain should be > 1, red < 1.
        var (rG, _, bG) = ColorSpace.TempTintToGain(10000, 0);
        Assert.That(bG, Is.GreaterThan(rG), "Cooler colour → blue dominant");
    }

    [Test]
    public void TempTintToGain_WarmTungsten_RedGainHigh() {
        var (rG, _, bG) = ColorSpace.TempTintToGain(3200, 0);
        Assert.That(rG, Is.GreaterThan(bG), "Warmer colour → red dominant");
    }

    [Test]
    public void Luminance_Rec709_GreenDominant() {
        // Pure green should produce the highest luminance (0.7152), red
        // the lowest of the three primaries (0.2126 < 0.7152).
        Assert.That(ColorSpace.Luminance(0, 1, 0), Is.GreaterThan(ColorSpace.Luminance(1, 0, 0)));
        Assert.That(ColorSpace.Luminance(0, 1, 0), Is.GreaterThan(ColorSpace.Luminance(0, 0, 1)));
    }
}
