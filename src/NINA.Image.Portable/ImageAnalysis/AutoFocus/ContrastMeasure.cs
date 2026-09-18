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

namespace NINA.Image.ImageAnalysis.AutoFocus;

/// <summary>Edge operator behind a contrast measurement.</summary>
public enum ContrastMethod {
    /// <summary>Laplacian of Gaussian, 7x7, sigma 1: responds to fine detail
    /// and is the more sensitive of the two near focus.</summary>
    Laplace,
    /// <summary>A 5x5 gradient kernel: responds to broader edges and is the
    /// less noise-sensitive of the two on a rough sky.</summary>
    Sobel
}

/// <summary>
/// Focus metric for scenes without measurable stars: a daylight test on a
/// distant object, the Moon, a planet, a landscape. Sharp focus is where the
/// image holds the most high-frequency detail, so the metric is the mean
/// absolute response of an edge operator over the frame; it peaks at focus
/// instead of dipping like HFR. The same recipe as NINA desktop's contrast
/// detection (8-bit conversion, crop, downsample, LoG or Sobel convolution,
/// mean of the non-black response), written for a plain ushort buffer.
/// </summary>
public static class ContrastMeasure {
    /// <summary>Longest side the frame is reduced to before the convolution.
    /// Detail below this scale is seeing and noise, and the reduction makes a
    /// 26 MP frame cost the same as a small one.</summary>
    public const int DefaultMaxWidth = 1024;

    /// <summary>
    /// Mean absolute edge response over the frame, on the 16-bit scale of the
    /// input (0 to 65535). Higher is sharper. Zero for a flat or empty frame.
    /// </summary>
    public static double Measure(ushort[] data, int width, int height, ContrastMethod method,
            int maxWidth = DefaultMaxWidth) {
        if (data == null || width < 8 || height < 8 || data.Length < width * height) return 0;

        // Box-average down to at most maxWidth on the long side.
        int factor = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)maxWidth));
        var (img, w, h) = Downsample(data, width, height, factor);
        if (w < 8 || h < 8) return 0;

        double[,] kernel = method == ContrastMethod.Laplace ? LaplacianOfGaussian(7, 1.0) : SobelKernel();
        return MeanAbsResponse(img, w, h, kernel);
    }

    /// <summary>
    /// The value the V-curve machinery fits: a bowl with its minimum at best
    /// focus, like HFR. For a Gaussian-shaped contrast peak, minus its
    /// logarithm is exactly a parabola, which is why the contrast metric pairs
    /// with the parabolic fit. Offset so it stays positive on the whole 16-bit
    /// range (a zero reading means "no measurement" downstream).
    /// </summary>
    public static double ToFitSpace(double contrast) =>
        Math.Log(65536.0 / Math.Max(1.0, Math.Min(65535.0, contrast)));

    /// <summary>Inverse of <see cref="ToFitSpace"/>, for display.</summary>
    public static double FromFitSpace(double y) => 65536.0 * Math.Exp(-y);

    internal static (double[] Data, int Width, int Height) Downsample(ushort[] src, int width, int height, int factor) {
        if (factor <= 1) {
            var d = new double[width * height];
            for (int i = 0; i < d.Length; i++) d[i] = src[i];
            return (d, width, height);
        }
        int w = width / factor, h = height / factor;
        var dst = new double[w * h];
        double n = factor * factor;
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                double sum = 0;
                int by = y * factor, bx = x * factor;
                for (int j = 0; j < factor; j++) {
                    int row = (by + j) * width + bx;
                    for (int i = 0; i < factor; i++) sum += src[row + i];
                }
                dst[y * w + x] = sum / n;
            }
        }
        return (dst, w, h);
    }

    /// <summary>Convolve and average the absolute response, skipping the border
    /// the kernel cannot cover and pixels whose response rounds to nothing (the
    /// desktop's "without black" mean, so a large flat sky does not dilute the
    /// edges that carry the signal).</summary>
    internal static double MeanAbsResponse(double[] img, int w, int h, double[,] kernel) {
        int k = kernel.GetLength(0), r = k / 2;
        double sum = 0; long count = 0;
        for (int y = r; y < h - r; y++) {
            for (int x = r; x < w - r; x++) {
                double acc = 0;
                for (int j = -r; j <= r; j++) {
                    int row = (y + j) * w + x;
                    for (int i = -r; i <= r; i++) acc += img[row + i] * kernel[j + r, i + r];
                }
                double a = Math.Abs(acc);
                if (a >= 0.5) { sum += a; count++; }
            }
        }
        return count == 0 ? 0 : sum / count;
    }

    /// <summary>Laplacian of Gaussian, zero-sum and normalised so the sum of
    /// absolute weights is 1, which keeps the response on the input scale.</summary>
    internal static double[,] LaplacianOfGaussian(int size, double sigma) {
        var k = new double[size, size];
        int r = size / 2;
        double s2 = sigma * sigma, s4 = s2 * s2, total = 0;
        for (int y = -r; y <= r; y++) {
            for (int x = -r; x <= r; x++) {
                double q = (x * x + y * y) / (2 * s2);
                double v = -(1.0 / (Math.PI * s4)) * (1 - q) * Math.Exp(-q);
                k[y + r, x + r] = v;
                total += v;
            }
        }
        // Remove the residual DC term so a flat field reads zero, then normalise.
        double mean = total / (size * size), abs = 0;
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) { k[y, x] -= mean; abs += Math.Abs(k[y, x]); }
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) k[y, x] /= abs;
        return k;
    }

    /// <summary>The desktop's 5x5 gradient kernel, normalised like the LoG.</summary>
    internal static double[,] SobelKernel() {
        double[,] k = {
            { -1, -2, 0,  2,  1 },
            { -2, -4, 0,  4,  2 },
            {  0,  0, 0,  0,  0 },
            {  2,  4, 0, -4, -2 },
            {  1,  2, 0, -2, -1 }
        };
        double abs = 0;
        for (int y = 0; y < 5; y++) for (int x = 0; x < 5; x++) abs += Math.Abs(k[y, x]);
        for (int y = 0; y < 5; y++) for (int x = 0; x < 5; x++) k[y, x] /= abs;
        return k;
    }
}
