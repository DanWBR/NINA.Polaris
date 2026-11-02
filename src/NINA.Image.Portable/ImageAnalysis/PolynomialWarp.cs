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

namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Second-order polynomial registration: for each pixel of the reference grid,
/// where to read the current frame.
///
/// <para>An affine transform covers shift, rotation and scale, which is all a
/// rigid camera on a tracking mount should need. It stops being enough when
/// the field rotates for hours (poor polar alignment) or sits low in the sky:
/// the optics' distortion stays put on the sensor while the stars turn through
/// it, and differential refraction squeezes the field more at the bottom than
/// at the top as the altitude changes. Neither is linear. Measured on a
/// two-hour T Cep run (SQA55 + ATR2600C, Florania, 2026-11-01, 1.2 degrees of
/// rotation by the last frame): after the best affine fit the corner stars
/// were still off by 1.7 px (median), 5 px for one star in ten; a quadratic
/// brought every zone of the frame under 0.35 px.</para>
///
/// <para>The quadratic is fitted on the star pairs the affine match already
/// found, so it only refines; when it does not clearly beat the affine (the
/// common case early in a session) <see cref="FromStars"/> returns null and the
/// caller keeps the affine warp.</para>
/// </summary>
public sealed class PolynomialWarp {
    // src = c0 + c1 u + c2 v + c3 u^2 + c4 u v + c5 v^2, with u, v the output
    // pixel centred on the frame and scaled to about +-1 (keeps the normal
    // equations well conditioned on a 6000 px sensor).
    private readonly double[] _cx, _cy;
    private readonly double _x0, _y0, _scale;

    /// <summary>RMS distance between fitted and measured star positions, px.</summary>
    public double RmsResidual { get; }
    /// <summary>Star pairs the fit used.</summary>
    public int Pairs { get; }

    /// <summary>Fewest pairs a fit accepts: twelve unknowns, with margin.</summary>
    public const int MinPairs = 24;

    private PolynomialWarp(double[] cx, double[] cy, double x0, double y0, double scale, double rms, int pairs) {
        _cx = cx; _cy = cy; _x0 = x0; _y0 = y0; _scale = scale; RmsResidual = rms; Pairs = pairs;
    }

    /// <summary>Source position for an output (reference-grid) pixel.</summary>
    public (double x, double y) Apply(double x, double y) {
        double u = (x - _x0) / _scale, v = (y - _y0) / _scale;
        return (Eval(_cx, u, v), Eval(_cy, u, v));
    }

    private static double Eval(double[] c, double u, double v)
        => c[0] + c[1] * u + c[2] * v + c[3] * u * u + c[4] * u * v + c[5] * v * v;

    /// <summary>For one output row, the source x and y as quadratics in the
    /// column: src = a + b u + c u^2 with u = (x - X0) / Scale.</summary>
    internal void Row(double y, out double ax, out double bx, out double cx,
                      out double ay, out double by, out double cy) {
        double v = (y - _y0) / _scale;
        ax = _cx[0] + _cx[2] * v + _cx[5] * v * v; bx = _cx[1] + _cx[4] * v; cx = _cx[3];
        ay = _cy[0] + _cy[2] * v + _cy[5] * v * v; by = _cy[1] + _cy[4] * v; cy = _cy[3];
    }
    internal double X0 => _x0;
    internal double Scale => _scale;

    /// <summary>Least-squares fit through point pairs (output -> source).</summary>
    public static PolynomialWarp? Fit(IReadOnlyList<(double x, double y)> output,
            IReadOnlyList<(double x, double y)> source, int width, int height) {
        int n = Math.Min(output.Count, source.Count);
        if (n < 6) return null;
        double x0 = width / 2.0, y0 = height / 2.0, scale = Math.Max(width, height) / 2.0;
        var ata = new double[6, 6];
        var bx = new double[6];
        var by = new double[6];
        var t = new double[6];
        for (int k = 0; k < n; k++) {
            Terms((output[k].x - x0) / scale, (output[k].y - y0) / scale, t);
            for (int i = 0; i < 6; i++) {
                bx[i] += t[i] * source[k].x;
                by[i] += t[i] * source[k].y;
                for (int j = 0; j < 6; j++) ata[i, j] += t[i] * t[j];
            }
        }
        var cx = Solve(ata, bx);
        var cy = Solve(ata, by);
        if (cx == null || cy == null) return null;

        var probe = new PolynomialWarp(cx, cy, x0, y0, scale, 0, n);
        double sum = 0;
        for (int k = 0; k < n; k++) {
            var (px, py) = probe.Apply(output[k].x, output[k].y);
            sum += (px - source[k].x) * (px - source[k].x) + (py - source[k].y) * (py - source[k].y);
        }
        return new PolynomialWarp(cx, cy, x0, y0, scale, Math.Sqrt(sum / n), n);
    }

