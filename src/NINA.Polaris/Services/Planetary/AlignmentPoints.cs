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

namespace NINA.Polaris.Services.Planetary;

/// <summary>
/// Local ("alignment point") registration, after the design of Rolf Hempel's
/// PlanetarySystemStacker (GPL-3, github.com/Rolf-Hempel/PlanetarySystemStacker):
/// the ideas are re-implemented here, no code is copied.
///
/// A staggered mesh of alignment points (APs) is laid over the reference
/// image; points on dark or structureless patches are dropped. For every AP
/// each frame is ranked by its LOCAL sharpness, the best few percent are
/// kept, and each kept frame is registered to the reference locally by
/// normalised cross-correlation (coarse stride-2 search, fine search, then
/// a quadratic sub-pixel fit), which undoes the seeing's local distortion
/// that a single global shift cannot. Patches are blended with linear ramps
/// so overlapping APs fade into each other, and the globally aligned mean of
/// the best frames fills whatever the mesh does not cover.
/// </summary>
public static class AlignmentPoints {
    /// <summary>One alignment point: box = where the local shift is measured,
    /// patch = what is stacked (same size, ramp-weighted).</summary>
    public sealed class Point {
        public int Cx, Cy, HalfBox;
        public int X0 => Cx - HalfBox; public int Y0 => Cy - HalfBox;
        public int X1 => Cx + HalfBox; public int Y1 => Cy + HalfBox;   // exclusive
        public double Structure;
        public int[] BestFrames = Array.Empty<int>();
    }

    public readonly record struct MeshOptions(
        int HalfBox = 24, int SearchWidth = 14,
        double StructureThreshold = 0.04, double BrightnessFraction = 0.10, double DimFraction = 0.6);

    /// <summary>Lays the staggered mesh over <paramref name="refLumBlurred"/>
    /// and keeps the points whose box is bright enough and shows structure.
    /// PSS spacing: one half-box, so neighbouring patches overlap by half.</summary>
    public static List<Point> BuildMesh(float[] refLumBlurred, int width, int height, MeshOptions o) {
        var (bg, peak) = PlanetaryFrames.Levels(refLumBlurred);
        double range = Math.Max(1.0, peak - bg);
        float bright = (float)(bg + o.BrightnessFraction * range);
        int margin = o.HalfBox + o.SearchWidth;                // box + search must stay inside the frame
        int step = o.HalfBox;
        var pts = new List<Point>();
        if (width - 2 * margin < 2 * o.HalfBox || height - 2 * margin < 2 * o.HalfBox) return pts;
        int row = 0;
        for (int cy = margin + o.HalfBox; cy + o.HalfBox + o.SearchWidth <= height; cy += step, row++) {
            int offset = (row % 2 == 0) ? 0 : step / 2;       // staggered rows
            for (int cx = margin + o.HalfBox + offset; cx + o.HalfBox + o.SearchWidth <= width; cx += step) {
                var p = new Point { Cx = cx, Cy = cy, HalfBox = o.HalfBox };
                // brightness: the box must hold something above the sky, and
                // must not be mostly dark
                float max = float.MinValue; long dim = 0, n = 0;
                for (int y = p.Y0; y < p.Y1; y++) {
                    int r = y * width;
                    for (int x = p.X0; x < p.X1; x++) {
                        float v = refLumBlurred[r + x];
                        if (v > max) max = v;
                        if (v < bright) dim++;
                        n++;
                    }
                }
                if (max <= bright || (double)dim / n > o.DimFraction) continue;
                p.Structure = BoxStructure(refLumBlurred, width, p, range);
                pts.Add(p);
            }
        }
        // PSS keeps a point when its structure is at least StructureThreshold
        // of the BEST point's (4% by default): the threshold is relative, so
        // it works for a soft planet and a crisp lunar surface alike.
        if (pts.Count == 0) return pts;
        double maxStructure = pts.Max(q => q.Structure);
        if (maxStructure <= 0) return new List<Point>();
        return pts.Where(q => q.Structure >= o.StructureThreshold * maxStructure).ToList();
    }

    /// <summary>Normalised structure of a box: standard deviation of the
    /// Laplacian of the (already blurred) luminance, over the image's dynamic
    /// range. Dimensionless, so one threshold serves every exposure.</summary>
    public static double BoxStructure(float[] lumBlurred, int width, Point p, double range) {
        double sum = 0, sumSq = 0; long count = 0;
        for (int y = p.Y0 + 1; y < p.Y1 - 1; y++) {
            int r = y * width;
            for (int x = p.X0 + 1; x < p.X1 - 1; x++) {
                double l = 4.0 * lumBlurred[r + x] - lumBlurred[r + x - 1] - lumBlurred[r + x + 1]
                         - lumBlurred[r - width + x] - lumBlurred[r + width + x];
                sum += l; sumSq += l * l; count++;
            }
        }
        if (count == 0) return 0;
        double mean = sum / count, var = sumSq / count - mean * mean;
        return Math.Sqrt(Math.Max(0, var)) / Math.Max(1.0, range);
    }

