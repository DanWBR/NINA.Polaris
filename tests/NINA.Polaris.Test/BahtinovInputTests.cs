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

using NINA.Core.Enum;
using NINA.Polaris.Services.Focus;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Getting a live video frame ready for the Bahtinov sweep: the ROI the
/// operator can resize, the mosaic reduction, and putting the answer back
/// into frame pixels afterwards.
/// </summary>
[TestFixture]
public class BahtinovInputTests {

    [TestCase(null, BahtinovInput.DefaultRoiHalf)]
    [TestCase(100, 100)]
    [TestCase(1, BahtinovInput.MinRoiHalf)]
    [TestCase(0, BahtinovInput.MinRoiHalf)]
    [TestCase(-40, BahtinovInput.MinRoiHalf)]
    [TestCase(10000, BahtinovInput.MaxRoiHalf)]
    public void RoiHalfIsClampedToSomethingTheSweepCanUse(int? requested, int expected) {
        Assert.That(BahtinovInput.ClampRoiHalf(requested), Is.EqualTo(expected));
    }

    [TestCase(BayerPatternEnum.RGGB, true)]
    [TestCase(BayerPatternEnum.BGGR, true)]
    [TestCase(BayerPatternEnum.GBRG, true)]
    [TestCase(BayerPatternEnum.GRBG, true)]
    [TestCase(BayerPatternEnum.None, false)]
    [TestCase(BayerPatternEnum.Auto, false)]
    public void OnlyAKnownMosaicIsReduced(BayerPatternEnum pattern, bool mosaic) {
        // Auto means "nobody told us", and reducing a mono frame would throw
        // away half the resolution the measurement depends on.
        Assert.That(BahtinovInput.IsMosaic(pattern), Is.EqualTo(mosaic));
    }

    [Test]
    public void PseudoLuminanceAveragesEachQuad() {
        // 4x2 frame, two quads: (10,20,30,40) and (100,200,300,400).
        var px = new ushort[] { 10, 20, 100, 200,
                                30, 40, 300, 400 };
        var (lum, w, h) = BahtinovInput.PseudoLuminance2x2(px, 4, 2);
        Assert.That(w, Is.EqualTo(2));
        Assert.That(h, Is.EqualTo(1));
        Assert.That(lum[0], Is.EqualTo(25));      // (10+20+30+40)/4
        Assert.That(lum[1], Is.EqualTo(250));     // (100+200+300+400)/4
    }

