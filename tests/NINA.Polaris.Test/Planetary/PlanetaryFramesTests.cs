using System;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Polaris.Services.Planetary;
using NUnit.Framework;

namespace NINA.Polaris.Test.Planetary;

/// <summary>
/// The per-frame primitives behind the planetary stacker: sub-pixel centroid
/// on luminance, sharpness that ranks seeing rather than exposure, and the
/// bilinear shift-and-accumulate that keeps a planet exactly where the
/// reference put it.
/// </summary>
[TestFixture]
public class PlanetaryFramesTests {
    private const int W = 96, H = 96;

    /// <summary>Gaussian disc of the given sigma at (cx, cy) on a pedestal, as
    /// a float luminance plane.</summary>
    private static float[] Disc(double cx, double cy, double sigma, float peak = 20000f, float pedestal = 3000f) {
        var f = new float[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++) {
                double d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                f[y * W + x] = pedestal + peak * (float)Math.Exp(-d2 / (2 * sigma * sigma));
            }
        return f;
    }

    [Test]
    public void Centroid_IsSubPixel_OnAPedestal() {
        var (x, y, above) = PlanetaryFrames.Centroid(Disc(40.3, 51.7, 4.0), W, H);
        Assert.That(above, Is.GreaterThan(20));
        Assert.That(x, Is.EqualTo(40.3).Within(0.05));
        Assert.That(y, Is.EqualTo(51.7).Within(0.05));
    }

    [Test]
    public void Centroid_EmptyFrame_FallsBackToCentre() {
        var flat = new float[W * H]; Array.Fill(flat, 3000f);
        var (x, y, above) = PlanetaryFrames.Centroid(flat, W, H);
        Assert.That(above, Is.EqualTo(0));
        Assert.That((x, y), Is.EqualTo((W / 2.0, H / 2.0)));
    }

    [Test]
    public void Sharpness_RanksSharpAboveBlurred_RegardlessOfExposure() {
        double sharp = PlanetaryFrames.Sharpness(Disc(48, 48, 3.0, peak: 8000f), W, H, 48, 48, 48);
        double blurred = PlanetaryFrames.Sharpness(Disc(48, 48, 6.0, peak: 30000f), W, H, 48, 48, 48);
        Assert.That(sharp, Is.GreaterThan(blurred));
    }

    [Test]
    public void AccumulateShifted_RecentresAFractionalOffset_AndKeepsTheEdge() {
        var reference = Disc(48.0, 48.0, 3.0);
        var moved = Disc(48.0 + 1.75, 48.0 - 0.6, 3.0);         // the same planet, 1.75 px right, 0.6 px up
        var (mx, my, _) = PlanetaryFrames.Centroid(moved, W, H);
        var acc = new float[W * H]; var wgt = new float[W * H];
        PlanetaryFrames.AccumulateShifted(moved, W, H, 48.0 - mx, 48.0 - my, acc, wgt);
        var stacked = new float[W * H];
        for (int i = 0; i < stacked.Length; i++) stacked[i] = wgt[i] > 0 ? acc[i] / wgt[i] : 0;

        var (sx, sy, _) = PlanetaryFrames.Centroid(stacked, W, H);
        Assert.That(sx, Is.EqualTo(48.0).Within(0.1));
        Assert.That(sy, Is.EqualTo(48.0).Within(0.1));
        // bilinear resampling of a sigma-3 disc costs almost nothing in sharpness
        double s0 = PlanetaryFrames.Sharpness(reference, W, H, 48, 48, 48);
        double s1 = PlanetaryFrames.Sharpness(stacked, W, H, 48, 48, 48);
        Assert.That(s1, Is.GreaterThan(0.85 * s0));
    }

    [Test]
    public void AccumulateShifted_IntegerShift_IsExact() {
        var src = new float[W * H]; src[10 * W + 20] = 100f;
        var acc = new float[W * H]; var wgt = new float[W * H];
        PlanetaryFrames.AccumulateShifted(src, W, H, 3, -2, acc, wgt);
        Assert.That(acc[8 * W + 23], Is.EqualTo(100f));
        Assert.That(wgt[8 * W + 23], Is.EqualTo(1f));
        Assert.That(acc[10 * W + 20], Is.EqualTo(0f));
    }

    [Test]
    public void Split_Mono_SharesOnePlane_AndFinishAverages() {
        var frame = new ushort[16]; for (int i = 0; i < 16; i++) frame[i] = (ushort)(i * 100);
        var p = PlanetaryFrames.Split(frame, 4, 4, BayerPatternEnum.None);
        Assert.That(p.Mono, Is.True);
        Assert.That(p.Lum[5], Is.EqualTo(500f));
        var acc = new float[] { 300f, 0f }; var wgt = new float[] { 3f, 0f };
        var o = PlanetaryFrames.Finish(acc, wgt);
        Assert.That(o, Is.EqualTo(new ushort[] { 100, 0 }));
    }

    [Test]
    public void Split_Bayer_ProducesThreePlanesAndLuminance() {
        var frame = new ushort[16 * 16]; Array.Fill(frame, (ushort)1000);
        var p = PlanetaryFrames.Split(frame, 16, 16, BayerPatternEnum.RGGB);
        Assert.That(p.Mono, Is.False);
        Assert.That(p.Lum[8 * 16 + 8], Is.EqualTo(1000f).Within(1f));
        Assert.That(p.R[8 * 16 + 8], Is.EqualTo(1000f).Within(1f));
    }
}
