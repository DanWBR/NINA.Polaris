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

using NINA.Image.ImageAnalysis;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class LiveStackFrameNormTests {
    private const int W = 600, H = 400;

    private static ushort[] Sky(double level, double sigma, int seed, int zeroBorder = 0) {
        var rnd = new Random(seed);
        var a = new ushort[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++) {
                if (x < zeroBorder) continue;     // off-canvas after a warp
                double g = Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
                a[y * W + x] = (ushort)Math.Clamp(level + sigma * g, 1, 65535);
            }
        return a;
    }

    [Test]
    public void Planes_MeasureEachBackgroundAndTheGreenNoise() {
        var st = LiveStackFrameNorm.ForPlanes(new[] { Sky(1200, 10, 1), Sky(1500, 20, 2), Sky(900, 15, 3) }, W, H);
        Assert.That(st.Background[0], Is.EqualTo(1200).Within(2));
        Assert.That(st.Background[1], Is.EqualTo(1500).Within(2));
        Assert.That(st.Background[2], Is.EqualTo(900).Within(2));
        Assert.That(st.Noise, Is.EqualTo(20).Within(1.5));
    }

    // The empty border a warp leaves must not drag the background down.
    [Test]
    public void ZeroBorder_IsIgnored() {
        var st = LiveStackFrameNorm.ForPlanes(new[] { Sky(1300, 10, 4, zeroBorder: 200) }, W, H, noisePlane: 0);
        Assert.That(st.Background[0], Is.EqualTo(1300).Within(2));
    }

    [Test]
    public void Mosaic_MeasuresEachCfaSite() {
        var a = new ushort[W * H];
        var levels = new[] { 1000.0, 2400.0, 2500.0, 1010.0 };   // GRBG: G R / B G
        var rnd = new Random(5);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                a[y * W + x] = (ushort)(levels[(y & 1) * 2 + (x & 1)] + rnd.Next(-8, 9));
        var st = LiveStackFrameNorm.ForMosaic(a, W, H, cfa: true);
        for (int k = 0; k < 4; k++) Assert.That(st.Background[k], Is.EqualTo(levels[k]).Within(2), $"site {k}");
    }

    [Test]
    public void Subtract_ShiftsData_KeepsEmptyPixelsEmpty_NeverCreatesThem() {
        var p = new ushort[] { 0, 1500, 1200, 30, 0 };
        LiveStackFrameNorm.Subtract(p, 200);
        Assert.That(p, Is.EqualTo(new ushort[] { 0, 1300, 1000, 1, 0 }));
        LiveStackFrameNorm.Subtract(p, -100);
        Assert.That(p, Is.EqualTo(new ushort[] { 0, 1400, 1100, 101, 0 }));
    }

    [Test]
    public void SubtractMosaic_UsesEachSitesOffset() {
        var a = new ushort[4 * 2];
        Array.Fill(a, (ushort)2000);
        LiveStackFrameNorm.SubtractMosaic(a, 4, 2, new[] { 100.0, 200.0, 300.0, 400.0 });
        Assert.That(a, Is.EqualTo(new ushort[] { 1900, 1800, 1900, 1800, 1700, 1600, 1700, 1600 }));
    }

    // A cloudy frame: three times the sky, so ~1.7x the noise, stars a bit dimmer.
    [Test]
    public void Weight_FallsWithNoiseAndWithDimmerStars() {
        Assert.That(LiveStackFrameNorm.Weight(10, 10, 5000, 5000), Is.EqualTo(1).Within(1e-9));
        double cloudy = LiveStackFrameNorm.Weight(10, 17.3, 5000, 4000);
        Assert.That(cloudy, Is.EqualTo((10 / 17.3) * (10 / 17.3) * 0.64).Within(1e-6));
        Assert.That(cloudy, Is.LessThan(0.25));
        Assert.That(LiveStackFrameNorm.Weight(10, 8, 5000, 5000), Is.GreaterThan(1), "a cleaner frame than the reference");
    }

    [Test]
    public void Weight_IsClamped_AndOneWhenNothingIsMeasured() {
        Assert.That(LiveStackFrameNorm.Weight(10, 1000, 5000, 100), Is.EqualTo(LiveStackFrameNorm.MinWeight));
        Assert.That(LiveStackFrameNorm.Weight(10, 0.1, 5000, 5000), Is.EqualTo(LiveStackFrameNorm.MaxWeight));
        Assert.That(LiveStackFrameNorm.Weight(0, 0, 0, 0), Is.EqualTo(1));
    }

    [Test]
    public void StarSignal_IsTheBrightStarsMedian_NotTheFaintEnd() {
        var stars = Enumerable.Range(0, 90).Select(i => new DetectedStar { Flux = i < 30 ? 10_000 + i : 50 }).ToList();
        Assert.That(LiveStackFrameNorm.StarSignal(stars), Is.EqualTo(10_015).Within(1));
        Assert.That(LiveStackFrameNorm.StarSignal(new List<DetectedStar>()), Is.EqualTo(0));
    }
}
