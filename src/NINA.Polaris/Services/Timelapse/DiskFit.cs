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

namespace NINA.Polaris.Services.Timelapse;

/// <summary>
/// Finds the center of a bright Sun/Moon disc from its limb (edge), which is
/// what the centroid can't do during a partial eclipse: the centroid of a
/// crescent leans toward the bright side, but the limb is an arc of the true
/// disc and a circle fit to it recovers the real center.
///
/// Method: threshold at the half-height between background and peak, collect the
/// bright region's boundary pixels, fit a circle (Kasa algebraic least squares),
/// then sigma-clip the radial residuals and re-fit a few times. The clipping
/// drops the minority arc (the Moon's terminator, in a solar eclipse) and the
/// dominant arc (the Sun's limb, most of the circle in the partial phases)
/// wins, so the fit converges on the Sun's disc. Pure and Skia-free.
/// </summary>
public static class DiskFit {

    /// <summary>Try to locate the disc center by fitting a circle to its limb.
    /// Returns false (leaving the frame-center defaults) when there is no clear
    /// bounded disc: an empty frame, a frame-filling surface (limb off-frame),
    /// or a fit that fails the radius/position sanity checks. Callers fall back
    /// to the centroid then.</summary>
    public static bool TryFindCenter(ushort[] lum, int width, int height,
                                     out double cx, out double cy) {
        cx = width / 2.0; cy = height / 2.0;
        if (lum == null || width < 8 || height < 8 || lum.Length < (long)width * height)
            return false;

        // Background (min over a strided sample) and peak (max), like CentroidAligner.
        ushort peak = 0, bg = ushort.MaxValue;
        for (int i = 0; i < lum.Length; i++) if (lum[i] > peak) peak = lum[i];
        for (int i = 0; i < lum.Length; i += 97) if (lum[i] < bg) bg = lum[i];
        if (peak <= bg) return false;

        // Half-height threshold: the limb is where brightness crosses it.
        double thr = bg + 0.5 * (peak - bg);

        // Boundary pixels: bright, with at least one dark 4-neighbour (interior
        // only, so a subject touching the frame edge doesn't add false points).
        var xs = new List<double>();
        var ys = new List<double>();
        for (int y = 1; y < height - 1; y++) {
            int row = y * width;
            for (int x = 1; x < width - 1; x++) {
                if (lum[row + x] < thr) continue;
                if (lum[row + x - 1] < thr || lum[row + x + 1] < thr
                    || lum[row - width + x] < thr || lum[row + width + x] < thr) {
                    xs.Add(x); ys.Add(y);
                }
            }
        }
        if (xs.Count < 24) return false;

        // Cap the point count for speed (a big disc has thousands of edge px).
        const int cap = 4000;
        if (xs.Count > cap) {
            int stride = xs.Count / cap + 1;
            var sx = new List<double>(cap);
            var sy = new List<double>(cap);
            for (int i = 0; i < xs.Count; i += stride) { sx.Add(xs[i]); sy.Add(ys[i]); }
            xs = sx; ys = sy;
        }

        // Fit + sigma-clip re-fit. Start with all points; each pass drops the
        // ones whose radius disagrees most (the terminator arc, noise).
        var idx = new List<int>(xs.Count);
        for (int i = 0; i < xs.Count; i++) idx.Add(i);

        double fx = cx, fy = cy, r = 0;
        for (int pass = 0; pass < 4; pass++) {
            if (idx.Count < 12) break;
            if (!Kasa(xs, ys, idx, out fx, out fy, out r)) return false;
            if (pass == 3) break;

            // Radial residuals; keep points within a robust band of the median.
            var resid = new double[idx.Count];
            for (int k = 0; k < idx.Count; k++) {
                double dx = xs[idx[k]] - fx, dy = ys[idx[k]] - fy;
                resid[k] = Math.Abs(Math.Sqrt(dx * dx + dy * dy) - r);
            }
            double med = Median(resid);
            double keep = Math.Max(1.5, 3.0 * med);
            var next = new List<int>(idx.Count);
            for (int k = 0; k < idx.Count; k++) if (resid[k] <= keep) next.Add(idx[k]);
            if (next.Count < 12 || next.Count == idx.Count) break;   // converged / too few
            idx = next;
        }

        // Sanity: a plausible radius and a center not wildly outside the frame.
        double maxR = Math.Sqrt((double)width * width + (double)height * height);
        if (r < 4 || r > maxR) return false;
        if (fx < -0.5 * width || fx > 1.5 * width || fy < -0.5 * height || fy > 1.5 * height)
            return false;

        cx = fx; cy = fy;
        return true;
    }

    // Kasa algebraic circle fit over the selected point indices.
    private static bool Kasa(List<double> xs, List<double> ys, List<int> idx,
                             out double cx, out double cy, out double r) {
        cx = cy = r = 0;
        int n = idx.Count;
        if (n < 3) return false;
        double xm = 0, ym = 0;
        foreach (var i in idx) { xm += xs[i]; ym += ys[i]; }
        xm /= n; ym /= n;

        double Suu = 0, Svv = 0, Suv = 0, Suuu = 0, Svvv = 0, Suvv = 0, Svuu = 0;
        foreach (var i in idx) {
            double u = xs[i] - xm, v = ys[i] - ym;
            double uu = u * u, vv = v * v;
            Suu += uu; Svv += vv; Suv += u * v;
            Suuu += uu * u; Svvv += vv * v; Suvv += u * vv; Svuu += v * uu;
        }
        double det = Suu * Svv - Suv * Suv;
        if (Math.Abs(det) < 1e-9) return false;

        double b1 = 0.5 * (Suuu + Suvv);
        double b2 = 0.5 * (Svvv + Svuu);
        double uc = (b1 * Svv - b2 * Suv) / det;
        double vc = (Suu * b2 - Suv * b1) / det;

        cx = uc + xm; cy = vc + ym;
        r = Math.Sqrt(uc * uc + vc * vc + (Suu + Svv) / n);
        return true;
    }

    private static double Median(double[] v) {
        var a = (double[])v.Clone();
        Array.Sort(a);
        int m = a.Length / 2;
        return a.Length % 2 == 1 ? a[m] : 0.5 * (a[m - 1] + a[m]);
    }
}
