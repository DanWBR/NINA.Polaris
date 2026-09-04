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

using NINA.Image.ImageAnalysis;
using NINA.Core.Enum;
using NINA.Image.ImageData;

namespace NINA.Polaris.Services.Planetary;

/// <summary>
/// Per-frame primitives of the planetary stacker, pure and testable.
///
/// The stacker used to measure sharpness and centroid on the raw Bayer mosaic
/// and shift whole frames by integers rounded to EVEN pixels (to keep the CFA
/// phase). On a Saturn clip that meant: the Laplacian saw the checkerboard
/// instead of the planet, and every frame landed up to a pixel off. Here each
/// frame is debayered first, sharpness and centroid come from its luminance
/// in a window around the planet, and the colour planes are shifted with
/// sub-pixel bilinear resampling before accumulating, which is what the
/// planetary tools people compare us with do.
/// </summary>
public static class PlanetaryFrames {
    /// <summary>Fraction of (peak - background) above which a pixel counts as
    /// planet for the centroid; matches CentroidAligner.</summary>
    public const double ThresholdFraction = 0.25;

    public readonly record struct Planes(float[] R, float[] G, float[] B, float[] Lum, int Width, int Height) {
        public bool Mono => ReferenceEquals(R, G);
    }

    /// <summary>Debayers a mosaic frame (or wraps a mono one) into float planes
    /// plus a luminance plane (R + 2G + B) / 4; mono frames share one plane.</summary>
    public static Planes Split(ushort[] frame, int width, int height, BayerPatternEnum bayer) {
        int n = width * height;
        if (bayer == BayerPatternEnum.None) {
            var m = new float[n];
            for (int i = 0; i < n; i++) m[i] = frame[i];
            return new Planes(m, m, m, m, width, height);
        }
        var ch = BayerDebayer.Bilinear(frame, width, height, bayer);
        var r = new float[n]; var g = new float[n]; var b = new float[n]; var lum = new float[n];
        for (int i = 0; i < n; i++) {
            r[i] = ch.R[i]; g[i] = ch.G[i]; b[i] = ch.B[i];
            lum[i] = (ch.R[i] + 2f * ch.G[i] + ch.B[i]) * 0.25f;
        }
        return new Planes(r, g, b, lum, width, height);
    }

    /// <summary>Background (5th percentile of a strided sample) and peak of a plane.</summary>
    public static (float Background, float Peak) Levels(float[] plane) {
        float peak = float.MinValue;
        var sample = new List<float>(plane.Length / 97 + 1);
        for (int i = 0; i < plane.Length; i++) {
            var v = plane[i];
            if (v > peak) peak = v;
            if (i % 97 == 0) sample.Add(v);
        }
        sample.Sort();
        float bg = sample.Count > 0 ? sample[(int)(sample.Count * 0.05)] : 0f;
        return (bg, peak);
    }

    /// <summary>Intensity-weighted centroid of the pixels above background +
    /// ThresholdFraction × (peak - background). Sub-pixel. Falls back to the
    /// frame centre when nothing rises above the threshold.</summary>
    public static (double X, double Y, long Above) Centroid(float[] lum, int width, int height) {
        var (bg, peak) = Levels(lum);
        if (peak <= bg) return (width / 2.0, height / 2.0, 0);
        double thr = bg + ThresholdFraction * (peak - bg);
        double sw = 0, sx = 0, sy = 0; long above = 0;
        for (int y = 0; y < height; y++) {
            int row = y * width;
            for (int x = 0; x < width; x++) {
                double v = lum[row + x] - thr;
                if (v <= 0) continue;
                sw += v; sx += v * x; sy += v * y; above++;
            }
        }
        if (above == 0 || sw <= 0) return (width / 2.0, height / 2.0, 0);
        return (sx / sw, sy / sw, above);
    }

    /// <summary>Laplacian variance of the luminance inside a window of
    /// <paramref name="size"/> pixels centred on (cx, cy), normalised by the
    /// squared dynamic range so it ranks seeing, not exposure.</summary>
    public static double Sharpness(float[] lum, int width, int height, double cx, double cy, int size) {
        int half = Math.Max(4, size / 2);
        int x0 = Math.Max(1, (int)cx - half), x1 = Math.Min(width - 1, (int)cx + half);
        int y0 = Math.Max(1, (int)cy - half), y1 = Math.Min(height - 1, (int)cy + half);
        if (x1 - x0 < 3 || y1 - y0 < 3) return 0;
        double sum = 0, sumSq = 0; long count = 0;
        float lo = float.MaxValue, hi = float.MinValue;
        for (int y = y0; y < y1; y++) {
            int row = y * width;
            for (int x = x0; x < x1; x++) {
                float c = lum[row + x];
                if (c < lo) lo = c; if (c > hi) hi = c;
                double l = 4.0 * c - lum[row + x - 1] - lum[row + x + 1] - lum[row - width + x] - lum[row + width + x];
                sum += l; sumSq += l * l; count++;
            }
        }
        if (count == 0) return 0;
        double mean = sum / count, var = sumSq / count - mean * mean;
        double range = Math.Max(1.0, hi - lo);
        return var / (range * range);
    }

    /// <summary>Adds <paramref name="plane"/> shifted by (dx, dy) (sub-pixel,
    /// bilinear) into <paramref name="accum"/>, counting coverage in
    /// <paramref name="weight"/>. A destination pixel (x, y) reads the source
    /// at (x - dx, y - dy); pixels whose source falls outside are skipped.</summary>
    public static void AccumulateShifted(float[] plane, int width, int height, double dx, double dy,
                                         float[] accum, float[] weight) {
        for (int y = 0; y < height; y++) {
            double sy = y - dy;
            int y0 = (int)Math.Floor(sy); double fy = sy - y0;
            if (y0 < 0 || y0 + 1 >= height) continue;
            int dstRow = y * width, r0 = y0 * width, r1 = r0 + width;
            for (int x = 0; x < width; x++) {
                double sx = x - dx;
                int x0 = (int)Math.Floor(sx); double fx = sx - x0;
                if (x0 < 0 || x0 + 1 >= width) continue;
                double v = (1 - fy) * ((1 - fx) * plane[r0 + x0] + fx * plane[r0 + x0 + 1])
                         + fy * ((1 - fx) * plane[r1 + x0] + fx * plane[r1 + x0 + 1]);
                accum[dstRow + x] += (float)v;
                weight[dstRow + x] += 1f;
            }
        }
    }

    /// <summary>accum / weight as 16-bit samples (0 where nothing landed).</summary>
    public static ushort[] Finish(float[] accum, float[] weight) {
        var o = new ushort[accum.Length];
        for (int i = 0; i < o.Length; i++) {
            if (weight[i] <= 0) continue;
            double v = accum[i] / weight[i];
            o[i] = (ushort)Math.Clamp(Math.Round(v), 0, 65535);
        }
        return o;
    }
}
