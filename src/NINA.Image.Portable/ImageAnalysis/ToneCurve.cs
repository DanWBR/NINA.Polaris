namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Tone curve interpolation. Caller supplies control points (anchor pairs
/// in 0..255 space), this builds a smooth byte[256] lookup table the
/// pipeline can apply with a single array index per pixel.
///
/// Why a natural cubic spline (not Bezier or Catmull-Rom): natural cubics
/// guarantee C² continuity at the knots and have zero curvature at the
/// endpoints, which gives the smooth "S-curve" feel Lightroom users
/// expect. Bezier is harder to constrain at endpoints without extra
/// handles; Catmull-Rom can overshoot wildly with closely-spaced control
/// points and we'd have to clamp anyway.
///
/// A degenerate case worth calling out: identity curve (just (0,0) and
/// (255,255)) produces a strictly linear LUT — no rounding noise added.
/// </summary>
public static class ToneCurve {

    /// <summary>
    /// Build a 256-entry LUT from control points. Points must be sorted
    /// by X ascending; X values are clamped to [0,255]. Two or more
    /// points required (typically the endpoints + interior anchors).
    /// </summary>
    public static byte[] Build(IReadOnlyList<(double x, double y)> points) {
        var lut = new byte[256];

        if (points == null || points.Count < 2) {
            // Identity LUT.
            for (int i = 0; i < 256; i++) lut[i] = (byte)i;
            return lut;
        }

        // Defensive: copy + sort + clamp.
        var pts = points
            .Select(p => (x: Math.Clamp(p.x, 0, 255), y: Math.Clamp(p.y, 0, 255)))
            .OrderBy(p => p.x)
            .ToList();

        // Ensure x values strictly increasing — collapse duplicates by
        // keeping the *last* one (matches Lightroom's "drag to same X
        // overrides" feel).
        for (int i = 1; i < pts.Count; i++) {
            if (pts[i].x <= pts[i - 1].x) {
                pts[i] = (pts[i - 1].x + 1, pts[i].y);
            }
        }

        int n = pts.Count;
        var xs = pts.Select(p => p.x).ToArray();
        var ys = pts.Select(p => p.y).ToArray();

        // Solve tridiagonal system for natural cubic spline second
        // derivatives (Numerical Recipes 3.3, "spline").
        var y2 = new double[n];
        var u = new double[n];
        y2[0] = 0; u[0] = 0;       // natural BC
        for (int i = 1; i < n - 1; i++) {
            double sig = (xs[i] - xs[i - 1]) / (xs[i + 1] - xs[i - 1]);
            double p = sig * y2[i - 1] + 2.0;
            y2[i] = (sig - 1.0) / p;
            u[i] = (ys[i + 1] - ys[i]) / (xs[i + 1] - xs[i])
                 - (ys[i] - ys[i - 1]) / (xs[i] - xs[i - 1]);
            u[i] = (6.0 * u[i] / (xs[i + 1] - xs[i - 1]) - sig * u[i - 1]) / p;
        }
        y2[n - 1] = 0;             // natural BC
        for (int k = n - 2; k >= 0; k--) {
            y2[k] = y2[k] * y2[k + 1] + u[k];
        }

        // Fill LUT by interpolation at each integer X.
        int lo = 0;
        for (int i = 0; i < 256; i++) {
            double x = i;
            // Advance lo until x is between xs[lo]..xs[lo+1].
            while (lo < n - 2 && xs[lo + 1] < x) lo++;
            int hi = lo + 1;
            // Hold endpoints flat outside the defined range.
            if (x <= xs[0]) { lut[i] = (byte)Math.Clamp(Math.Round(ys[0]), 0, 255); continue; }
            if (x >= xs[n - 1]) { lut[i] = (byte)Math.Clamp(Math.Round(ys[n - 1]), 0, 255); continue; }

            double h = xs[hi] - xs[lo];
            double a = (xs[hi] - x) / h;
            double b = (x - xs[lo]) / h;
            double y = a * ys[lo] + b * ys[hi]
                     + ((a * a * a - a) * y2[lo] + (b * b * b - b) * y2[hi]) * (h * h) / 6.0;
            lut[i] = (byte)Math.Clamp(Math.Round(y), 0, 255);
        }
        return lut;
    }

    /// <summary>
    /// Identity LUT (no change). Convenience for "this slider is at
    /// default" — pipeline can skip the apply step entirely.
    /// </summary>
    public static byte[] Identity() {
        var lut = new byte[256];
        for (int i = 0; i < 256; i++) lut[i] = (byte)i;
        return lut;
    }
}
