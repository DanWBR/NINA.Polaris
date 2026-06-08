// Copyright (C) 2016-2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This file is derived from N.I.N.A. - Nighttime Imaging 'N' Astronomy.
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

using System.Threading.Tasks;
using NINA.Core.Enum;

namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Bilinear demosaicing for the four common Bayer patterns. Given a raw
/// single-channel CFA buffer (one ushort per pixel, each pixel sees only
/// one of R/G/B) it produces three full-resolution channel buffers.
///
/// Convention: the pattern name describes the top-left 2×2 block read
/// row-major. RGGB means row 0 = R G R G..., row 1 = G B G B..., etc.
///
/// Output convention: each channel is a width×height ushort[] aligned
/// with the input. <see cref="ToLuminance"/> collapses an (R, G, B)
/// triple to a perceptual luminance plane for FITS output paths that
/// only carry one channel.
///
/// Why bilinear and not VNG / AHD? Bilinear is ~30 lines, fast, and the
/// downstream STUDIO pipeline (calibration, integration) operates on
/// luminance for star detection anyway. Higher-quality debayer is a
/// follow-up if anyone cares about colour fidelity in the on-server
/// preview, most users export to PixInsight for that.
/// </summary>
public static class BayerDebayer {

    public record Channels(ushort[] R, ushort[] G, ushort[] B);

    public static Channels Bilinear(ushort[] cfa, int width, int height, BayerPatternEnum pattern) {
        if (pattern == BayerPatternEnum.None || pattern == BayerPatternEnum.Auto)
            throw new ArgumentException("Pattern must be RGGB / GRBG / GBRG / BGGR.", nameof(pattern));
        if (cfa.Length < width * height)
            throw new ArgumentException("CFA buffer too small for declared dimensions.", nameof(cfa));

        int n = width * height;
        var r = new ushort[n];
        var g = new ushort[n];
        var b = new ushort[n];

        // For each output pixel, identify which CFA colour it has and
        // bilinear-interpolate the other two from neighbours. The colour
        // at (x, y) depends only on (x&1, y&1), so the whole pattern is a
        // 2x2 lookup table (block[(y&1)*2 + (x&1)] = 0=R/1=G/2=B). This
        // replaces the per-pixel delegate invocation that the old loop
        // paid 3x per pixel (~50M indirect calls on a 16 MP frame); the
        // table read is a couple of branch-free integer ops instead.
        //
        // BENCH-PERF: rows are independent, so the loop fans out across
        // cores. Output is bit-for-bit identical to the old serial path.
        // On WASM (single-threaded mono) it degrades to sequential.
        int[] block = ColorBlockFor(pattern);

        Parallel.For(0, height, y => {
            int yp = y & 1;
            int rowBase = yp << 1;
            for (int x = 0; x < width; x++) {
                int idx = y * width + x;
                int xp = x & 1;
                int colour = block[rowBase + xp];
                ushort raw = cfa[idx];

                switch (colour) {
                    case 0:  // R location
                        r[idx] = raw;
                        g[idx] = AvgN4(cfa, x, y, width, height);   // greens at N/E/S/W
                        b[idx] = AvgDiag4(cfa, x, y, width, height); // blues at diagonals
                        break;
                    case 1:  // G location, interpolate R + B from
                             // horizontal/vertical neighbours depending
                             // on which row we're on.
                        g[idx] = raw;
                        // The other-parity column on this row carries R or
                        // B; if it is R, reds are horizontal from here.
                        if (block[rowBase + (xp ^ 1)] == 0) {
                            // Reds on the same row (left/right), blues
                            // above/below.
                            r[idx] = AvgH(cfa, x, y, width);
                            b[idx] = AvgV(cfa, x, y, width, height);
                        } else {
                            r[idx] = AvgV(cfa, x, y, width, height);
                            b[idx] = AvgH(cfa, x, y, width);
                        }
                        break;
                    case 2:  // B location
                        b[idx] = raw;
                        g[idx] = AvgN4(cfa, x, y, width, height);
                        r[idx] = AvgDiag4(cfa, x, y, width, height);
                        break;
                }
            }
        });

        return new Channels(r, g, b);
    }

