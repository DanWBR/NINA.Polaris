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

namespace NINA.Polaris.Services.Focus;

/// <summary>
/// The area of the video frame that focus is judged on, as fractions of the
/// frame (0 to 1). Fractions, not pixels: the stream's frame can change size
/// with its ROI or binning, and the browser sees a scaled JPEG, but a fraction
/// means the same place in all of them.
/// </summary>
public readonly record struct FocusRegion(double X, double Y, double W, double H) {
    /// <summary>Smallest side of the region, in frame pixels. Below this the
    /// edge measure has too few pixels to be steadier than the seeing.</summary>
    public const int MinSidePx = 24;

    /// <summary>A region from request values, or null when any of them is
    /// missing or the region is empty. The region is moved inside the frame
    /// rather than rejected, so a tap near the edge still works.</summary>
    public static FocusRegion? From(double? x, double? y, double? w, double? h) {
        if (x is not double fx || y is not double fy || w is not double fw || h is not double fh) return null;
        if (!double.IsFinite(fx) || !double.IsFinite(fy) || !double.IsFinite(fw) || !double.IsFinite(fh)) return null;
        if (fw <= 0 || fh <= 0) return null;
        fw = Math.Min(fw, 1);
        fh = Math.Min(fh, 1);
        fx = Math.Clamp(fx, 0, 1 - fw);
        fy = Math.Clamp(fy, 0, 1 - fh);
        return new FocusRegion(fx, fy, fw, fh);
    }

    /// <summary>The region in pixels of a <paramref name="width"/> x
    /// <paramref name="height"/> frame, at least <see cref="MinSidePx"/> a side
    /// and inside the frame. On a colour mosaic the corner and the size are
    /// even, so the crop keeps the Bayer phase of the frame.</summary>
    public (int X, int Y, int W, int H) ToPixels(int width, int height, bool evenAlign) {
        int w = Math.Clamp((int)Math.Round(W * width), Math.Min(MinSidePx, width), width);
        int h = Math.Clamp((int)Math.Round(H * height), Math.Min(MinSidePx, height), height);
        int x = Math.Clamp((int)Math.Round(X * width), 0, width - w);
        int y = Math.Clamp((int)Math.Round(Y * height), 0, height - h);
        if (evenAlign) {
            x &= ~1; y &= ~1; w &= ~1; h &= ~1;
        }
        return (x, y, Math.Max(w, 0), Math.Max(h, 0));
    }
}

/// <summary>
/// Focus measure for the video autofocus: how much fine detail the region
/// holds, relative to how bright it is.
///
/// The edge response is a Laplacian of a lightly blurred image (a Laplacian of
/// Gaussian), averaged over every pixel of the region. The blur keeps sensor
/// noise from reading as detail, which matters on 8-bit stream frames.
///
/// Dividing by the mean brightness makes the number follow focus and not the
/// sky: if a thin cloud dims the Moon by a third, the edges dim by a third too,
/// and the ratio stays where it was. Without this, a passing cloud reads as a
/// loss of focus and drags the fitted vertex towards whatever position was
/// measured under a clear sky.
/// </summary>
public static class VideoFocusMeasure {
    /// <summary>Scale between the relative contrast (detail over brightness,
    /// about 0.001 to 1) and the 16-bit contrast range the V-curve fit space
    /// expects. Only ratios of the measure matter to the fit, so the scale only
    /// has to keep the numbers inside that range.</summary>
    public const double FitScale = 10000;

    /// <summary>Copy the region out of a frame. Without a region it is a centred
    /// square half the short side of the frame, the same window the sharpness
    /// bar uses. A colour mosaic is first cut on even coordinates, and then
    /// each 2x2 quad is summed into one luminance pixel, so the colour
    /// checkerboard doesn't read as detail.</summary>
    public static (ushort[] Data, int Width, int Height) Crop(
            ushort[] px, int width, int height, FocusRegion? region, bool bayered) {
        var r = region ?? Centre(width, height);
        var (x, y, w, h) = r.ToPixels(width, height, bayered);
        var data = new ushort[w * h];
        for (int row = 0; row < h; row++)
            Array.Copy(px, (y + row) * width + x, data, row * w, w);
        if (bayered && w >= 4 && h >= 4)
            return AutoFocusService.BinBayerQuads(data, w, h);
        return (data, w, h);
    }

    /// <summary>Mean absolute Laplacian of Gaussian over the region, divided by
    /// the region's mean brightness. Higher is sharper. Zero for a region that
    /// is too small, black or flat.</summary>
    public static double RelativeContrast(ushort[] data, int width, int height) {
        if (data == null || width < 8 || height < 8 || data.Length < width * height) return 0;

        // Separable Gaussian, sigma about 1 (1-4-6-4-1 binomial).
        var tmp = new double[width * height];
        var blur = new double[width * height];
        double sum = 0;
        for (int y = 0; y < height; y++) {
            int row = y * width;
            for (int x = 0; x < width; x++) {
                sum += data[row + x];
                double a = 0;
                for (int k = -2; k <= 2; k++) {
                    int xx = Math.Clamp(x + k, 0, width - 1);
                    a += Binomial[k + 2] * data[row + xx];
                }
                tmp[row + x] = a / 16.0;
            }
        }
        double mean = sum / (width * height);
        if (mean <= 0) return 0;
        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) {
                double a = 0;
                for (int k = -2; k <= 2; k++) {
                    int yy = Math.Clamp(y + k, 0, height - 1);
                    a += Binomial[k + 2] * tmp[yy * width + x];
                }
                blur[y * width + x] = a / 16.0;
            }
        }

        // Five-point Laplacian of the blurred image, away from the border the
        // blur had to clamp.
        double acc = 0;
        long n = 0;
        for (int y = 3; y < height - 3; y++) {
            int row = y * width;
            for (int x = 3; x < width - 3; x++) {
                double l = 4 * blur[row + x] - blur[row + x - 1] - blur[row + x + 1]
                           - blur[row - width + x] - blur[row + width + x];
                acc += Math.Abs(l);
                n++;
            }
        }
        if (n == 0) return 0;
        return acc / n / mean;
    }

    private static readonly double[] Binomial = { 1, 4, 6, 4, 1 };

    /// <summary>A centred square, half the short side of the frame.</summary>
    public static FocusRegion Centre(int width, int height) {
        if (width <= 0 || height <= 0) return new FocusRegion(0.25, 0.25, 0.5, 0.5);
        double side = Math.Min(width, height) / 2.0;
        double fw = side / width, fh = side / height;
        return new FocusRegion((1 - fw) / 2, (1 - fh) / 2, fw, fh);
    }

    /// <summary>The mean of the better half of the readings. Seeing blurs some
    /// frames more than others whatever the focuser does, and the sharpest
    /// frames at each position are the ones least disturbed by it, so they say
    /// the most about the focus. Lucky imaging keeps the best frames for the
    /// same reason.</summary>
    public static IReadOnlyList<double> BetterHalf(IReadOnlyList<double> values) {
        if (values.Count <= 2) return values;
        return values.OrderByDescending(v => v).Take((values.Count + 1) / 2).ToList();
    }
}
