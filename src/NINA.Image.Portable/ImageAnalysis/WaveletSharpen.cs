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
        // WAVE-1: the one-knob entry point is now a preset over the per-layer
        // engine, so the single Detail slider and a saved workflow keep
        // behaving exactly as before while both go through one code path.
        scales = Math.Clamp(scales, 1, 8);
        double d0 = Math.Clamp(detail, 0.0, 1.0);
        double dn0 = Math.Clamp(denoise, 0.0, 1.0);
        var gains = new double[scales];
        var denoises = new double[scales];
        for (int j = 0; j < scales; j++) {
            // Historical curve: finer scales get more boost, and only the two
            // finest are denoised. Reproduced exactly, not approximated.
            gains[j] = d0 > 0.0 ? 1.0 + d0 * Math.Exp(-j / 2.0) : 1.0;
            denoises[j] = dn0 > 0.0 && j < 2 ? dn0 * (j == 0 ? 1.0 : 0.5) : 0.0;
        }
        ApplyLayers(data, width, height, channels, gains, denoises);
    }

    /// <summary>
    /// WAVE-1: per-layer control, the way RegiStax and AstroSurface present
    /// wavelets and the way planetary images are actually tuned.
    ///
    /// <para><paramref name="gains"/>[j] multiplies detail scale j, finest
    /// first: 1.0 leaves the layer alone, above 1 sharpens, below 1 softens.
    /// <paramref name="denoise"/>[j] soft-thresholds that layer at
    /// <c>3 * value * sigma(layer)</c>, where sigma is the layer's own noise
    /// estimate, so the same number means the same thing on a bright Jupiter
    /// and a faint Saturn.</para>
    ///
    /// <para>Both arrays may be shorter than the decomposition (missing
    /// entries mean "leave it alone"), which keeps the wire format forgiving
    /// when a client sends four sliders for a six-scale transform.</para>
    /// </summary>
    public static void ApplyLayers(ushort[] data, int width, int height, int channels,
                                   IReadOnlyList<double> gains,
                                   IReadOnlyList<double>? denoise = null) {
        int ch = channels == 3 ? 3 : 1;
        long plane = (long)width * height;
        int scales = Math.Clamp(gains?.Count ?? 0, 1, 8);
        if (gains == null || gains.Count == 0) return;

        // Identity check up front: an all-1.0 gain set with no denoising must
        // not touch a single pixel, or "reset the sliders" would still degrade
        // the image through two float round trips.
        bool identity = true;
        for (int j = 0; j < scales && identity; j++) {
            if (Math.Abs(GainAt(gains, j) - 1.0) > 1e-9) identity = false;
            if (DenoiseAt(denoise, j) > 0) identity = false;
        }
        if (identity) return;

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
            // Denoise first, then gain: thresholding after a boost would
            // amplify the noise and only then cut it, which leaves the
            // amplified residue behind.
            double dn = DenoiseAt(denoise, j);
            if (dn > 0.0) {
                double t = 3.0 * dn * AtrousWavelet.NoiseSigma(w);
                if (t > 0) {
                    for (int i = 0; i < w.Length; i++) {
                        double v = w[i];
                        double s = Math.Sign(v) * Math.Max(Math.Abs(v) - t, 0.0);
                        w[i] = (float)s;
                    }
                }
            }
            double g = GainAt(gains, j);
            if (Math.Abs(g - 1.0) > 1e-9) {
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

    /// <summary>Gain for scale j; scales past the end of the array are left
    /// alone rather than clamped to the last value, so a short array cannot
    /// silently apply the finest layer's boost to the coarse background.
    /// </summary>
    private static double GainAt(IReadOnlyList<double> gains, int j) =>
        j < gains.Count ? Math.Clamp(gains[j], 0.0, 8.0) : 1.0;

    private static double DenoiseAt(IReadOnlyList<double>? denoise, int j) =>
        denoise != null && j < denoise.Count ? Math.Clamp(denoise[j], 0.0, 1.0) : 0.0;
}
