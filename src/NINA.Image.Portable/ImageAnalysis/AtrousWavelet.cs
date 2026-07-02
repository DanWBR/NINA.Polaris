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
/// À-trous ("with holes") stationary wavelet transform — the multiscale
/// foundation for wavelet sharpening and multiscale HDR. Decomposes a single
/// float plane into N detail planes + a coarse residual using the B3 spline
/// scaling function, so <c>residual + Σ details == original</c> exactly (up to
/// float rounding). The transform is undecimated: every plane has the full
/// image size, which is what makes it useful for astro detail work (no
/// aliasing / block artifacts).
///
/// The 1-D B3 kernel is [1,4,6,4,1]/16, applied separably (rows then columns)
/// with reflect padding; at scale j the taps are spaced 2^(j-1) apart (the
/// "holes"), which is the standard Starck à-trous algorithm.
///
/// Pure math (no external deps); the building block for
/// <see cref="WaveletSharpen"/> and <see cref="WaveScaleHdr"/>.
/// </summary>
public static class AtrousWavelet {
    /// <summary>Result of a decomposition: <see cref="Detail"/>[0] is the
    /// finest scale, and <see cref="Residual"/> is the coarse background.</summary>
    public sealed class Decomposition {
        public float[][] Detail = Array.Empty<float[]>();
        public float[] Residual = Array.Empty<float>();
        public int Scales => Detail.Length;
    }

    private static readonly double[] B3 = { 1.0 / 16, 4.0 / 16, 6.0 / 16, 4.0 / 16, 1.0 / 16 };

    /// <summary>
    /// Decompose a plane into <paramref name="scales"/> detail planes + a
    /// residual. Input is not modified.
    /// </summary>
    public static Decomposition Decompose(float[] plane, int width, int height, int scales) {
        scales = Math.Clamp(scales, 1, 10);
        var detail = new float[scales][];
        var current = (float[])plane.Clone();
        for (int j = 1; j <= scales; j++) {
            int step = 1 << (j - 1);
            var smoothed = Convolve(current, width, height, step);
            // Detail at this scale = what the smoothing removed.
            var d = new float[current.Length];
            for (int i = 0; i < d.Length; i++) d[i] = current[i] - smoothed[i];
            detail[j - 1] = d;
            current = smoothed;
        }
        return new Decomposition { Detail = detail, Residual = current };
    }

    /// <summary>Reconstruct a plane from a (possibly modified) decomposition:
    /// <c>residual + Σ detail</c>.</summary>
    public static float[] Reconstruct(Decomposition d) {
        var outp = (float[])d.Residual.Clone();
        foreach (var plane in d.Detail)
            for (int i = 0; i < outp.Length; i++) outp[i] += plane[i];
        return outp;
    }

    /// <summary>Median absolute deviation of a detail plane (robust noise
    /// scale, /0.6745 → sigma estimate). Used by the denoise threshold.</summary>
    public static double NoiseSigma(float[] detail) {
        if (detail.Length == 0) return 0;
        var abs = new float[detail.Length];
        for (int i = 0; i < detail.Length; i++) abs[i] = Math.Abs(detail[i]);
        Array.Sort(abs);
        double mad = abs[abs.Length / 2];
        return mad / 0.6745;
    }

    // Separable à-trous convolution with the B3 kernel at the given tap
    // spacing (step), reflect padding at the borders.
    private static float[] Convolve(float[] src, int w, int h, int step) {
        var tmp = new float[(long)w * h];
        var dst = new float[(long)w * h];
        // Horizontal pass.
        Parallel.For(0, h, y => {
            long row = (long)y * w;
            for (int x = 0; x < w; x++) {
                double acc = 0;
                for (int k = -2; k <= 2; k++) {
                    int xx = Reflect(x + k * step, w);
                    acc += B3[k + 2] * src[row + xx];
                }
                tmp[row + x] = (float)acc;
            }
        });
        // Vertical pass.
        Parallel.For(0, h, y => {
            for (int x = 0; x < w; x++) {
                double acc = 0;
                for (int k = -2; k <= 2; k++) {
                    int yy = Reflect(y + k * step, h);
                    acc += B3[k + 2] * tmp[(long)yy * w + x];
                }
                dst[(long)y * w + x] = (float)acc;
            }
        });
        return dst;
    }

    private static int Reflect(int i, int n) {
        if (n == 1) return 0;
        // Mirror without repeating the edge (…2 1 0 1 2…).
        while (i < 0 || i >= n) {
            if (i < 0) i = -i;
            if (i >= n) i = 2 * (n - 1) - i;
        }
        return i;
    }
}
