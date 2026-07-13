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
/// Guards the client-side (WASM) live-stack colour path. The browser stacker
/// (<c>NINA.Polaris.Wasm.Interop.AddFrame</c>) used to align OSC frames by
/// bilinear-warping the <b>raw Bayer mosaic</b>, which blends adjacent
/// R/G/G/B pixels together and desaturates the stack — the field bug where the
/// live stack went black-and-white while the individual frames were fine.
///
/// The fix mirrors <c>LiveStackingService</c>'s colour path: debayer to RGB,
/// warp each plane (interpolation stays inside one channel), accumulate, then
/// re-mosaic back to a CFA frame for the display debayer. These tests pin both
/// halves: (1) warping the raw mosaic really does corrupt colour while the
/// per-plane path preserves it, and (2) the re-mosaic phase table matches
/// <see cref="BayerDebayer"/> so a colour round-trips unchanged.
/// </summary>
[TestFixture]
public class LiveStackClientColourTests {

    private const int W = 16, H = 16;
    private const ushort RVAL = 40000, GVAL = 20000, BVAL = 8000;

    // Re-mosaic phase table — MUST match NINA.Polaris.Wasm.Interop.ColorBlock
    // (itself mirroring BayerDebayer.ColorBlockFor). index = (y&1)*2 + (x&1),
    // value 0=R / 1=G / 2=B. If this drifts from BayerDebayer, the round-trip
    // assertion below swaps colours and fails — which is the whole point.
    private static int[] ColorBlock(BayerPatternEnum p) => p switch {
        BayerPatternEnum.RGGB => new[] { 0, 1, 1, 2 },
        BayerPatternEnum.GRBG => new[] { 1, 0, 2, 1 },
        BayerPatternEnum.GBRG => new[] { 1, 2, 0, 1 },
        BayerPatternEnum.BGGR => new[] { 2, 1, 1, 0 },
        _ => new[] { 1, 1, 1, 1 }
    };

    /// <summary>Build a uniformly-coloured OSC mosaic: every R site holds
    /// RVAL, every G site GVAL, every B site BVAL. Debayering it yields a flat
    /// (RVAL, GVAL, BVAL) field everywhere in the interior.</summary>
    private static ushort[] FlatColourMosaic(BayerPatternEnum pattern) {
        int[] block = ColorBlock(pattern);
        var cfa = new ushort[W * H];
        for (int y = 0; y < H; y++) {
            int rowBase = (y & 1) << 1;
            for (int x = 0; x < W; x++) {
                int colour = block[rowBase + (x & 1)];
                cfa[y * W + x] = colour == 0 ? RVAL : colour == 1 ? GVAL : BVAL;
            }
        }
        return cfa;
    }

    /// <summary>Re-mosaic three planes back to a CFA frame the way
    /// Interop.GetStackedResult does for a colour session.</summary>
    private static ushort[] ReMosaic(ushort[] r, ushort[] g, ushort[] b, BayerPatternEnum pattern) {
        int[] block = ColorBlock(pattern);
        var cfa = new ushort[W * H];
        for (int y = 0; y < H; y++) {
            int rowBase = (y & 1) << 1;
            for (int x = 0; x < W; x++) {
                int i = y * W + x;
                int colour = block[rowBase + (x & 1)];
                cfa[i] = colour == 0 ? r[i] : colour == 1 ? g[i] : b[i];
            }
        }
        return cfa;
    }

    private static AffineTransform Shift(double tx, double ty) =>
        new() { Tx = tx, Ty = ty };

    [Test]
    public void WarpingRawMosaic_DesaturatesColour_WhilePerPlaneWarpPreservesIt() {
        // A flat magenta-ish field (R and B high, G mid). One-pixel dither.
        var cfa = FlatColourMosaic(BayerPatternEnum.RGGB);
        var shift = Shift(1, 0);

        // --- OLD (buggy) path: warp the raw CFA mosaic, THEN debayer. ---
        var warpedCfa = ImageResampler.ApplyTransform(cfa, W, H, shift);
        var badCh = BayerDebayer.Bilinear(warpedCfa, W, H, BayerPatternEnum.RGGB);

        // --- NEW path: debayer FIRST, warp each plane, recombine. ---
        var ch = BayerDebayer.Bilinear(cfa, W, H, BayerPatternEnum.RGGB);
        var wr = ImageResampler.ApplyTransform(ch.R, W, H, shift);
        var wg = ImageResampler.ApplyTransform(ch.G, W, H, shift);
        var wb = ImageResampler.ApplyTransform(ch.B, W, H, shift);

        // Sample a well-interior pixel (away from the warp's zero-fill edge).
        int i = 8 * W + 8;

        // The per-plane path keeps the true colour (a flat plane shifted by an
        // integer pixel is still that flat plane in the interior).
        Assert.That(wr[i], Is.EqualTo(RVAL).Within(2), "per-plane R preserved");
        Assert.That(wg[i], Is.EqualTo(GVAL).Within(2), "per-plane G preserved");
        Assert.That(wb[i], Is.EqualTo(BVAL).Within(2), "per-plane B preserved");

        // The raw-mosaic warp corrupts the colour: shifting the CFA by 1 px
        // slides each colour site onto a neighbour of a DIFFERENT colour, so
        // the debayer reads the wrong channel. At least one channel must land
        // far from its true value — that desaturation IS the bug.
        int dr = System.Math.Abs(badCh.R[i] - RVAL);
        int dg = System.Math.Abs(badCh.G[i] - GVAL);
        int db = System.Math.Abs(badCh.B[i] - BVAL);
        Assert.That(dr + dg + db, Is.GreaterThan(4000),
            "warping the raw CFA mosaic must visibly corrupt colour");
    }

    [Test]
    [TestCase(BayerPatternEnum.RGGB)]
    [TestCase(BayerPatternEnum.GRBG)]
    [TestCase(BayerPatternEnum.GBRG)]
    [TestCase(BayerPatternEnum.BGGR)]
    public void ReMosaicRoundTrip_PreservesColour_ForEveryPattern(BayerPatternEnum pattern) {
        // debayer -> re-mosaic -> debayer must return the same flat colour in
        // the interior. A wrong ColorBlock entry (phase mismatch vs the display
        // debayer) swaps channels here and blows the assertion.
        var cfa = FlatColourMosaic(pattern);
        var ch = BayerDebayer.Bilinear(cfa, W, H, pattern);
        var reMosaic = ReMosaic(ch.R, ch.G, ch.B, pattern);
        var ch2 = BayerDebayer.Bilinear(reMosaic, W, H, pattern);

        int i = 8 * W + 8;   // interior
        Assert.That(ch2.R[i], Is.EqualTo(RVAL).Within(2), $"{pattern} R round-trip");
        Assert.That(ch2.G[i], Is.EqualTo(GVAL).Within(2), $"{pattern} G round-trip");
        Assert.That(ch2.B[i], Is.EqualTo(BVAL).Within(2), $"{pattern} B round-trip");
    }
}
