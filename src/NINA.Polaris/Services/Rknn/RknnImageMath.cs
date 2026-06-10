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

namespace NINA.Polaris.Services.Rknn;

/// <summary>
/// Small numeric helpers used by the RKNN tile pipelines, ported to mirror the
/// browser <c>onnx-pipelines.js</c> math so the host NPU path produces output
/// consistent with the in-browser ONNX path:
/// median/MAD background statistics, bilinear resize, and a multi-pass box blur
/// that approximates the Gaussian smoothing GraXpert applies to the modelled
/// background.
///
/// MAD here is the raw median absolute deviation (NOT scaled by 1.4826) because
/// the pipelines consume it directly as the normalization scale
/// (<c>(v - median) / mad * 0.04</c>), exactly as the JS does.
/// </summary>
internal static class RknnImageMath {
    // Cap on how many pixels we sample for the median/MAD estimate. The JS path
    // also subsamples for speed; an exact full-image median would shift the
    // estimate by well under a tenth of a percent for real frames.
    private const int MaxSamples = 100_000;

    /// <summary>Median + raw MAD of float data in its own units (e.g. [0,1]).</summary>
    public static (double median, double mad) MedianMadSampled(ReadOnlySpan<float> data) {
        int n = data.Length;
        if (n == 0) return (0.0, 1e-6);
        int stride = Math.Max(1, n / MaxSamples);
        int count = (n + stride - 1) / stride;
        var samples = new float[count];
        int k = 0;
        for (int i = 0; i < n && k < count; i += stride) samples[k++] = data[i];
        if (k < count) Array.Resize(ref samples, k);
        return MedianMadOf(samples);
    }

    /// <summary>
    /// Median + raw MAD of uint16 pixels, computed in NORMALIZED [0,1] space
    /// (value / 65535) so it matches the JS <c>medianMadSampledFromUint16</c>
    /// convention used by the denoise pipeline.
    /// </summary>
    public static (double median, double mad) MedianMadSampledU16(ReadOnlySpan<ushort> data) {
        int n = data.Length;
        if (n == 0) return (0.0, 1e-6);
        const double inv = 1.0 / 65535.0;
        int stride = Math.Max(1, n / MaxSamples);
        int count = (n + stride - 1) / stride;
        var samples = new float[count];
        int k = 0;
        for (int i = 0; i < n && k < count; i += stride) samples[k++] = (float)(data[i] * inv);
        if (k < count) Array.Resize(ref samples, k);
        return MedianMadOf(samples);
    }

