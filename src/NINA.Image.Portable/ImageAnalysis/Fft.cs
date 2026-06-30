// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

using System;
using System.Threading.Tasks;

namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Minimal, dependency-free 2-D complex FFT (iterative radix-2 Cooley-Tukey),
/// used to make convolution cost independent of the kernel size — the key to
/// keeping measured-PSF deconvolution viable at full resolution on a low-power
/// SBC, where the O(N·k²) spatial path is hopeless for large PSF stamps.
/// Power-of-two dimensions only (the convolver zero-pads up to the next pow2).
/// </summary>
public static class Fft {
    /// <summary>Smallest power of two ≥ <paramref name="n"/> (min 1).</summary>
    public static int NextPow2(int n) {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    /// <summary>In-place 1-D FFT (forward when <paramref name="inverse"/> is
    /// false). Length must be a power of two. No 1/N scaling — applied once in
    /// the 2-D inverse.</summary>
    public static void Transform1D(float[] re, float[] im, bool inverse)
        => Transform1D(re, im, 0, 1, re.Length, inverse);

    /// <summary>
    /// In-place strided 1-D FFT over <paramref name="n"/> samples starting at
    /// <paramref name="offset"/> with the given <paramref name="stride"/>
    /// (stride 1 = a contiguous row; stride = width = a column). Operating in
    /// place avoids the per-row/column allocations that otherwise dominate a
    /// large 2-D transform. Length must be a power of two.
    /// </summary>
    public static void Transform1D(float[] re, float[] im, int offset, int stride, int n, bool inverse) {
        // bit-reversal permutation
        for (int i = 1, j = 0; i < n; i++) {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) {
                int ai = offset + i * stride, aj = offset + j * stride;
                (re[ai], re[aj]) = (re[aj], re[ai]);
                (im[ai], im[aj]) = (im[aj], im[ai]);
            }
        }
        for (int len = 2; len <= n; len <<= 1) {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
            int half = len >> 1;
            for (int i = 0; i < n; i += len) {
                float cr = 1f, ci = 0f;
                for (int k = 0; k < half; k++) {
                    int a = offset + (i + k) * stride, b = offset + (i + k + half) * stride;
                    float xr = re[b] * cr - im[b] * ci;
                    float xi = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                    float ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr; cr = ncr;
                }
            }
        }
    }

    /// <summary>
    /// In-place 2-D FFT on row-major buffers of length <paramref name="w"/>×
    /// <paramref name="h"/> (both powers of two). The inverse applies the
    /// 1/(w·h) normalization so forward→inverse round-trips to identity.
    /// Allocation-free: rows and columns are transformed in place via strided
    /// 1-D FFTs.
    /// </summary>
    public static void Transform2D(float[] re, float[] im, int w, int h, bool inverse) {
        Parallel.For(0, h, y => Transform1D(re, im, y * w, 1, w, inverse));   // rows
        Parallel.For(0, w, x => Transform1D(re, im, x, w, h, inverse));       // columns
        if (inverse) {
            float s = 1f / ((long)w * h);
            for (int i = 0; i < re.Length; i++) { re[i] *= s; im[i] *= s; }
        }
    }
}