    /// <summary>Local sharpness of a frame at an AP: the same measure, on the
    /// frame's blurred luminance at the AP box moved by the frame's global
    /// shift (frame = reference shifted by (-gdx, -gdy)).</summary>
    public static double LocalSharpness(float[] frameLumBlurred, int width, int height, Point p,
                                        double gdx, double gdy, double range) {
        int ox = (int)Math.Round(-gdx), oy = (int)Math.Round(-gdy);
        var q = new Point { Cx = p.Cx + ox, Cy = p.Cy + oy, HalfBox = p.HalfBox };
        if (q.X0 < 1 || q.Y0 < 1 || q.X1 >= width - 1 || q.Y1 >= height - 1) return 0;
        return BoxStructure(frameLumBlurred, width, q, range);
    }

    /// <summary>Multi-level normalised cross-correlation: finds the shift
    /// (dx, dy) such that the frame's content at (x - dx, y - dy) matches the
    /// reference box, i.e. the same convention as the global shifts (a
    /// destination pixel reads the source at minus the shift). Coarse search
    /// on a stride-2 grid over the whole search width, fine search ±4 at full
    /// resolution, then a quadratic fit on the 3×3 neighbourhood for the
    /// sub-pixel offset (accepted only when it stays within one pixel).
    /// Returns null when the optimum sits on the search border (no match).</summary>
    /// <summary>Lowest normalised correlation accepted as a match; the
    /// same detail seen through seeing correlates far above this, unrelated
    /// texture inside the search window does not.</summary>
    public const double MinCorrelation = 0.6;
    /// <summary>PSS penalty_factor: the coarse correlation is scaled by
    /// 1 - penalty × (dx² + dy²), so a distant spurious maximum loses to a
    /// nearby honest one.</summary>
    public const double PenaltyFactor = 0.00025;

    public static (double dx, double dy)? LocalShift(float[] refLumBlurred, float[] frameLumBlurred,
                                                     int width, int height, Point p, double gdx, double gdy,
                                                     int searchWidth) {
        // where the box sits in the frame after the global shift
        double fx = p.Cx - gdx, fy = p.Cy - gdy;
        int bx = (int)Math.Round(fx), by = (int)Math.Round(fy);
        int hb = p.HalfBox;
        const int fine = 4;
        int coarse = Math.Max(1, (searchWidth - fine) / 2);       // in stride-2 steps

        // phase 1: stride-2 grid
        double best = double.NegativeInfinity; int bdx = 0, bdy = 0;
        for (int sy = -coarse; sy <= coarse; sy++)
            for (int sx = -coarse; sx <= coarse; sx++) {
                double c = Ncc(refLumBlurred, frameLumBlurred, width, height, p.Cx, p.Cy, bx + 2 * sx, by + 2 * sy, hb, 2)
                           * (1.0 - PenaltyFactor * (4.0 * sx * sx + 4.0 * sy * sy));
                if (c > best) { best = c; bdx = 2 * sx; bdy = 2 * sy; }
            }
        if (Math.Abs(bdx) >= 2 * coarse || Math.Abs(bdy) >= 2 * coarse) return null;   // on the border: no match
        if (best < MinCorrelation) return null;                                         // nothing like the reference here

        // phase 2: full resolution ±fine around the coarse optimum
        int cx0 = bx + bdx, cy0 = by + bdy;
        var grid = new double[2 * fine + 1, 2 * fine + 1];
        best = double.NegativeInfinity; int fdx = 0, fdy = 0;
        for (int sy = -fine; sy <= fine; sy++)
            for (int sx = -fine; sx <= fine; sx++) {
                double c = Ncc(refLumBlurred, frameLumBlurred, width, height, p.Cx, p.Cy, cx0 + sx, cy0 + sy, hb, 1);
                grid[sy + fine, sx + fine] = c;
                if (c > best) { best = c; fdx = sx; fdy = sy; }
            }
        if (Math.Abs(fdx) >= fine || Math.Abs(fdy) >= fine) return null;
        if (best < MinCorrelation) return null;

        // sub-pixel: quadratic surface through the 3×3 neighbourhood
        double subx = 0, suby = 0;
        {
            int i = fdy + fine, j = fdx + fine;
            double c00 = grid[i, j];
            double cxm = grid[i, j - 1], cxp = grid[i, j + 1], cym = grid[i - 1, j], cyp = grid[i + 1, j];
            double ax = (cxp + cxm - 2 * c00) / 2, bxx = (cxp - cxm) / 2;
            double ay = (cyp + cym - 2 * c00) / 2, byy = (cyp - cym) / 2;
            if (ax < 0) subx = -bxx / (2 * ax);
            if (ay < 0) suby = -byy / (2 * ay);
            if (Math.Abs(subx) > 1) subx = 0;
            if (Math.Abs(suby) > 1) suby = 0;
        }
        // the box content was found at frame position (cx0+fdx+subx, cy0+fdy+suby);
        // the shift that brings it onto the reference position (p.Cx, p.Cy):
        double dx = p.Cx - (cx0 + fdx + subx), dy = p.Cy - (cy0 + fdy + suby);
        return (dx, dy);
    }

