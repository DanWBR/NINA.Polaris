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

namespace NINA.Polaris.Services;

/// <summary>
/// Pure helpers for the native guider's dark library + bad-pixel map (the
/// guide-camera equivalent of PHD2's dark library / defect map). Kept
/// side-effect-free and camera-independent so they unit-test without
/// hardware: <see cref="NativeGuider"/> wires the capture loop + persistence
/// around them.
///
/// Guide frames are small (≤ a few megapixels) and calibration is a one-shot
/// build, so these stay simple (full-frame median/MAD, per-pixel loops)
/// rather than tiled like the STUDIO integrators.
/// </summary>
public static class GuideDarkMath {

    /// <summary>Per-pixel mean of N equally-sized frames → a master dark
    /// (rounded, clamped to ushort). Averaging beats out read noise so the
    /// subtracted dark current / amp glow is the fixed pattern, not noise.</summary>
    public static ushort[] MeanStack(IReadOnlyList<ushort[]> frames, int length) {
        var outBuf = new ushort[length];
        if (frames.Count == 0) return outBuf;
        var sum = new long[length];
        foreach (var f in frames) {
            int n = Math.Min(length, f.Length);
            for (int i = 0; i < n; i++) sum[i] += f[i];
        }
        int count = frames.Count;
        for (int i = 0; i < length; i++)
            outBuf[i] = (ushort)Math.Clamp((sum[i] + count / 2) / count, 0, 65535);
        return outBuf;
    }

    /// <summary>
    /// Identify hot (and dead) pixels in a master dark: those whose value is
    /// more than <paramref name="sigmaK"/> robust sigmas from the frame median.
    /// Robust sigma = 1.4826·MAD (median absolute deviation), so a handful of
    /// blazing hot pixels don't inflate the threshold the way stdev would.
    /// Returns the flat indices, sorted ascending.
    /// </summary>
    public static int[] DetectBadPixels(ushort[] dark, double sigmaK = 8.0) {
        if (dark.Length == 0) return Array.Empty<int>();

        // Median.
        var sorted = (ushort[])dark.Clone();
        Array.Sort(sorted);
        double median = sorted[sorted.Length / 2];

        // MAD: median of |x - median|.
        var dev = new int[dark.Length];
        for (int i = 0; i < dark.Length; i++) dev[i] = Math.Abs(dark[i] - (int)median);
        Array.Sort(dev);
        double mad = dev[dev.Length / 2];
        double robustSigma = Math.Max(1.0, 1.4826 * mad);   // floor so a near-flat dark still finds true outliers

        double hi = median + sigmaK * robustSigma;
        double lo = median - sigmaK * robustSigma;

        var bad = new List<int>();
        for (int i = 0; i < dark.Length; i++) {
            int v = dark[i];
            if (v > hi || v < lo) bad.Add(i);
        }
        return bad.ToArray();
    }

    /// <summary>Subtract the master dark from a frame in place (clamped at 0).
    /// Removes the bias + dark-current fixed pattern so the star detector sees
    /// a flat background. No-op when sizes disagree.</summary>
    public static void SubtractDarkInPlace(ushort[] frame, ushort[] dark) {
        if (frame.Length != dark.Length) return;
        for (int i = 0; i < frame.Length; i++) {
            int v = frame[i] - dark[i];
            frame[i] = v <= 0 ? (ushort)0 : (ushort)v;
        }
    }

    /// <summary>Replace each bad pixel with the median of its up-to-8
    /// neighbours that aren't themselves bad, in place. Stops a stuck hot
    /// pixel from masquerading as a guide star without touching real signal
    /// elsewhere. <paramref name="bad"/> is the flat-index set from
    /// <see cref="DetectBadPixels"/>.</summary>
    public static void ApplyBadPixelsInPlace(ushort[] frame, int width, int height, HashSet<int> bad) {
        if (bad.Count == 0 || frame.Length != (long)width * height) return;
        Span<ushort> nb = stackalloc ushort[8];
        foreach (var idx in bad) {
            if (idx < 0 || idx >= frame.Length) continue;
            int x = idx % width;
            int y = idx / width;
            int n = 0;
            for (int dy = -1; dy <= 1; dy++) {
                int ny = y + dy;
                if (ny < 0 || ny >= height) continue;
                for (int dx = -1; dx <= 1; dx++) {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx;
                    if (nx < 0 || nx >= width) continue;
                    int nIdx = ny * width + nx;
                    if (bad.Contains(nIdx)) continue;   // don't average in another defect
                    nb[n++] = frame[nIdx];
                }
            }
            if (n == 0) continue;                       // fully surrounded by defects: leave as-is
            // Median of the collected neighbours.
            var slice = nb[..n];
            slice.Sort();
            frame[idx] = slice[n / 2];
        }
    }
}
