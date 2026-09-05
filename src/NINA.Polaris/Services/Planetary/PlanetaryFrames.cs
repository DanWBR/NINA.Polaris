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

    /// <summary>7-tap Gaussian (sigma 1.1, what OpenCV picks for a 7×7
    /// kernel), separable. PlanetarySystemStacker blurs the mono frame this
    /// way before every quality measure and every correlation, so noise does
    /// not masquerade as structure; we do the same on the luminance used for
    /// ranking and centroiding (never on the planes that get stacked).</summary>
    public static float[] Blur7(float[] plane, int width, int height) {
        float[] k = { 0.0071f, 0.0722f, 0.2394f, 0.3626f, 0.2394f, 0.0722f, 0.0071f };
        var tmp = new float[plane.Length]; var o = new float[plane.Length];
        for (int y = 0; y < height; y++) {
            int row = y * width;
            for (int x = 0; x < width; x++) {
                float acc = 0;
                for (int t = -3; t <= 3; t++) {
                    int xx = Math.Clamp(x + t, 0, width - 1);
                    acc += k[t + 3] * plane[row + xx];
                }
                tmp[row + x] = acc;
            }
        }
        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) {
                float acc = 0;
                for (int t = -3; t <= 3; t++) {
                    int yy = Math.Clamp(y + t, 0, height - 1);
                    acc += k[t + 3] * tmp[yy * width + x];
                }
                o[y * width + x] = acc;
            }
        }
        return o;
    }

    /// <summary>Sharpness the way PlanetarySystemStacker ranks frames
    /// ("Laplace"): the standard deviation of the discrete Laplacian of the
    /// BLURRED luminance, sampled on a stride-2 grid, inside a window of
    /// <paramref name="size"/> pixels centred on (cx, cy). Normalised by the
    /// window's dynamic range so it ranks seeing, not exposure. Pass the
    /// output of <see cref="Blur7"/>; an unblurred plane rewards noise.</summary>
    public static double Sharpness(float[] lumBlurred, int width, int height, double cx, double cy, int size) {
        const int stride = 2;
        int half = Math.Max(4, size / 2);
        int x0 = Math.Max(stride, (int)cx - half), x1 = Math.Min(width - stride, (int)cx + half);
        int y0 = Math.Max(stride, (int)cy - half), y1 = Math.Min(height - stride, (int)cy + half);
        if (x1 - x0 < 3 * stride || y1 - y0 < 3 * stride) return 0;
        double sum = 0, sumSq = 0; long count = 0;
        float lo = float.MaxValue, hi = float.MinValue;
        for (int y = y0; y < y1; y += stride) {
            int row = y * width;
            for (int x = x0; x < x1; x += stride) {
                float c = lumBlurred[row + x];
                if (c < lo) lo = c; if (c > hi) hi = c;
                double l = 4.0 * c - lumBlurred[row + x - stride] - lumBlurred[row + x + stride]
                                   - lumBlurred[row - stride * width + x] - lumBlurred[row + stride * width + x];
                sum += l; sumSq += l * l; count++;
            }
        }
        if (count == 0) return 0;
        double mean = sum / count, var = sumSq / count - mean * mean;
        double range = Math.Max(1.0, hi - lo);
        return Math.Sqrt(Math.Max(0, var)) / range;
    }

    /// <summary>Mean of the samples above <paramref name="threshold"/>: the
    /// brightness a frame is normalised by (PlanetarySystemStacker's
    /// frames_normalization, threshold on the object, not the sky).</summary>
    public static double MeanAbove(float[] plane, float threshold) {
        double sum = 0; long n = 0;
        foreach (var v in plane) if (v > threshold) { sum += v; n++; }
        return n == 0 ? 0 : sum / n;
    }

    /// <summary>Gain that brings a frame's object brightness to the
    /// reference's, clamped to [0.5, 2] so a cloud or a dropped frame cannot
    /// be amplified into the stack; 1 when either mean is unknown.</summary>
    public static float NormalisationGain(double referenceMean, double frameMean) {
        if (referenceMean <= 0 || frameMean <= 0) return 1f;
        return (float)Math.Clamp(referenceMean / frameMean, 0.5, 2.0);
    }

    /// <summary>Adds <paramref name="plane"/> shifted by (dx, dy) (sub-pixel,
    /// bilinear) into <paramref name="accum"/>, counting coverage in
    /// <paramref name="weight"/>. A destination pixel (x, y) reads the source
    /// at (x - dx, y - dy); pixels whose source falls outside are skipped.</summary>
    public static void AccumulateShifted(float[] plane, int width, int height, double dx, double dy,
                                         float[] accum, float[] weight, float gain = 1f) {
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
                accum[dstRow + x] += (float)v * gain;
                weight[dstRow + x] += 1f;
            }
        }
    }

    /// <summary>Lift the stack off the camera's black level and rescale it to
    /// use the 16-bit range, the way a planetary stacker is expected to hand its
    /// result over.
    ///
    /// Field clip 2026-09-05 (Saturn, ASI585MC, 640x480): the sky floor sat at
    /// 12 800 ADU in G, 13 900 in R and 19 200 in B, and the planet only reached
    /// ~1 600 counts above it. The stack was arithmetically correct and visually
    /// useless: every auto-stretch anchors on the median, which is sky, so the
    /// planet went to white and the belts with it.
    ///
    /// The floor is taken per channel (a 0.5% percentile, low enough that real
    /// surface detail is not clipped on a frame-filling Moon) so a coloured
    /// background is neutralised, while ONE gain is applied to every channel so
    /// the object's own colour balance survives. Returns what it did, for the log.
    /// A frame with no range left (flat, or already saturated) is left alone.</summary>
    public static (double[] Floor, double Gain) NormaliseLevels(ushort[] pixels, int channels, int npx,
                                                                double headroom = 0.92) {
        var floor = new double[channels];
        double span = 0;
        for (int c = 0; c < channels; c++) {
            var plane = new ushort[npx];
            Array.Copy(pixels, c * npx, plane, 0, npx);
            floor[c] = Percentile(plane, 0.005);
            span = Math.Max(span, Percentile(plane, 0.9999) - floor[c]);
        }
        if (span <= 1) return (floor, 1.0);
        double gain = 65535.0 * headroom / span;
        if (gain <= 1.0 && floor.All(f => f <= 65535 * 0.01)) return (floor, 1.0);   // already sane
        for (int c = 0; c < channels; c++) {
            int off = c * npx;
            double f = floor[c];
            for (int i = 0; i < npx; i++) {
                pixels[off + i] = (ushort)Math.Clamp(Math.Round((pixels[off + i] - f) * gain), 0, 65535);
            }
        }
        return (floor, gain);
    }

    /// <summary>Value at <paramref name="q"/> (0..1) of a copy of the samples.</summary>
    private static double Percentile(ushort[] samples, double q) {
        var sorted = (ushort[])samples.Clone();
        Array.Sort(sorted);
        int idx = (int)Math.Clamp(Math.Round(q * (sorted.Length - 1)), 0, sorted.Length - 1);
        return sorted[idx];
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