    /// <summary>Normalised cross-correlation between the reference box centred
    /// on (rcx, rcy) and the frame box centred on (fcx, fcy), sampled every
    /// <paramref name="stride"/> pixels. -1 when a box leaves the image.</summary>
    public static double Ncc(float[] a, float[] b, int width, int height, int rcx, int rcy, int fcx, int fcy, int hb, int stride) {
        if (rcx - hb < 0 || rcy - hb < 0 || rcx + hb > width || rcy + hb > height) return -1;
        if (fcx - hb < 0 || fcy - hb < 0 || fcx + hb > width || fcy + hb > height) return -1;
        double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0; long n = 0;
        for (int y = -hb; y < hb; y += stride) {
            int ra = (rcy + y) * width + rcx, rb = (fcy + y) * width + fcx;
            for (int x = -hb; x < hb; x += stride) {
                double va = a[ra + x], vb = b[rb + x];
                sa += va; sb += vb; saa += va * va; sbb += vb * vb; sab += va * vb; n++;
            }
        }
        double ma = sa / n, mb = sb / n;
        double cov = sab / n - ma * mb, va2 = saa / n - ma * ma, vb2 = sbb / n - mb * mb;
        if (va2 <= 1e-9 || vb2 <= 1e-9) return -1;
        return cov / Math.Sqrt(va2 * vb2);
    }

    /// <summary>Linear-ramp weight of a patch at (x, y): 1 at the centre,
    /// falling to 1/(half+1) at the edge, as min(ramp_x, ramp_y). Overlapping
    /// patches on a half-box mesh then sum to a smooth field.</summary>
    public static float RampWeight(Point p, int x, int y) {
        int half = p.HalfBox;
        double wx = 1.0 - Math.Abs(x - p.Cx + 0.5) / (half + 1.0);
        double wy = 1.0 - Math.Abs(y - p.Cy + 0.5) / (half + 1.0);
        double w = Math.Min(wx, wy);
        return (float)Math.Max(0.0, w);
    }

    /// <summary>Adds the patch of <paramref name="plane"/> for AP <paramref name="p"/>,
    /// shifted by (dx, dy) sub-pixel (destination reads source at minus the
    /// shift), scaled by <paramref name="gain"/>, into the accumulators with
    /// ramp weights.</summary>
    public static void AccumulatePatch(float[] plane, int width, int height, Point p, double dx, double dy,
                                       float gain, float[] accum, float[] weight) {
        for (int y = p.Y0; y < p.Y1; y++) {
            double sy = y - dy; int y0 = (int)Math.Floor(sy); double fy = sy - y0;
            if (y0 < 0 || y0 + 1 >= height) continue;
            int r0 = y0 * width, r1 = r0 + width, dst = y * width;
            for (int x = p.X0; x < p.X1; x++) {
                double sx = x - dx; int x0 = (int)Math.Floor(sx); double fxr = sx - x0;
                if (x0 < 0 || x0 + 1 >= width) continue;
                double v = (1 - fy) * ((1 - fxr) * plane[r0 + x0] + fxr * plane[r0 + x0 + 1])
                         + fy * ((1 - fxr) * plane[r1 + x0] + fxr * plane[r1 + x0 + 1]);
                float w = RampWeight(p, x, y);
                accum[dst + x] += (float)v * gain * w;
                weight[dst + x] += w;
            }
        }
    }

    /// <summary>Merges the AP accumulation with the background reference:
    /// where the mesh covered a pixel with at least
    /// <paramref name="blendThreshold"/> × <paramref name="stackSize"/> weight
    /// the AP result is used in full; below that it fades into the reference.</summary>
    public static ushort[] Merge(float[] accum, float[] weight, float[] background, double stackSize, double blendThreshold) {
        var o = new ushort[accum.Length];
        double full = Math.Max(1e-6, blendThreshold * stackSize);
        for (int i = 0; i < o.Length; i++) {
            double fg = Math.Min(1.0, weight[i] / full);
            double ap = weight[i] > 0 ? accum[i] / weight[i] : 0;
            double v = fg * ap + (1 - fg) * background[i];
            o[i] = (ushort)Math.Clamp(Math.Round(v), 0, 65535);
        }
        return o;
    }
}
