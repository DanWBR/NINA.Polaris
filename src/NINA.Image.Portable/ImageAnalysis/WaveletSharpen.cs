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
/// Multiscale wavelet sharpening + denoising, operating on the image
/// luminance so colour balance is preserved (the per-pixel luminance gain is
/// re-applied to each RGB channel). Uses the à-trous transform
/// (<see cref="AtrousWavelet"/>): fine detail planes are boosted (sharpen) and
/// optionally soft-thresholded (denoise), then the plane is reconstructed.
///
/// This is the multiscale tool the Polaris STUDIO lacked; it subsumes the
/// classic "frequency separation" workflow (each à-trous scale is a frequency
/// band). Inspired by the wavelet detail tools in SASpro/PixInsight but
/// implemented from scratch on the published Starck à-trous algorithm.
/// </summary>
public static class WaveletSharpen {
    /// <summary>
    /// Apply in place. <paramref name="detail"/> 0..1 = fine-detail boost;
    /// <paramref name="denoise"/> 0..1 = soft-threshold of the finest scales;
    /// <paramref name="scales"/> = number of wavelet levels (default 5).
    /// </summary>
    public static void Apply(ushort[] data, int width, int height, int channels,
                             double detail = 0.5, double denoise = 0.0, int scales = 5) {
        int ch = channels == 3 ? 3 : 1;
        long plane = (long)width * height;
        double d = Math.Clamp(detail, 0.0, 1.0);
        double dn = Math.Clamp(denoise, 0.0, 1.0);
        scales = Math.Clamp(scales, 1, 8);
        if (d <= 0.0 && dn <= 0.0) return; // identity

        const double inv = 1.0 / 65535.0;
        // Luminance plane (mono = the plane itself).
        var lum = new float[plane];
        if (ch == 3) {
            for (long i = 0; i < plane; i++)
                lum[i] = (float)(ColorSpace.Luminance(
                    data[i] * inv, data[plane + i] * inv, data[2 * plane + i] * inv));
        } else {
            for (long i = 0; i < plane; i++) lum[i] = (float)(data[i] * inv);
        }

        var dec = AtrousWavelet.Decompose(lum, width, height, scales);

        for (int j = 0; j < dec.Scales; j++) {
            var w = dec.Detail[j];
            // Denoise: soft-threshold the two finest scales (where noise lives).
            if (dn > 0.0 && j < 2) {
                double t = dn * 3.0 * AtrousWavelet.NoiseSigma(w) * (j == 0 ? 1.0 : 0.5);
                if (t > 0) {
                    for (int i = 0; i < w.Length; i++) {
                        double v = w[i];
                        double s = Math.Sign(v) * Math.Max(Math.Abs(v) - t, 0.0);
                        w[i] = (float)s;
                    }
                }
            }
            // Sharpen: boost finer scales more (exponential emphasis).
            if (d > 0.0) {
                double g = 1.0 + d * Math.Exp(-j / 2.0);
                for (int i = 0; i < w.Length; i++) w[i] = (float)(w[i] * g);
            }
        }

        var newLum = AtrousWavelet.Reconstruct(dec);

        // Re-apply the luminance change as a multiplicative gain per pixel so
        // colour ratios are preserved. Guard against division by ~0.
        if (ch == 3) {
            for (long i = 0; i < plane; i++) {
                double l0 = lum[i];
                double gain = l0 > 1e-5 ? Math.Clamp(newLum[i] / l0, 0.0, 8.0) : 1.0;
                data[i]             = Scale(data[i] * gain);
                data[plane + i]     = Scale(data[plane + i] * gain);
                data[2 * plane + i] = Scale(data[2 * plane + i] * gain);
            }
        } else {
            for (long i = 0; i < plane; i++)
                data[i] = (ushort)Math.Clamp(Math.Round(newLum[i] * 65535.0), 0, 65535);
        }
    }

    private static ushort Scale(double v) => (ushort)Math.Clamp(Math.Round(v), 0, 65535);
}
