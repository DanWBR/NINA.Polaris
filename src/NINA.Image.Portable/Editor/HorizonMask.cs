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

namespace NINA.Image.Editor;

/// <summary>
/// Turns a hand-drawn horizon line into a per-pixel <b>foreground coverage</b>
/// map for the nightscape composite: 1 below the line (the landscape), 0 above
/// it (the sky), with a soft feathered band across the transition so stars just
/// above a tree line don't get cut with a hard edge.
///
/// <para>The line is a polyline in NORMALISED frame coordinates: each point is
/// (x, y) in 0..1, y increasing downward, the same space the drawing canvas
/// uses. Points are read left to right by x; the segment is interpolated
/// linearly between them and held flat before the first and after the last, so
/// a two-point line already spans the whole width.</para>
/// </summary>
public static class HorizonMask {

    /// <summary>Build the foreground coverage (row-major, length w*h, values
    /// 0..1). <paramref name="featherPx"/> is the HALF-width of the soft band
    /// in pixels; 0 gives a hard edge. An empty line yields all-sky (0).</summary>
    public static float[] BuildCoverage(
            IReadOnlyList<(double X, double Y)> line, int width, int height, double featherPx) {
        if (width <= 0 || height <= 0) return System.Array.Empty<float>();
        var cov = new float[width * height];
        if (line == null || line.Count == 0) return cov;   // no horizon -> all sky

        // Sort a copy by x so the caller can hand points in click order.
        var pts = new List<(double X, double Y)>(line);
        pts.Sort((a, b) => a.X.CompareTo(b.X));

        // Horizon Y (in pixels) at each column, from the interpolated line.
        var horizonY = new double[width];
        for (int px = 0; px < width; px++) {
            double xn = width == 1 ? 0.0 : (double)px / (width - 1);
            horizonY[px] = InterpolateY(pts, xn) * (height - 1);
        }

        double feather = featherPx > 0 ? featherPx : 0.0;
        for (int px = 0; px < width; px++) {
            double hy = horizonY[px];
            for (int py = 0; py < height; py++) {
                // Positive distance = below the line = foreground.
                double d = py - hy;
                float c;
                if (feather <= 0) {
                    c = d >= 0 ? 1f : 0f;
                } else {
                    double t = (d + feather) / (2 * feather);   // 0 at top of band, 1 at bottom
                    c = (float)Smoothstep(t);
                }
                cov[py * width + px] = c;
            }
        }
        return cov;
    }

    /// <summary>Interpolated horizon y (normalised 0..1) at normalised x,
    /// clamped flat outside the point range.</summary>
    private static double InterpolateY(List<(double X, double Y)> pts, double xn) {
        if (pts.Count == 1 || xn <= pts[0].X) return pts[0].Y;
        var last = pts[pts.Count - 1];
        if (xn >= last.X) return last.Y;
        for (int i = 1; i < pts.Count; i++) {
            if (xn <= pts[i].X) {
                var a = pts[i - 1];
                var b = pts[i];
                double span = b.X - a.X;
                double f = span <= 0 ? 0 : (xn - a.X) / span;
                return a.Y + (b.Y - a.Y) * f;
            }
        }
        return last.Y;
    }

    /// <summary>Hermite smoothstep, clamped to 0..1.</summary>
    private static double Smoothstep(double t) {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        return t * t * (3 - 2 * t);
    }
}
