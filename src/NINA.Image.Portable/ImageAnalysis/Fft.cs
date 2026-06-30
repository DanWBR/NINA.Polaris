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
    public static void Transform1D(float[] re, float[] im, bool inverse) {
        int n = re.Length;
        // bit-reversal permutation
        for (int i = 1, j = 0; i < n; i++) {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1) {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
            for (int i = 0; i < n; i += len) {
                float cr = 1f, ci = 0f;
                int half = len >> 1;
                for (int k = 0; k < half; k++) {
                    int a = i + k, b = i + k + half;
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
    /// </summary>
    public static void Transform2D(float[] re, float[] im, int w, int h, bool inverse) {
        // rows
        Parallel.For(0, h, y => {
            int off = y * w;
            var rr = new float[w]; var ii = new float[w];
            Array.Copy(re, off, rr, 0, w); Array.Copy(im, off, ii, 0, w);
            Transform1D(rr, ii, inverse);
            Array.Copy(rr, 0, re, off, w); Array.Copy(ii, 0, im, off, w);
        });
        // columns
        Parallel.For(0, w, x => {
            var rr = new float[h]; var ii = new float[h];
            for (int y = 0; y < h; y++) { rr[y] = re[y * w + x]; ii[y] = im[y * w + x]; }
            Transform1D(rr, ii, inverse);
            for (int y = 0; y < h; y++) { re[y * w + x] = rr[y]; im[y * w + x] = ii[y]; }
        });
        if (inverse) {
            float s = 1f / ((long)w * h);
            for (int i = 0; i < re.Length; i++) { re[i] *= s; im[i] *= s; }
        }
    }
}