    private static (double median, double mad) MedianMadOf(float[] samples) {
        if (samples.Length == 0) return (0.0, 1e-6);
        Array.Sort(samples);
        double median = MedianOfSorted(samples);
        var dev = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++) dev[i] = (float)Math.Abs(samples[i] - median);
        Array.Sort(dev);
        double mad = MedianOfSorted(dev);
        if (mad <= 1e-12) mad = 1e-6;   // guard div-by-zero on flat tiles
        return (median, mad);
    }

    private static double MedianOfSorted(float[] sorted) {
        int n = sorted.Length;
        if (n == 0) return 0.0;
        int mid = n / 2;
        return (n & 1) == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>
    /// Bilinear resize of a single-plane uint16 buffer. Half-pixel center
    /// mapping with edge clamping. Used to downsample a full frame to the BGE
    /// model's 256x256 input.
    /// </summary>
    public static ushort[] BilinearResizeU16(ReadOnlySpan<ushort> src, int sw, int sh, int dw, int dh) {
        var dst = new ushort[dw * dh];
        double scaleX = (double)sw / dw;
        double scaleY = (double)sh / dh;
        for (int y = 0; y < dh; y++) {
            double syf = (y + 0.5) * scaleY - 0.5;
            int y0 = (int)Math.Floor(syf);
            double fy = syf - y0;
            int y0c = Clamp(y0, 0, sh - 1);
            int y1c = Clamp(y0 + 1, 0, sh - 1);
            for (int x = 0; x < dw; x++) {
                double sxf = (x + 0.5) * scaleX - 0.5;
                int x0 = (int)Math.Floor(sxf);
                double fx = sxf - x0;
                int x0c = Clamp(x0, 0, sw - 1);
                int x1c = Clamp(x0 + 1, 0, sw - 1);
                double v00 = src[y0c * sw + x0c];
                double v01 = src[y0c * sw + x1c];
                double v10 = src[y1c * sw + x0c];
                double v11 = src[y1c * sw + x1c];
                double top = v00 + (v01 - v00) * fx;
                double bot = v10 + (v11 - v10) * fx;
                double v = top + (bot - top) * fy;
                dst[y * dw + x] = (ushort)Math.Clamp(Math.Round(v), 0, 65535);
            }
        }
        return dst;
    }

    /// <summary>Bilinear resize of a single-plane float buffer (background upscale).</summary>
    public static float[] BilinearResizeF(ReadOnlySpan<float> src, int sw, int sh, int dw, int dh) {
        var dst = new float[dw * dh];
        double scaleX = (double)sw / dw;
        double scaleY = (double)sh / dh;
        for (int y = 0; y < dh; y++) {
            double syf = (y + 0.5) * scaleY - 0.5;
            int y0 = (int)Math.Floor(syf);
            double fy = syf - y0;
            int y0c = Clamp(y0, 0, sh - 1);
            int y1c = Clamp(y0 + 1, 0, sh - 1);
            for (int x = 0; x < dw; x++) {
                double sxf = (x + 0.5) * scaleX - 0.5;
                int x0 = (int)Math.Floor(sxf);
                double fx = sxf - x0;
                int x0c = Clamp(x0, 0, sw - 1);
                int x1c = Clamp(x0 + 1, 0, sw - 1);
                double v00 = src[y0c * sw + x0c];
                double v01 = src[y0c * sw + x1c];
                double v10 = src[y1c * sw + x0c];
                double v11 = src[y1c * sw + x1c];
                double top = v00 + (v01 - v00) * fx;
                double bot = v10 + (v11 - v10) * fx;
                dst[y * dw + x] = (float)(top + (bot - top) * fy);
            }
        }
        return dst;
    }

    /// <summary>
    /// Separable multi-pass box blur (radius 2) over a float plane. Repeated
    /// passes approach a Gaussian; the BGE pipeline uses this to smooth the
    /// modelled 256x256 background before upscaling, matching the JS
    /// <c>boxBlurF(bg, 256, 256, passes)</c>.
    /// </summary>
    public static float[] BoxBlurF(float[] src, int w, int h, int passes, int radius = 2) {
        var cur = (float[])src.Clone();
        var tmp = new float[cur.Length];
        for (int p = 0; p < passes; p++) {
            BoxBlurHorizontal(cur, tmp, w, h, radius);
            BoxBlurVertical(tmp, cur, w, h, radius);
        }
        return cur;
    }

    private static void BoxBlurHorizontal(float[] src, float[] dst, int w, int h, int r) {
        double norm = 1.0 / (2 * r + 1);
        for (int y = 0; y < h; y++) {
            int row = y * w;
            for (int x = 0; x < w; x++) {
                double sum = 0;
                for (int k = -r; k <= r; k++) {
                    int xx = Clamp(x + k, 0, w - 1);
                    sum += src[row + xx];
                }
                dst[row + x] = (float)(sum * norm);
            }
        }
    }

    private static void BoxBlurVertical(float[] src, float[] dst, int w, int h, int r) {
        double norm = 1.0 / (2 * r + 1);
        for (int x = 0; x < w; x++) {
            for (int y = 0; y < h; y++) {
                double sum = 0;
                for (int k = -r; k <= r; k++) {
                    int yy = Clamp(y + k, 0, h - 1);
                    sum += src[yy * w + x];
                }
                dst[y * w + x] = (float)(sum * norm);
            }
        }
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
}