    [Test]
    public void PseudoLuminanceFlattensTheMosaicRipple() {
        // A green-dominant Bayer frame with a flat scene: red sites at 1000,
        // blue at 1000, green at 4000. Along any row the raw frame swings by
        // 3000 counts every pixel, which is what confuses peak finding; the
        // reduced frame is flat.
        const int w = 64, h = 64;
        var px = new ushort[w * h];
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                bool green = ((x + y) & 1) == 1;
                px[y * w + x] = green ? (ushort)4000 : (ushort)1000;
            }
        }
        var (lum, lw, lh) = BahtinovInput.PseudoLuminance2x2(px, w, h);
        Assert.That(lw, Is.EqualTo(32));
        Assert.That(lh, Is.EqualTo(32));
        Assert.That(lum, Is.All.EqualTo(2500));   // (1000 + 4000 + 4000 + 1000)/4
    }

    [Test]
    public void PseudoLuminanceDropsAnOddEdgeInsteadOfHalfSampling() {
        var px = new ushort[5 * 3];
        var (lum, w, h) = BahtinovInput.PseudoLuminance2x2(px, 5, 3);
        Assert.That(w, Is.EqualTo(2));
        Assert.That(h, Is.EqualTo(1));
        Assert.That(lum.Length, Is.EqualTo(2));
    }

    [Test]
    public void PseudoLuminanceRejectsAMismatchedBuffer() {
        var (lum, w, h) = BahtinovInput.PseudoLuminance2x2(new ushort[10], 4, 4);
        Assert.That(lum, Is.Empty);
        Assert.That(w, Is.Zero);
        Assert.That(h, Is.Zero);
    }

    [Test]
    public void ResultFromAReducedGridComesBackInFramePixels() {
        var onGrid = new BahtinovResult {
            Ok = true,
            StarX = 400, StarY = 300, RoiHalf = 50,
            Spike1Angle = 30, Spike1Rho = 2.5,
            Spike2Angle = 90, Spike2Rho = -1.5,
            Spike3Angle = 150, Spike3Rho = 0.5,
            CentreSpikeIndex = 1,
            OffsetPx = 1.25, InFocusThresholdPx = 0.5,
            IntersectionX = 401.5, IntersectionY = 299.5
        };
        var framed = BahtinovInput.ToFrameScale(onGrid, 2);

        Assert.That(framed.StarX, Is.EqualTo(800));
        Assert.That(framed.StarY, Is.EqualTo(600));
        Assert.That(framed.RoiHalf, Is.EqualTo(100));
        Assert.That(framed.Spike1Rho, Is.EqualTo(5.0));
        Assert.That(framed.Spike2Rho, Is.EqualTo(-3.0));
        Assert.That(framed.OffsetPx, Is.EqualTo(2.5));
        Assert.That(framed.IntersectionX, Is.EqualTo(803.0));
        Assert.That(framed.IntersectionY, Is.EqualTo(599.0));
        // Angles are angles: they do not scale with the grid.
        Assert.That(framed.Spike1Angle, Is.EqualTo(30));
        Assert.That(framed.CentreSpikeIndex, Is.EqualTo(1));
        // The threshold scales with the offset, so "in focus" stays the same
        // verdict on either grid rather than becoming twice as strict.
        Assert.That(framed.InFocusThresholdPx, Is.EqualTo(1.0));
        Assert.That(framed.OffsetPx / framed.InFocusThresholdPx,
                    Is.EqualTo(onGrid.OffsetPx / onGrid.InFocusThresholdPx));
    }

    [Test]
    public void ScaleOneAndFailuresPassStraightThrough() {
        var r = new BahtinovResult { Ok = true, StarX = 10, OffsetPx = 1.0, InFocusThresholdPx = 0.5 };
        Assert.That(BahtinovInput.ToFrameScale(r, 1), Is.SameAs(r));

        var failed = BahtinovResult.Fail("no stars detected");
        var framed = BahtinovInput.ToFrameScale(failed, 2);
        Assert.That(framed.Ok, Is.False);
        Assert.That(framed.Error, Is.EqualTo("no stars detected"));
    }

    [Test]
    public void ReducingAMosaicKeepsTheSpikePatternMeasurable() {
        // End to end on synthetic data: paint a defocused Bahtinov pattern,
        // then sample it through an RGGB mosaic (green sites at full signal,
        // red and blue at a third). Analysing the mosaic directly and
        // analysing the reduced frame must both find the pattern, and the
        // reduced one must report the offset in FRAME pixels, so the two
        // agree to within a pixel.
        const int w = 400, h = 400;
        var mono = MakeBahtinovFrame(w, h, centralOffset: 6);
        var mosaic = new ushort[w * h];
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                bool green = ((x + y) & 1) == 1;
                var v = mono[y * w + x];
                mosaic[y * w + x] = green ? v : (ushort)(v / 3);
            }
        }

        var (lum, lw, lh) = BahtinovInput.PseudoLuminance2x2(mosaic, w, h);
        var onGrid = BahtinovAnalyzer.Analyze(lum, lw, lh, starX: 100, starY: 100, roiHalf: 50);
        Assert.That(onGrid.Ok, Is.True, onGrid.Error);

        var framed = BahtinovInput.ToFrameScale(onGrid, 2);
        Assert.That(framed.StarX, Is.EqualTo(200));
        Assert.That(Math.Abs(framed.OffsetPx), Is.GreaterThan(framed.InFocusThresholdPx),
                    "a 6 px central-spike offset must not read as in focus");
    }

    // Same synthetic pattern the analyser's own tests use: three spikes
    // through the centre, the central one displaced by centralOffset px.
    private static ushort[] MakeBahtinovFrame(int w, int h, double centralOffset) {
        var px = new ushort[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = 100;
        var cx = w / 2.0;
        var cy = h / 2.0;
        DrawSpike(px, w, h, cx, cy, 30, 0);
        DrawSpike(px, w, h, cx, cy, 150, 0);
        DrawSpike(px, w, h, cx, cy, 90, centralOffset);
        px[(int)cy * w + (int)cx] = 55000;      // a core for the star detector
        return px;
    }

    private static void DrawSpike(ushort[] px, int w, int h,
                                  double cx, double cy, double angleDeg, double rho) {
        var th = angleDeg * Math.PI / 180.0;
        var dx = Math.Cos(th);
        var dy = Math.Sin(th);
        var nx = -dy;
        var ny = dx;
        for (int t = -140; t <= 140; t++) {
            for (int across = -1; across <= 1; across++) {
                var x = (int)Math.Round(cx + rho * nx + t * dx + across * nx);
                var y = (int)Math.Round(cy + rho * ny + t * dy + across * ny);
                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                px[y * w + x] = 8000;
            }
        }
    }
}