    /// <summary>
    /// Refine an affine star match into a quadratic. Pairs every current star
    /// through <paramref name="curToRef"/> with its reference star, fits the
    /// quadratic, drops the pairs it cannot explain, and fits again.
    ///
    /// Returns null, so the caller keeps the affine warp, when there are too
    /// few pairs, when they do not span the frame (a quadratic extrapolated
    /// past its stars bends freely), when the quadratic is not clearly better
    /// than the affine on the same stars, or when it strays far from the
    /// affine anywhere on the frame edge.
    /// </summary>
    public static PolynomialWarp? FromStars(List<DetectedStar> refs, List<DetectedStar> curs,
            AffineTransform curToRef, int width, int height) {
        if (refs.Count < MinPairs || curs.Count < MinPairs) return null;

        var outPts = new List<(double x, double y)>();
        var srcPts = new List<(double x, double y)>();
        double affineSum = 0;
        var taken = new HashSet<int>();
        foreach (var c in curs) {
            var (x, y) = curToRef.Apply(c.X, c.Y);
            int best = -1;
            double bestD2 = 3.0 * 3.0;
            for (int i = 0; i < refs.Count; i++) {
                double dx = refs[i].X - x, dy = refs[i].Y - y, d2 = dx * dx + dy * dy;
                if (d2 < bestD2 && !taken.Contains(i)) { bestD2 = d2; best = i; }
            }
            if (best < 0) continue;
            taken.Add(best);
            outPts.Add((refs[best].X, refs[best].Y));
            srcPts.Add((c.X, c.Y));
            affineSum += bestD2;
        }
        if (outPts.Count < MinPairs) return null;

        double minX = outPts.Min(p => p.x), maxX = outPts.Max(p => p.x);
        double minY = outPts.Min(p => p.y), maxY = outPts.Max(p => p.y);
        if (maxX - minX < 0.6 * width || maxY - minY < 0.6 * height) return null;

        var warp = Fit(outPts, srcPts, width, height);
        if (warp == null) return null;

        // One clipping pass: a blended or mis-centred star should not pull the
        // whole surface.
        double clip = Math.Max(0.5, 3 * warp.RmsResidual);
        var keepOut = new List<(double x, double y)>();
        var keepSrc = new List<(double x, double y)>();
        for (int k = 0; k < outPts.Count; k++) {
            var (px, py) = warp.Apply(outPts[k].x, outPts[k].y);
            double d = Math.Sqrt((px - srcPts[k].x) * (px - srcPts[k].x) + (py - srcPts[k].y) * (py - srcPts[k].y));
            if (d <= clip) { keepOut.Add(outPts[k]); keepSrc.Add(srcPts[k]); }
        }
        if (keepOut.Count < MinPairs) return null;
        warp = Fit(keepOut, keepSrc, width, height);
        if (warp == null) return null;

        // Worth it only when it clearly beats the affine on the same stars.
        double affineRms = Math.Sqrt(affineSum / outPts.Count);
        if (warp.RmsResidual > 0.8 * affineRms) return null;

        // Bounded departure from the affine along the frame edge.
        var refToCur = Invert(curToRef);
        if (refToCur == null) return null;
        for (int i = 0; i <= 4; i++) {
            double fx = i / 4.0 * (width - 1), fy = i / 4.0 * (height - 1);
            foreach (var (x, y) in new[] { (fx, 0.0), (fx, height - 1.0), (0.0, fy), (width - 1.0, fy) }) {
                var (qx, qy) = warp.Apply(x, y);
                var (ax, ay) = refToCur.Apply(x, y);
                if ((qx - ax) * (qx - ax) + (qy - ay) * (qy - ay) > 25.0 * 25.0) return null;
            }
        }
        return warp;
    }

    private static AffineTransform? Invert(AffineTransform t) {
        double det = t.M00 * t.M11 - t.M01 * t.M10;
        if (Math.Abs(det) < 1e-12) return null;
        double a = t.M11 / det, b = -t.M01 / det, c = -t.M10 / det, d = t.M00 / det;
        return new AffineTransform {
            M00 = a, M01 = b, M10 = c, M11 = d,
            Tx = -(a * t.Tx + b * t.Ty), Ty = -(c * t.Tx + d * t.Ty)
        };
    }

    private static void Terms(double u, double v, double[] t) {
        t[0] = 1; t[1] = u; t[2] = v; t[3] = u * u; t[4] = u * v; t[5] = v * v;
    }

    /// <summary>Gaussian elimination with partial pivoting on a copy.</summary>
    private static double[]? Solve(double[,] a0, double[] b0) {
        int n = b0.Length;
        var a = (double[,])a0.Clone();
        var b = (double[])b0.Clone();
        for (int col = 0; col < n; col++) {
            int p = col;
            for (int r = col + 1; r < n; r++)
                if (Math.Abs(a[r, col]) > Math.Abs(a[p, col])) p = r;
            if (Math.Abs(a[p, col]) < 1e-12) return null;
            if (p != col) {
                for (int k = 0; k < n; k++) (a[col, k], a[p, k]) = (a[p, k], a[col, k]);
                (b[col], b[p]) = (b[p], b[col]);
            }
            for (int r = col + 1; r < n; r++) {
                double f = a[r, col] / a[col, col];
                for (int k = col; k < n; k++) a[r, k] -= f * a[col, k];
                b[r] -= f * b[col];
            }
        }
        var x = new double[n];
        for (int r = n - 1; r >= 0; r--) {
            double s = b[r];
            for (int k = r + 1; k < n; k++) s -= a[r, k] * x[k];
            x[r] = s / a[r, r];
        }
        return x;
    }
}
