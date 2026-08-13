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
using NINA.Image.ImageAnalysis;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// Collapsing a Bayer mosaic to luminance before auto-focus detects stars.
///
/// Field, 2026-08-13 (SV550 + ASI585MC): auto-focus reported no stars at all on
/// a field with plenty of them. The detector was being handed the raw CFA
/// mosaic, where a star's flux is divided among its R/G/G/B sites and the
/// same-colour pixels sit two apart, too far for even eight-connectivity to
/// join. A small star arrives as loose single pixels, each under MinStarSize
/// and each too dim on its own for the 5-sigma cut.
/// </summary>
[TestFixture]
public class BayerQuadBinningTests {

    [Test]
    public void EachQuadBecomesTheSumOfItsFour() {
        // 4x2 mosaic: two quads side by side.
        ushort[] src = {
            1, 2, 10, 20,
            3, 4, 30, 40,
        };
        var (data, w, h) = AutoFocusService.BinBayerQuads(src, 4, 2);

        Assert.That((w, h), Is.EqualTo((2, 1)));
        Assert.That(data, Is.EqualTo(new ushort[] { 10, 100 }));
    }

    [Test]
    public void TheResultIsHalfSizeInBothAxes() {
        var (_, w, h) = AutoFocusService.BinBayerQuads(new ushort[64 * 40], 64, 40);
        Assert.That((w, h), Is.EqualTo((32, 20)));
    }

    /// <summary>An odd dimension must not read past the row or crash; the
    /// spare column/row is simply dropped.</summary>
    [TestCase(5, 4, 2, 2)]
    [TestCase(4, 5, 2, 2)]
    [TestCase(7, 7, 3, 3)]
    public void OddDimensionsAreTruncated(int w, int h, int ew, int eh) {
        var (data, gw, gh) = AutoFocusService.BinBayerQuads(new ushort[w * h], w, h);
        Assert.That((gw, gh), Is.EqualTo((ew, eh)));
        Assert.That(data.Length, Is.EqualTo(ew * eh));
    }

    /// <summary>THE ONE THAT PROTECTS THE MEASUREMENT. Four bright pixels sum
    /// past 65535; wrapping would turn the brightest core in the frame into a
    /// near-black pixel and the star would vanish at exactly the focus
    /// positions where it is sharpest.</summary>
    [Test]
    public void ABrightQuadSaturatesInsteadOfWrapping() {
        ushort[] src = { 60000, 60000, 60000, 60000 };
        var (data, _, _) = AutoFocusService.BinBayerQuads(src, 2, 2);
        Assert.That(data[0], Is.EqualTo(ushort.MaxValue));
    }

    /// <summary>The point of the whole exercise: a star the detector cannot see
    /// in the mosaic becomes one it can. Same synthetic star, both paths.</summary>
    [Test]
    public void AStarLostInTheMosaicIsFoundAfterBinning() {
        const int W = 120, H = 120;
        var rng = new Random(1234);
        var mosaic = new ushort[W * H];
        for (int i = 0; i < mosaic.Length; i++) mosaic[i] = (ushort)(500 + rng.Next(40));

        // A compact star, spread over a CFA quad the way a real one is: the
        // green sites take most of it, red and blue rather less.
        void Put(int cx, int cy, double peak) {
            for (int dy = -2; dy <= 2; dy++) {
                for (int dx = -2; dx <= 2; dx++) {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= W || y >= H) continue;
                    double f = Math.Exp(-(dx * dx + dy * dy) / 2.0);
                    // RGGB: R at even/even, B at odd/odd, G on the diagonal.
                    double q = ((x & 1) == (y & 1)) ? 1.0 : 0.45;
                    mosaic[y * W + x] = (ushort)Math.Min(65535, mosaic[y * W + x] + peak * f * q);
                }
            }
        }
        Put(40, 40, 900);
        Put(80, 70, 900);

        var detector = new StarDetector {
            EightConnected = true, CurveOfGrowthHfr = true,
            MinStarSize = 3, MaxStarSize = 20000, MaxHfr = 200
        };

        var onMosaic = detector.Detect(mosaic, W, H);
        var (binned, bw, bh) = AutoFocusService.BinBayerQuads(mosaic, W, H);
        var onBinned = detector.Detect(binned, bw, bh);

        Assert.That(onBinned.Count, Is.GreaterThanOrEqualTo(onMosaic.Count),
            "collapsing the mosaic must never find FEWER stars than the mosaic did; "
            + $"mosaic={onMosaic.Count} binned={onBinned.Count}");
        Assert.That(onBinned.Count, Is.GreaterThan(0),
            "the two planted stars have to survive the collapse");
    }
}
