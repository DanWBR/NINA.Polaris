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
using NINA.Core.Enum;
using NINA.Image.ImageAnalysis;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the bilinear-debayer math: the four canonical patterns'
/// colour layouts must match the convention documented in
/// BayerDebayer (top-left 2×2 read row-major), and the interpolation
/// must populate every channel at every pixel.
/// </summary>
[TestFixture]
public class BayerDebayerTests {

    private static ushort[] Build4x4(ushort fill) {
        var a = new ushort[16];
        Array.Fill(a, fill);
        return a;
    }

    /// <summary>
    /// Remosaic is the exact inverse of the debayer's SAMPLING step: at every
    /// site the debayer copies the raw value into that site's own channel, so
    /// taking it back out has to return the original mosaic bit for bit, for
    /// all four patterns.
    /// </summary>
    [Test]
    public void Remosaic_RoundTripsTheOriginalMosaic(
            [Values(BayerPatternEnum.RGGB, BayerPatternEnum.GRBG,
                    BayerPatternEnum.GBRG, BayerPatternEnum.BGGR)] BayerPatternEnum pattern) {
        const int W = 16, H = 12;
        var cfa = new ushort[W * H];
        var rnd = new System.Random(7);
        for (int i = 0; i < cfa.Length; i++) cfa[i] = (ushort)rnd.Next(0, 65536);

        var ch = BayerDebayer.Bilinear(cfa, W, H, pattern);
        var back = BayerDebayer.Remosaic(ch.R, ch.G, ch.B, W, H, pattern);

        Assert.That(back, Is.EqualTo(cfa), $"round trip must be lossless for {pattern}");
    }

    /// <summary>
    /// THE TECHNIQUE, pinned end to end (LIVEBAYER-4).
    ///
    /// A CFA mosaic must not be resampled as if it were one image: bilinear
    /// blending mixes neighbours that are different colours, and the shift
    /// moves the pattern off phase. The live stacker did exactly that and then
    /// told the client to debayer the result, so the LIVE view went grey with
    /// mosaic banding after the reference frame (field, Q6A, 2026-08-09).
    ///
    /// Here a synthetic OSC frame has R bright, G mid and B dark. Shifting it
    /// by a whole pixel and putting it back is the cheapest transform that
    /// still moves the CFA phase. Warping the mosaic directly must destroy the
    /// channel separation; debayer + per-plane warp + remosaic must keep it.
    /// </summary>
    [Test]
    public void Remosaic_PerPlaneWarpKeepsChannelsApart_MosaicWarpDoesNot() {
        const int W = 32, H = 32;
        const ushort R = 50000, G = 20000, B = 3000;
        var cfa = new ushort[W * H];
        for (int y = 0; y < H; y++) {
            for (int x = 0; x < W; x++) {
                bool er = (y & 1) == 0, ec = (x & 1) == 0;      // RGGB: R G / G B
                cfa[y * W + x] = er ? (ec ? R : G) : (ec ? G : B);
            }
        }
        // Sub-pixel, like a real alignment: this hits BOTH failure modes at
        // once, the interpolation blending across colours and the phase shift.
        // A whole-pixel shift would only demonstrate the phase half.
        var t = new AffineTransform { M00 = 1, M11 = 1, Tx = 1.5, Ty = 0.5 };

        // WRONG WAY: resample the mosaic itself, then debayer.
        var mosaicWarped = ImageResampler.ApplyTransform(cfa, W, H, t, new ushort[W * H]);
        var bad = BayerDebayer.Bilinear(mosaicWarped, W, H, BayerPatternEnum.RGGB);

        // RIGHT WAY: debayer, warp each plane, remosaic, then debayer.
        var src = BayerDebayer.Bilinear(cfa, W, H, BayerPatternEnum.RGGB);
        var wr = ImageResampler.ApplyTransform(src.R, W, H, t, new ushort[W * H]);
        var wg = ImageResampler.ApplyTransform(src.G, W, H, t, new ushort[W * H]);
        var wb = ImageResampler.ApplyTransform(src.B, W, H, t, new ushort[W * H]);
        var remosaiced = BayerDebayer.Remosaic(wr, wg, wb, W, H, BayerPatternEnum.RGGB);
        var good = BayerDebayer.Bilinear(remosaiced, W, H, BayerPatternEnum.RGGB);

        // Measure in the interior, away from the edges the shift leaves blank.
        static double Mean(ushort[] p, int w, int h) {
            double s = 0; int n = 0;
            for (int y = 4; y < h - 4; y++)
                for (int x = 4; x < w - 4; x++) { s += p[y * w + x]; n++; }
            return n > 0 ? s / n : 0;
        }
        double goodSpread = Mean(good.R, W, H) - Mean(good.B, W, H);
        double badSpread = Mean(bad.R, W, H) - Mean(bad.B, W, H);

        Assert.That(goodSpread, Is.GreaterThan(0.8 * (R - B)),
            "per-plane warp + remosaic must preserve the R-to-B separation "
            + $"(got {goodSpread:F0}, source spread {R - B})");
        Assert.That(badSpread, Is.LessThan(0.5 * goodSpread),
            "warping the mosaic directly must visibly collapse the channels "
            + $"towards each other (got {badSpread:F0} vs {goodSpread:F0}); if this "
            + "ever stops being true the test is no longer proving anything");
    }

