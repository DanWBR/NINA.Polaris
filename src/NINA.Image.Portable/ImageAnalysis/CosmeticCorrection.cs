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
/// Cosmetic correction: automatic hot / cold pixel + bad-column removal.
/// Detects outliers relative to the local neighbourhood (sigma above/below
/// the channel median, gated by the average deviation) and replaces them
/// with a local median (cold) or 3x3 average (hot), blended by an amount.
///
/// Re-implemented in C# from Siril's <c>src/filters/cosmetic_correction.c</c>
/// (the <c>autoDetect</c> path, GPLv3; no code copied). Works in raw ADU per
/// channel like Siril does. An optional CFA-aware mode samples same-Bayer
/// neighbours (step 2) so it can run on undebayered OSC frames without
/// smearing the mosaic.
/// </summary>
public static class CosmeticCorrection {
    /// <summary>
    /// Apply cosmetic correction in place to a plane-sequential ushort buffer.
    /// <paramref name="sigmaCold"/> / <paramref name="sigmaHot"/> are the
    /// detection thresholds in units of the channel's average deviation
    /// (-1 disables that side). Returns (cold, hot) corrected pixel counts.
    /// </summary>
    public static (long cold, long hot) Apply(ushort[] data, int width, int height, int channels,
                                              double sigmaCold = 5.0, double sigmaHot = 3.0,
                                              double amount = 1.0, bool cfa = false) {
        long cold = 0, hot = 0;
        int ch = channels == 3 ? 3 : 1;
        long plane = (long)width * height;
        double f0 = Math.Clamp(amount, 0.0, 1.0);
        double f1 = 1.0 - f0;
        bool doHot = sigmaHot != -1.0;
        bool doCold = sigmaCold != -1.0;
        int step = cfa ? 2 : 1;
        int radius = 2 * step;

        for (int c = 0; c < ch; c++) {
            long baseIdx = (long)c * plane;
            // Channel stats: median (background) + average deviation.
            double median = Median(data, baseIdx, plane);
            double avgDev = AverageDeviation(data, baseIdx, plane, median);

            double k1 = avgDev;
            double k2 = k1 / 2.0;
            double k3 = sigmaHot * k1;
            double k4 = Math.Max(k1, k3);
            double k = avgDev * sigmaCold;
            double coldVal = doCold ? median - k : 0.0;
            double hotVal = doHot ? median + k1 : 65535.0;

            // Work off an immutable float snapshot so a corrected pixel does
            // not perturb its neighbours' detection (Siril's temp buffer).
            var temp = new float[plane];
            for (long i = 0; i < plane; i++) temp[i] = data[baseIdx + i];

            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    float pixel = temp[y * width + x];
                    // Only pixels OUTSIDE the [cold,hot] band are candidates.
                    if (pixel >= coldVal && pixel <= hotVal) continue;

                    double m = Median24(temp, x, y, width, height, step, radius);

                    if (doHot && pixel > hotVal && pixel > m + k4) {
                        double a = Average3x3(temp, x, y, width, height, step);
                        if (a < m + k2) {
                            hot++;
                            data[baseIdx + y * width + x] =
                                (ushort)Math.Clamp(Math.Round(a * f0 + pixel * f1), 0, 65535);
                        }
                    } else if (doCold && pixel < coldVal && pixel + k < m) {
                        cold++;
                        data[baseIdx + y * width + x] =
                            (ushort)Math.Clamp(Math.Round(m * f0 + pixel * f1), 0, 65535);
                    }
                }
            }
        }
        return (cold, hot);
    }

    private static double Median(ushort[] data, long offset, long count) {
        var hist = new long[65536];
        for (long i = 0; i < count; i++) hist[data[offset + i]]++;
        long half = count / 2;
        long acc = 0;
        for (int v = 0; v < 65536; v++) {
            acc += hist[v];
            if (acc >= half) return v;
        }
        return 0;
    }

    private static double AverageDeviation(ushort[] data, long offset, long count, double median) {
        double sum = 0;
        for (long i = 0; i < count; i++) sum += Math.Abs(data[offset + i] - median);
        return count == 0 ? 0 : sum / count;
    }

    // Median of the 5x5 (step-scaled for CFA) neighbourhood excluding the
    // centre, clamped to image bounds.
    private static double Median24(float[] buf, int xx, int yy, int w, int h, int step, int radius) {
        Span<float> value = stackalloc float[24];
        int n = 0;
        for (int y = yy - radius; y <= yy + radius; y += step) {
            if (y < 0 || y >= h) continue;
            for (int x = xx - radius; x <= xx + radius; x += step) {
                if (x < 0 || x >= w) continue;
                if (x == xx && y == yy) continue;
                value[n++] = buf[x + y * w];
            }
        }
        if (n == 0) return buf[xx + yy * w];
        var slice = value.Slice(0, n);
        slice.Sort();
        return (n % 2 == 1) ? slice[n / 2] : 0.5 * (slice[n / 2 - 1] + slice[n / 2]);
    }

    // Average of the 3x3 (step-scaled) neighbourhood excluding the centre.
    private static double Average3x3(float[] buf, int xx, int yy, int w, int h, int step) {
        double sum = 0;
        int n = 0;
        for (int y = yy - step; y <= yy + step; y += step) {
            if (y < 0 || y >= h) continue;
            for (int x = xx - step; x <= xx + step; x += step) {
                if (x < 0 || x >= w) continue;
                if (x == xx && y == yy) continue;
                sum += buf[x + y * w];
                n++;
            }
        }
        return n == 0 ? 0 : sum / n;
    }
}
