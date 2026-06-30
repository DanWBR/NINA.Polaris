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

namespace NINA.Image.ImageAnalysis;

/// <summary>
/// FFT-based linear convolution of a fixed PSF kernel against arbitrary images
/// of a known size. The PSF spectrum is computed once; each
/// <see cref="Convolve"/> / <see cref="Correlate"/> is two FFTs + a spectral
/// multiply, so the cost is O(N log N) — independent of the PSF stamp size,
/// unlike the O(N·k²) spatial path. This is what keeps measured-PSF
/// Richardson-Lucy practical at full resolution on a low-power SBC.
///
/// The buffer is zero-padded to the next power of two ≥ (dim + 2·kr) and the
/// image is inset by the kernel radius with edge replication, so wrap-around
/// from the circular FFT can't contaminate the cropped interior.
/// </summary>
public sealed class FftConvolver {
    private readonly int _w, _h, _kr, _nx, _ny;
    private readonly float[] _hRe, _hIm;   // PSF spectrum (centered, zero-shift)

    public FftConvolver(float[] kernel, int ks, int width, int height) {
        if (kernel == null) throw new ArgumentNullException(nameof(kernel));
        if (kernel.Length != ks * ks) throw new ArgumentException("kernel length != ks*ks");
        _w = width; _h = height; _kr = ks / 2;
        _nx = Fft.NextPow2(width + 2 * _kr);
        _ny = Fft.NextPow2(height + 2 * _kr);

        // Place the PSF centered at (0,0) with circular wrap so convolution
        // introduces no spatial shift, then transform once.
        _hRe = new float[_nx * _ny];
        _hIm = new float[_nx * _ny];
        for (int ky = 0; ky < ks; ky++) {
            int dy = ((ky - _kr) % _ny + _ny) % _ny;
            for (int kx = 0; kx < ks; kx++) {
                int dx = ((kx - _kr) % _nx + _nx) % _nx;
                _hRe[dy * _nx + dx] = kernel[ky * ks + kx];
            }
        }
        Fft.Transform2D(_hRe, _hIm, _nx, _ny, inverse: false);
    }

    /// <summary>Convolve with the PSF (H·x).</summary>
    public float[] Convolve(float[] src) => Apply(src, adjoint: false);

    /// <summary>Correlate with the PSF (Hᵀ·x — the RL back-projection).</summary>
    public float[] Correlate(float[] src) => Apply(src, adjoint: true);

    private float[] Apply(float[] src, bool adjoint) {
        if (src.Length != (long)_w * _h) throw new ArgumentException("src length != w*h");
        int n = _nx * _ny;
        var re = new float[n];
        var im = new float[n];

        // Inset the image by kr; replicate the border into the kr margin so the
        // padded field has no zero cliff near the cropped region.
        for (int y = 0; y < _h; y++)
            Array.Copy(src, (long)y * _w, re, (long)(y + _kr) * _nx + _kr, _w);
        ReplicateBorders(re);

        Fft.Transform2D(re, im, _nx, _ny, inverse: false);

        // Spectral multiply by H (or conj(H) for the adjoint correlation).
        for (int i = 0; i < n; i++) {
            float ar = re[i], ai = im[i];
            float br = _hRe[i], bi = adjoint ? -_hIm[i] : _hIm[i];
            re[i] = ar * br - ai * bi;
            im[i] = ar * bi + ai * br;
        }

        Fft.Transform2D(re, im, _nx, _ny, inverse: true);

        var outd = new float[(long)_w * _h];
        for (int y = 0; y < _h; y++)
            Array.Copy(re, (long)(y + _kr) * _nx + _kr, outd, (long)y * _w, _w);
        return outd;
    }

    // Edge-replicate the inset image into the kr-wide margin (and the four
    // corners) so the circular convolution sees a smoothly extended field.
    private void ReplicateBorders(float[] buf) {
        int x0 = _kr, x1 = _kr + _w - 1, y0 = _kr, y1 = _kr + _h - 1;
        // left/right margins of each image row
        for (int y = y0; y <= y1; y++) {
            int row = y * _nx;
            float lv = buf[row + x0], rv = buf[row + x1];
            for (int x = 0; x < x0; x++) buf[row + x] = lv;
            for (int x = x1 + 1; x < _nx; x++) buf[row + x] = rv;
        }
        // top/bottom margins (copy whole already-filled rows y0 / y1)
        for (int y = 0; y < y0; y++) Array.Copy(buf, (long)y0 * _nx, buf, (long)y * _nx, _nx);
        for (int y = y1 + 1; y < _ny; y++) Array.Copy(buf, (long)y1 * _nx, buf, (long)y * _nx, _nx);
    }
}
