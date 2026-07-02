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
using System.Threading.Tasks;

namespace NINA.Image.ImageAnalysis;

/// <summary>
/// CLAHE — Contrast-Limited Adaptive Histogram Equalization. Boosts LOCAL
/// contrast (per-tile histogram equalization) while the clip limit caps noise
/// amplification, and bilinear interpolation between tile mappings removes the
/// tile-boundary seams. Runs on luminance and re-applies the change as a gain
/// to each RGB channel so colour is preserved.
///
/// Standard Zuiderveld CLAHE, implemented from scratch (256-level luminance
/// histograms). Best used AFTER a stretch (on display-referred data); on raw
/// linear data it will over-boost the background.
/// </summary>
public static class Clahe {
    private const int Levels = 256;

    /// <summary>
    /// Apply in place. <paramref name="clipLimit"/> ≥ 1 caps per-bin count at
    /// clipLimit×average (1 = no local boost, 2-4 typical); <paramref name="tiles"/>
    /// = grid size per axis (8 typical).
    /// </summary>
    public static void Apply(ushort[] data, int width, int height, int channels,
                             double clipLimit = 2.0, int tiles = 8) {
        int ch = channels == 3 ? 3 : 1;
        long plane = (long)width * height;
        int nt = Math.Clamp(tiles, 1, 32);
        double clip = Math.Max(1.0, clipLimit);
        if (width < nt || height < nt) return;

        const double inv = 1.0 / 65535.0;
        // Luminance level (0..255) per pixel.
        var lvl = new byte[plane];
        var lum = new float[plane];
        if (ch == 3) {
            for (long i = 0; i < plane; i++) {
                double l = ColorSpace.Luminance(data[i] * inv, data[plane + i] * inv, data[2 * plane + i] * inv);
                lum[i] = (float)l;
                lvl[i] = (byte)Math.Clamp((int)Math.Round(l * 255.0), 0, 255);
            }
        } else {
            for (long i = 0; i < plane; i++) {
                double l = data[i] * inv;
                lum[i] = (float)l;
                lvl[i] = (byte)Math.Clamp((int)Math.Round(l * 255.0), 0, 255);
            }
        }

        // Per-tile equalization mapping (level → equalized level 0..1).
        var map = new float[nt * nt][];
        Parallel.For(0, nt * nt, t => {
            int tx = t % nt, ty = t / nt;
            int x0 = (int)((long)tx * width / nt), x1 = (int)((long)(tx + 1) * width / nt);
            int y0 = (int)((long)ty * height / nt), y1 = (int)((long)(ty + 1) * height / nt);
            map[t] = TileMapping(lvl, width, x0, x1, y0, y1, clip);
        });

        // Apply with bilinear interpolation between the 4 surrounding tile
        // centres; re-apply as a gain to RGB (or straight for mono).
        double tw = (double)width / nt, th = (double)height / nt;
        Parallel.For(0, height, y => {
            double fy = y / th - 0.5;
            int ty0 = (int)Math.Floor(fy);
            double wy = fy - ty0;
            int tya = Math.Clamp(ty0, 0, nt - 1), tyb = Math.Clamp(ty0 + 1, 0, nt - 1);
            long row = (long)y * width;
            for (int x = 0; x < width; x++) {
                double fx = x / tw - 0.5;
                int tx0 = (int)Math.Floor(fx);
                double wx = fx - tx0;
                int txa = Math.Clamp(tx0, 0, nt - 1), txb = Math.Clamp(tx0 + 1, 0, nt - 1);
                int L = lvl[row + x];
                double m00 = map[tya * nt + txa][L], m10 = map[tya * nt + txb][L];
                double m01 = map[tyb * nt + txa][L], m11 = map[tyb * nt + txb][L];
                double top = m00 + (m10 - m00) * wx;
                double bot = m01 + (m11 - m01) * wx;
                double newL = top + (bot - top) * wy;

                long i = row + x;
                if (ch == 3) {
                    double l0 = lum[i];
                    double gain = l0 > 1e-5 ? Math.Clamp(newL / l0, 0.0, 8.0) : 1.0;
                    data[i]             = Scale(data[i] * gain);
                    data[plane + i]     = Scale(data[plane + i] * gain);
                    data[2 * plane + i] = Scale(data[2 * plane + i] * gain);
                } else {
                    data[i] = (ushort)Math.Clamp(Math.Round(newL * 65535.0), 0, 65535);
                }
            }
        });
    }

    // Clipped-histogram equalization mapping for one tile → CDF in [0,1].
    private static float[] TileMapping(byte[] lvl, int width, int x0, int x1, int y0, int y1, double clip) {
        var hist = new int[Levels];
        int n = 0;
        for (int y = y0; y < y1; y++) {
            long row = (long)y * width;
            for (int x = x0; x < x1; x++) { hist[lvl[row + x]]++; n++; }
        }
        var map = new float[Levels];
        if (n == 0) { for (int l = 0; l < Levels; l++) map[l] = l / 255f; return map; }

        // Clip and redistribute the excess uniformly.
        int clipCount = (int)Math.Max(1, clip * n / Levels);
        long excess = 0;
        for (int l = 0; l < Levels; l++) if (hist[l] > clipCount) { excess += hist[l] - clipCount; hist[l] = clipCount; }
        int add = (int)(excess / Levels);
        for (int l = 0; l < Levels; l++) hist[l] += add;

        long cdf = 0, total = 0;
        for (int l = 0; l < Levels; l++) total += hist[l];
        if (total == 0) total = 1;
        for (int l = 0; l < Levels; l++) {
            cdf += hist[l];
            map[l] = (float)((double)cdf / total);
        }
        return map;
    }

    private static ushort Scale(double v) => (ushort)Math.Clamp(Math.Round(v), 0, 65535);
}
