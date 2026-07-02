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

namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Multiscale HDR: compress the large-scale luminance dynamic range so bright
/// cores (galaxy/nebula centres, saturated star surroundings) come down toward
/// the background WITHOUT flattening the fine detail — the fine à-trous scales
/// are kept intact, only the coarse residual is tone-compressed and rescaled to
/// hold the background level. This is the "recover blown cores" tool
/// (PixInsight HDRMultiscaleTransform / SASpro WaveScale HDR family),
/// implemented from scratch on the à-trous transform (<see cref="AtrousWavelet"/>).
///
/// Works on luminance and re-applies the change as a per-pixel gain to each RGB
/// channel, so colour is preserved.
/// </summary>
public static class WaveScaleHdr {
    /// <summary>
    /// Apply in place. <paramref name="amount"/> 0..1 = compression strength;
    /// <paramref name="scales"/> = à-trous levels used to isolate the coarse
    /// residual (more scales = larger structures counted as "background").
    /// </summary>
    public static void Apply(ushort[] data, int width, int height, int channels,
                             double amount = 0.5, int scales = 6) {
        int ch = channels == 3 ? 3 : 1;
        long plane = (long)width * height;
        double a = Math.Clamp(amount, 0.0, 1.0);
        scales = Math.Clamp(scales, 2, 8);
        if (a <= 0.0) return; // identity

        const double inv = 1.0 / 65535.0;
        var lum = new float[plane];
        if (ch == 3) {
            for (long i = 0; i < plane; i++)
                lum[i] = (float)(ColorSpace.Luminance(
                    data[i] * inv, data[plane + i] * inv, data[2 * plane + i] * inv));
        } else {
            for (long i = 0; i < plane; i++) lum[i] = (float)(data[i] * inv);
        }

        var dec = AtrousWavelet.Decompose(lum, width, height, scales);
        var res = dec.Residual;

        // Tone-compress the coarse residual: brights divided more (extended
        // reciprocal), then rescale so the background median is unchanged, which
        // pulls the bright cores DOWN relative to the background.
        double medBefore = Median01(res);
        const double k = 4.0;
        var resC = new float[res.Length];
        for (int i = 0; i < res.Length; i++) {
            double r = Math.Max(0.0, res[i]);
            resC[i] = (float)(r / (1.0 + a * k * r));
        }
        double medAfter = Median01Copy(resC);
        double scale = medAfter > 1e-6 ? medBefore / medAfter : 1.0;
        for (int i = 0; i < resC.Length; i++) resC[i] = (float)(resC[i] * scale);

        // newLum = compressed background + untouched detail (local structure kept).
        double newLumMedianGuard = 0;
        var newLum = new float[plane];
        for (long i = 0; i < plane; i++) {
            double v = resC[i];
            for (int j = 0; j < dec.Scales; j++) v += dec.Detail[j][i];
            newLum[i] = (float)v;
            newLumMedianGuard += v;
        }
        _ = newLumMedianGuard;

        if (ch == 3) {
            for (long i = 0; i < plane; i++) {
                double l0 = lum[i];
                double gain = l0 > 1e-5 ? Math.Clamp(newLum[i] / l0, 0.0, 4.0) : 1.0;
                data[i]             = Scale(data[i] * gain);
                data[plane + i]     = Scale(data[plane + i] * gain);
                data[2 * plane + i] = Scale(data[2 * plane + i] * gain);
            }
        } else {
            for (long i = 0; i < plane; i++)
                data[i] = (ushort)Math.Clamp(Math.Round(newLum[i] * 65535.0), 0, 65535);
        }
    }

    private static double Median01(float[] v) {
        if (v.Length == 0) return 0;
        var hist = new int[65537];
        foreach (var x in v) {
            int b = (int)Math.Clamp(Math.Round(x * 65535.0), 0, 65535);
            hist[b]++;
        }
        int half = v.Length / 2, acc = 0;
        for (int b = 0; b <= 65535; b++) { acc += hist[b]; if (acc >= half) return b / 65535.0; }
        return 0;
    }

    private static double Median01Copy(float[] v) => Median01(v);

    private static ushort Scale(double v) => (ushort)Math.Clamp(Math.Round(v), 0, 65535);
}
