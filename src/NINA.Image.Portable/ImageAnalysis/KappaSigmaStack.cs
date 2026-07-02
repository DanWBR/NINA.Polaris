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
/// Incremental per-pixel kappa-sigma outlier rejection for a live (running)
/// stack. Each pixel keeps a running mean (sum / count) and Welford's M2 (the
/// sum of squared deviations of the accepted samples). Before a new sample is
/// added the pixel's inlier standard deviation is estimated; a sample that lies
/// more than <c>kappa</c> sigma from the running mean is rejected (not added),
/// so cosmic-ray hits, satellite/plane trails and hot pixels that dither onto
/// this sky position on only a few frames are kept out of the integration.
///
/// Welford's online algorithm is used (not sum + sum-of-squares) because the
/// naive variance suffers catastrophic cancellation for bright pixels, which
/// would drive the estimated sigma to zero and over-reject star cores.
///
/// Pure math over flat arrays (no per-pixel objects); the running mean the live
/// stack already divides for display is exactly <c>sum[i] / count[i]</c>, so the
/// output path is unchanged whether rejection ran or not. Reimplemented from the
/// standard kappa-sigma clipping + Welford update; not copied from any tool.
/// </summary>
public static class KappaSigmaStack {
    /// <summary>
    /// Decide whether <paramref name="x"/> is an inlier for pixel
    /// <paramref name="i"/>, given its current running mean and Welford M2.
    /// The first <paramref name="minFrames"/> samples are always accepted so a
    /// spread estimate can build up. Returns false to reject.
    /// </summary>
    public static bool Accept(float[] sum, int[] count, float[] m2, int i,
                              double x, int minFrames, double kappa) {
        int n = count[i];
        if (n < minFrames) return true;
        double mean = sum[i] / n;
        double variance = n > 1 ? m2[i] / (n - 1) : 0.0;
        double std = Math.Sqrt(variance);
        // std == 0 means every accepted sample so far was identical; with no
        // spread to compare against we accept rather than reject everything.
        if (std <= 0) return true;
        return Math.Abs(x - mean) <= kappa * std;
    }

    /// <summary>
    /// Fold an accepted sample <paramref name="x"/> into pixel
    /// <paramref name="i"/>'s running sum, count and Welford M2.
    /// </summary>
    public static void Update(float[] sum, int[] count, float[] m2, int i, double x) {
        int n = count[i];
        double oldMean = n > 0 ? sum[i] / n : 0.0;
        double newSum = sum[i] + x;
        int newCount = n + 1;
        double newMean = newSum / newCount;
        // Welford: M2 += (x - oldMean) * (x - newMean)
        m2[i] += (float)((x - oldMean) * (x - newMean));
        sum[i] = (float)newSum;
        count[i] = newCount;
    }

    /// <summary>
    /// Convenience for the mono path: decide + (if accepted) update in one call.
    /// Returns true if the sample was accepted (and folded in).
    /// </summary>
    public static bool Accumulate(float[] sum, int[] count, float[] m2, int i,
                                  double x, int minFrames, double kappa) {
        if (!Accept(sum, count, m2, i, x, minFrames, kappa)) return false;
        Update(sum, count, m2, i, x);
        return true;
    }
}
