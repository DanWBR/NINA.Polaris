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
using System.Collections.Generic;

namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Morphological star reduction: shrink / dim stars without removing them,
/// so an over-starry field lets the nebulosity read. Detects stars with the
/// existing <see cref="StarDetector"/>, builds a soft per-star mask, and
/// applies a grayscale-erosion (local minimum) transform inside that mask:
/// <c>reduced = orig - amount·mask·(orig - eroded)</c>. Because the eroded
/// value is a local minimum it is always ≤ the original, so stars only ever
/// get darker / smaller and the background is untouched.
///
/// A <c>protectCore</c> guard tapers the effect toward each star's centre so
/// the brightest cores (and their colour) survive instead of being flattened.
/// This is the v1 morphological approach (no starless model / recomposition
/// needed); it complements the AI star-removal + blend path.
///
/// Inspired by Siril's star-reduction / synthstar workflow but implemented
/// from scratch (grayscale morphology + a detected-star mask).
/// </summary>
public static class StarReduction {
    /// <summary>
    /// Reduce stars in place on a plane-sequential ushort buffer.
    /// <paramref name="amount"/> 0..1 = strength; <paramref name="size"/> =
    /// erosion radius in px (bigger = more shrink); <paramref name="protectCore"/>
    /// keeps bright cores. Returns the number of stars affected.
    /// </summary>
    public static int Apply(ushort[] data, int width, int height, int channels,
                            double amount = 0.5, int size = 2, bool protectCore = true,
                            StarDetector detector = null) {
        int ch = channels == 3 ? 3 : 1;
        long plane = (long)width * height;
        double a = Math.Clamp(amount, 0.0, 1.0);
        int r = Math.Clamp(size, 1, 12);
        if (a <= 0.0) return 0;

        // Luminance plane for detection (mono = the plane itself).
        ushort[] lum;
        if (ch == 3) {
            lum = new ushort[plane];
            for (long i = 0; i < plane; i++) {
                int v = (data[i] + data[plane + i] + data[2 * plane + i]) / 3;
                lum[i] = (ushort)v;
            }
        } else {
            lum = data;
        }

        detector ??= new StarDetector {
            MaxStars = 100000,
            MaxStarSize = 2000,
            MaxHfr = 100,
            BorderExclusion = Math.Max(4, r + 2),
        };
        var stars = detector.Detect(lum, width, height);
        if (stars.Count == 0) return 0;

        // Soft star mask (0..1). Each star paints a disk of radius ~2.5·HFR;
        // the edge feathers out and, with protectCore, the centre is tapered
        // so the peak is preserved.
        var mask = new float[plane];
        foreach (var s in stars) {
            double R = Math.Clamp(s.HFR * 2.5, 2.0, 80.0);
            double core = protectCore ? 0.35 * R : 0.0;
            int cx = (int)Math.Round(s.X), cy = (int)Math.Round(s.Y);
            int ri = (int)Math.Ceiling(R);
            for (int dy = -ri; dy <= ri; dy++) {
                int y = cy + dy;
                if (y < 0 || y >= height) continue;
                for (int dx = -ri; dx <= ri; dx++) {
                    int x = cx + dx;
                    if (x < 0 || x >= width) continue;
                    double d = Math.Sqrt((double)dx * dx + (double)dy * dy);
                    if (d > R) continue;
                    // Edge feather: full inside 0.8R, ramps to 0 at R.
                    double wEdge = d <= 0.8 * R ? 1.0 : 1.0 - (d - 0.8 * R) / (0.2 * R);
                    // Core protection: ramp up from 0 at centre to 1 at `core`.
                    double wCore = core > 0 ? Math.Clamp(d / core, 0.0, 1.0) : 1.0;
                    float w = (float)Math.Clamp(wEdge * wCore, 0.0, 1.0);
                    long i = (long)y * width + x;
                    if (w > mask[i]) mask[i] = w;
                }
            }
        }

        // Grayscale erosion (separable local min, radius r) + masked blend,
        // per channel.
        for (int c = 0; c < ch; c++) {
            long baseIdx = (long)c * plane;
            var eroded = ErodeSeparable(data, baseIdx, width, height, r);
            for (long i = 0; i < plane; i++) {
                float m = mask[i];
                if (m <= 0f) continue;
                double orig = data[baseIdx + i];
                double er = eroded[i];
                double reduced = orig - a * m * (orig - er);
                data[baseIdx + i] = (ushort)Math.Clamp(Math.Round(reduced), 0, 65535);
            }
        }
        return stars.Count;
    }

    // Separable grayscale erosion (local minimum over a (2r+1) box) of one
    // plane, returned as a fresh array. Horizontal then vertical min pass.
    private static ushort[] ErodeSeparable(ushort[] data, long offset, int w, int h, int r) {
        var tmp = new ushort[(long)w * h];
        var outp = new ushort[(long)w * h];
        // Horizontal min.
        for (int y = 0; y < h; y++) {
            long row = (long)y * w;
            for (int x = 0; x < w; x++) {
                int lo = Math.Max(0, x - r), hi = Math.Min(w - 1, x + r);
                ushort mn = ushort.MaxValue;
                for (int xx = lo; xx <= hi; xx++) {
                    ushort v = data[offset + row + xx];
                    if (v < mn) mn = v;
                }
                tmp[row + x] = mn;
            }
        }
        // Vertical min.
        for (int y = 0; y < h; y++) {
            int lo = Math.Max(0, y - r), hi = Math.Min(h - 1, y + r);
            for (int x = 0; x < w; x++) {
                ushort mn = ushort.MaxValue;
                for (int yy = lo; yy <= hi; yy++) {
                    ushort v = tmp[(long)yy * w + x];
                    if (v < mn) mn = v;
                }
                outp[(long)y * w + x] = mn;
            }
        }
        return outp;
    }
}