    /// <summary>
    /// Collapse (R, G, B) into perceptual luminance using the standard
    /// Rec.601 coefficients (Y = 0.299R + 0.587G + 0.114B). The result
    /// is a single ushort[] suitable for the FITS pipeline.
    /// </summary>
    public static ushort[] ToLuminance(Channels c) {
        var y = new ushort[c.R.Length];
        for (int i = 0; i < y.Length; i++) {
            double v = 0.299 * c.R[i] + 0.587 * c.G[i] + 0.114 * c.B[i];
            y[i] = (ushort)Math.Clamp(Math.Round(v), 0, 65535);
        }
        return y;
    }

    // --- internals ---

    /// <summary>
    /// The 2x2 colour block for a pattern, flattened row-major as
    /// <c>block[(y&amp;1)*2 + (x&amp;1)]</c> = 0=R / 1=G / 2=B. Each pattern is
    /// fully described by its top-left 2x2 block, so a length-4 int[]
    /// replaces the old per-pixel delegate entirely.
    /// </summary>
    private static int[] ColorBlockFor(BayerPatternEnum pattern) {
        // Layout: index 0 = (x0,y0), 1 = (x1,y0), 2 = (x0,y1), 3 = (x1,y1).
        return pattern switch {
            BayerPatternEnum.RGGB => new[] { 0, 1, 1, 2 },
            BayerPatternEnum.GRBG => new[] { 1, 0, 2, 1 },
            BayerPatternEnum.GBRG => new[] { 1, 2, 0, 1 },
            BayerPatternEnum.BGGR => new[] { 2, 1, 1, 0 },
            _ => throw new ArgumentException($"Unsupported pattern {pattern}")
        };
    }

    private static ushort AvgN4(ushort[] cfa, int x, int y, int w, int h) {
        // North / East / South / West.
        int sum = 0, n = 0;
        if (y > 0)        { sum += cfa[(y - 1) * w + x]; n++; }
        if (y + 1 < h)    { sum += cfa[(y + 1) * w + x]; n++; }
        if (x > 0)        { sum += cfa[y * w + (x - 1)]; n++; }
        if (x + 1 < w)    { sum += cfa[y * w + (x + 1)]; n++; }
        return n == 0 ? (ushort)0 : (ushort)(sum / n);
    }

    private static ushort AvgDiag4(ushort[] cfa, int x, int y, int w, int h) {
        // NW / NE / SE / SW.
        int sum = 0, n = 0;
        if (x > 0      && y > 0)      { sum += cfa[(y - 1) * w + (x - 1)]; n++; }
        if (x + 1 < w  && y > 0)      { sum += cfa[(y - 1) * w + (x + 1)]; n++; }
        if (x > 0      && y + 1 < h)  { sum += cfa[(y + 1) * w + (x - 1)]; n++; }
        if (x + 1 < w  && y + 1 < h)  { sum += cfa[(y + 1) * w + (x + 1)]; n++; }
        return n == 0 ? (ushort)0 : (ushort)(sum / n);
    }

    private static ushort AvgH(ushort[] cfa, int x, int y, int w) {
        int sum = 0, n = 0;
        if (x > 0)     { sum += cfa[y * w + (x - 1)]; n++; }
        if (x + 1 < w) { sum += cfa[y * w + (x + 1)]; n++; }
        return n == 0 ? (ushort)0 : (ushort)(sum / n);
    }

    private static ushort AvgV(ushort[] cfa, int x, int y, int w, int h) {
        int sum = 0, n = 0;
        if (y > 0)     { sum += cfa[(y - 1) * w + x]; n++; }
        if (y + 1 < h) { sum += cfa[(y + 1) * w + x]; n++; }
        return n == 0 ? (ushort)0 : (ushort)(sum / n);
    }
}