    [Test]
    public void Bilinear_RGGB_RedAtTopLeftSurvives() {
        // Pattern: top-left is R. Force a 4×4 with the R pixel at (0,0)
        // = 60000 and everything else = 0; the R-channel output should
        // be 60000 at (0,0) and the green/blue channels should be 0
        // there (no neighbours to average).
        var cfa = new ushort[16];
        cfa[0] = 60000;
        var ch = BayerDebayer.Bilinear(cfa, 4, 4, BayerPatternEnum.RGGB);
        Assert.That(ch.R[0], Is.EqualTo(60000));
        // G at the corner has no N/S/E/W neighbour with green, only
        // (0,1) and (1,0) which are green sites. They're zero in this
        // synthetic frame, so green at (0,0) is 0.
        Assert.That(ch.G[0], Is.EqualTo(0));
        Assert.That(ch.B[0], Is.EqualTo(0));
    }

    [Test]
    public void Bilinear_BGGR_BlueAtTopLeftSurvives() {
        var cfa = new ushort[16];
        cfa[0] = 50000;
        var ch = BayerDebayer.Bilinear(cfa, 4, 4, BayerPatternEnum.BGGR);
        Assert.That(ch.B[0], Is.EqualTo(50000));
        Assert.That(ch.R[0], Is.EqualTo(0));   // no R neighbours
    }

    [Test]
    public void Bilinear_OutputChannelsHaveCorrectSize() {
        var cfa = Build4x4(1000);
        var ch = BayerDebayer.Bilinear(cfa, 4, 4, BayerPatternEnum.RGGB);
        Assert.That(ch.R.Length, Is.EqualTo(16));
        Assert.That(ch.G.Length, Is.EqualTo(16));
        Assert.That(ch.B.Length, Is.EqualTo(16));
    }

    [Test]
    public void Bilinear_UniformInput_ProducesUniformOutput() {
        // Flat-field 1000 everywhere. After bilinear demosaic every
        // pixel of every channel should also read 1000 (interpolation
        // of constants is the constant), modulo edge effects.
        var cfa = Build4x4(1000);
        var ch = BayerDebayer.Bilinear(cfa, 4, 4, BayerPatternEnum.RGGB);
        // Check a centre pixel (1,1), it has full neighbour coverage.
        int idx = 1 * 4 + 1;
        Assert.That(ch.R[idx], Is.EqualTo(1000));
        Assert.That(ch.G[idx], Is.EqualTo(1000));
        Assert.That(ch.B[idx], Is.EqualTo(1000));
    }

    [Test]
    public void Bilinear_RejectsNonePattern() {
        var cfa = Build4x4(0);
        Assert.Throws<ArgumentException>(() =>
            BayerDebayer.Bilinear(cfa, 4, 4, BayerPatternEnum.None));
        Assert.Throws<ArgumentException>(() =>
            BayerDebayer.Bilinear(cfa, 4, 4, BayerPatternEnum.Auto));
    }

    [Test]
    public void ToLuminance_GreenDominatesWeight() {
        // Rec.601: Y = 0.299R + 0.587G + 0.114B. A pure-green pixel
        // should clearly outshine a pure-blue one of the same value.
        var ch = new BayerDebayer.Channels(
            R: new ushort[] { 0, 0, 0 },
            G: new ushort[] { 0, 1000, 0 },
            B: new ushort[] { 0, 0, 0 });
        var y = BayerDebayer.ToLuminance(ch);
        Assert.That(y[1], Is.EqualTo(587));  // 0.587 × 1000

        ch = new BayerDebayer.Channels(
            R: new ushort[] { 0, 0, 0 },
            G: new ushort[] { 0, 0, 0 },
            B: new ushort[] { 0, 1000, 0 });
        y = BayerDebayer.ToLuminance(ch);
        Assert.That(y[1], Is.EqualTo(114));  // 0.114 × 1000
    }

    [TestCase(BayerPatternEnum.RGGB)]
    [TestCase(BayerPatternEnum.GRBG)]
    [TestCase(BayerPatternEnum.GBRG)]
    [TestCase(BayerPatternEnum.BGGR)]
    public void Bilinear_AllFourPatternsProduceValidOutput(BayerPatternEnum pattern) {
        // Smoke test: each pattern returns three full-sized buffers
        // and doesn't throw for a typical input. Specific colour
        // placement is locked down by the dedicated RGGB / BGGR tests.
        var cfa = new ushort[64];
        var r = new Random(42);
        for (int i = 0; i < 64; i++) cfa[i] = (ushort)r.Next(0, 65535);
        var ch = BayerDebayer.Bilinear(cfa, 8, 8, pattern);
        Assert.That(ch.R.Length, Is.EqualTo(64));
        Assert.That(ch.G.Length, Is.EqualTo(64));
        Assert.That(ch.B.Length, Is.EqualTo(64));
    }
}