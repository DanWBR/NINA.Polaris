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

namespace NINA.Polaris.Services.Focus;

/// <summary>
/// Bahtinov-mask focus analysis. A Bahtinov mask is a physical
/// diffraction grating placed over the telescope aperture that turns
/// a bright star's airy disk into 3 spikes (one central + two outer
/// forming a "V"). When focused, the central spike intersects the V
/// at the star's exact centre. Defocused, the central spike sits
/// off the V's intersection by an amount proportional to the focus
/// error.
///
/// Algorithm (radial line integration / coarse Hough):
/// 1. Pick the brightest star in the frame (or use a caller-
///    supplied <c>(starX, starY)</c> from a click).
/// 2. Crop a square ROI around it (200 px default).
/// 3. Background-subtract via the exact ROI median.
/// 4. Joint (θ, ρ) sweep: for each candidate angle θ ∈ [0°, 180°) at
///    0.5° steps, integrate intensity along lines at angle θ offset
///    ρ ∈ [-R, +R] px from the ROI centre, and keep the best ρ.
///    Result = 1-D signal of best line intensity vs angle. Searching
///    ρ together with θ matters: out of focus the spikes do not pass
///    through the star centre, and a line forced through the centre
///    misses a displaced spike and reports a wrong angle.
/// 5. Pick 3 local maxima in that signal that are ≥ 12° apart. They
///    are the 3 spike angles. Common masks put the outer spikes 15-20°
///    either side of the central one, so a larger gap rejects them.
/// 6. Refine ρ of each spike to sub-pixel with a parabola through the
///    best ρ and its neighbours. That gives 3 lines.
/// 7. Outer two lines intersect at some point near (but not at) the
///    star centre. The perpendicular distance from the central spike
///    line to that intersection is the focus error.
///
/// Sign convention: positive offset means the central spike is on the
/// side of the V's open mouth (need to focus IN), negative on the
/// closed side (need to focus OUT). UI surfaces the sign as a
/// directional arrow.
/// </summary>
public static class BahtinovAnalyzer {
    private const int DefaultRoiHalf = 100;           // ROI is 200x200 px
    private const double AngleStepDeg = 0.5;
    private const int RhoSearchRange = 20;            // ±20 px perpendicular (minimum)
    // Outer spikes of common masks sit 15-20° from the central one
    // (some designs use less), so the gap must stay well below that.
    private const double MinPeakSeparationDeg = 12.0;
    private const double InFocusThresholdPx = 0.5;    // |offset| below = "in focus"
    private const double SaturatedPeak = 60000;       // 16-bit upper guard

    public static BahtinovResult Analyze(ushort[] pixels, int width, int height,
                                          int? starX = null, int? starY = null,
                                          int roiHalf = DefaultRoiHalf) {
        if (pixels == null || pixels.Length != width * height) {
            return BahtinovResult.Fail("invalid pixel buffer");
        }

        // 1. Locate brightest star if no manual pick.
        int sx, sy;
        if (starX.HasValue && starY.HasValue) {
            sx = starX.Value; sy = starY.Value;
        } else {
            var detector = new StarDetector();
            var stars = detector.Detect(pixels, width, height);
            if (stars.Count == 0) {
                return BahtinovResult.Fail("no stars detected; point at a bright star with the Bahtinov mask installed");
            }
            DetectedStar? best = null;
            foreach (var s in stars) {
                if (s.Peak > SaturatedPeak) continue;   // skip saturated cores
                if (best == null || s.Peak > best.Peak) best = s;
            }
            if (best == null) {
                return BahtinovResult.Fail("only saturated stars detected; reduce exposure / gain");
            }
            sx = (int)Math.Round(best.X);
            sy = (int)Math.Round(best.Y);
        }

        // Clamp ROI so it stays inside the frame.
        if (sx - roiHalf < 0) sx = roiHalf;
        if (sy - roiHalf < 0) sy = roiHalf;
        if (sx + roiHalf >= width)  sx = width  - roiHalf - 1;
        if (sy + roiHalf >= height) sy = height - roiHalf - 1;
        if (sx - roiHalf < 0 || sy - roiHalf < 0) {
            return BahtinovResult.Fail("frame too small for ROI; use a larger sensor or smaller binning");
        }

        // 2-3. Crop ROI + background subtract.
        var roiSize = roiHalf * 2;
        var roi = ExtractRoi(pixels, width, height, sx - roiHalf, sy - roiHalf,
                              roiSize, roiSize);
        var bg = Median(roi);
        for (int i = 0; i < roi.Length; i++) {
            roi[i] = roi[i] > bg ? (ushort)(roi[i] - bg) : (ushort)0;
        }

        // 4. Joint (θ, ρ) sweep: for each θ, the brightest line at that
        // angle within ±rhoRange px of the ROI centre.
        var nAngles = (int)Math.Round(180.0 / AngleStepDeg);
        var angleSig = new double[nAngles];
        var angleRho = new double[nAngles];
        var cx = roiHalf;
        var cy = roiHalf;
        var halfLen = roiHalf - 2;    // stay 2px inside the ROI edge
        var rhoRange = Math.Max(RhoSearchRange, roiHalf / 5);
        var rhoProfile = new double[2 * rhoRange + 1];
        for (int ai = 0; ai < nAngles; ai++) {
            var theta = ai * AngleStepDeg * Math.PI / 180.0;
            int bestIdx = 0;
            for (int ri = 0; ri < rhoProfile.Length; ri++) {
                rhoProfile[ri] = LineMean(roi, roiSize, cx, cy, halfLen, theta, ri - rhoRange);
                if (rhoProfile[ri] > rhoProfile[bestIdx]) bestIdx = ri;
            }
            angleSig[ai] = rhoProfile[bestIdx];
            angleRho[ai] = bestIdx - rhoRange + ParabolicOffset(rhoProfile, bestIdx);
        }

        // 5. Find 3 local maxima >= MinPeakSeparationDeg apart.
        var peakAngles = FindTopPeaks(angleSig, AngleStepDeg, MinPeakSeparationDeg, 3);
        if (peakAngles.Count < 3) {
            return BahtinovResult.Fail($"could not detect 3 diffraction spikes (found {peakAngles.Count}); is the Bahtinov mask installed and the star bright enough?");
        }

        // 6. Each spike = (θ, sub-pixel ρ) from the sweep.
        var spikes = new List<BahtinovSpike>(3);
        foreach (var thetaDeg in peakAngles) {
            var ai = (int)Math.Round(thetaDeg / AngleStepDeg) % nAngles;
            spikes.Add(new BahtinovSpike(thetaDeg, angleRho[ai], angleSig[ai]));
        }

        // 7. Identify the central spike (the one whose angle is closest
        // to the mean of the other two). Compute the offset of its
        // line from the intersection of the outer two lines.
        spikes.Sort((a, b) => a.AngleDeg.CompareTo(b.AngleDeg));
        var s0 = spikes[0]; var s1 = spikes[1]; var s2 = spikes[2];
        // Identify which is centre by smallest angular deviation from
        // the bisector of the other two.
        var d01 = Math.Abs(s0.AngleDeg - (s1.AngleDeg + s2.AngleDeg) / 2.0);
        var d1m = Math.Abs(s1.AngleDeg - (s0.AngleDeg + s2.AngleDeg) / 2.0);
        var d2  = Math.Abs(s2.AngleDeg - (s0.AngleDeg + s1.AngleDeg) / 2.0);
        BahtinovSpike outerA, outerB, centre;
        if (d1m <= d01 && d1m <= d2) {
            centre = s1; outerA = s0; outerB = s2;
        } else if (d01 <= d2) {
            centre = s0; outerA = s1; outerB = s2;
        } else {
            centre = s2; outerA = s0; outerB = s1;
        }

        // Intersection of outer lines. Each line is parameterised as
        // (ρ, θ): point on line = ρ * normal(θ), direction = (cos θ,
        // sin θ). In ROI coordinates the lines pass through
        // (cx + ρ·nx, cy + ρ·ny) with direction (cos θ, sin θ).
        // Use the standard 2-line intersection formula.
        var ix = SolveIntersection(outerA, outerB, cx, cy);
        if (ix == null) {
            return BahtinovResult.Fail("outer spikes are parallel; cannot compute focus error");
        }
        // Perpendicular distance from centre line to the intersection.
        var thetaC = centre.AngleDeg * Math.PI / 180.0;
        var nxC = -Math.Sin(thetaC);
        var nyC =  Math.Cos(thetaC);
        // Distance from line to point: |((P - L0) . n)|, where L0 is a
        // point on the line and n is its unit normal. Here L0 =
        // (cx + ρ·nx, cy + ρ·ny), n = (nxC, nyC).
        var l0x = cx + centre.Rho * nxC;
        var l0y = cy + centre.Rho * nyC;
        var offset = (ix.Value.X - l0x) * nxC + (ix.Value.Y - l0y) * nyC;

        return new BahtinovResult {
            Ok = true,
            StarX = sx,
            StarY = sy,
            RoiHalf = roiHalf,
            Spike1Angle = spikes[0].AngleDeg,
            Spike1Rho   = spikes[0].Rho,
            Spike2Angle = spikes[1].AngleDeg,
            Spike2Rho   = spikes[1].Rho,
            Spike3Angle = spikes[2].AngleDeg,
            Spike3Rho   = spikes[2].Rho,
            CentreSpikeIndex = spikes.IndexOf(centre),
            OffsetPx = offset,
            InFocusThresholdPx = InFocusThresholdPx,
            IntersectionX = ix.Value.X + (sx - roiHalf),    // back to frame coords
            IntersectionY = ix.Value.Y + (sy - roiHalf)
        };
    }

    // ─── helpers ──────────────────────────────────────────────────

    private static ushort[] ExtractRoi(ushort[] src, int srcW, int srcH,
                                        int x0, int y0, int w, int h) {
        var roi = new ushort[w * h];
        for (int y = 0; y < h; y++) {
            int srcRow = (y0 + y) * srcW + x0;
            int dstRow = y * w;
            for (int x = 0; x < w; x++) {
                roi[dstRow + x] = src[srcRow + x];
            }
        }
        return roi;
    }

    // Exact median via a full 16-bit histogram, O(n). A coarse
    // histogram (256-count bins) returns 0 for cameras whose data use
    // only the low bits (e.g. 10/12-bit sensors stored as 16-bit, with
    // a background of ~60-100 ADU), so the background would never be
    // removed and the line sums would be dominated by the sky level.
    internal static ushort Median(ushort[] data) {
        if (data.Length == 0) return 0;
        var bins = new int[65536];
        foreach (var v in data) bins[v]++;
        int half = (data.Length + 1) / 2;
        int acc = 0;
        for (int i = 0; i < bins.Length; i++) {
            acc += bins[i];
            if (acc >= half) return (ushort)i;
        }
        return 0;
    }

    /// <summary>Mean ROI intensity along the line at angle
    /// <paramref name="theta"/> (radians), offset <paramref name="rho"/>
    /// px along its normal (-sin θ, cos θ) from (cx, cy).</summary>
    private static double LineMean(ushort[] roi, int roiSize, int cx, int cy,
                                   int halfLen, double theta, double rho) {
        var dx = Math.Cos(theta);
        var dy = Math.Sin(theta);
        var x0 = cx - rho * dy;
        var y0 = cy + rho * dx;
        double sum = 0;
        int n = 0;
        for (int t = -halfLen; t <= halfLen; t++) {
            var x = (int)Math.Round(x0 + t * dx);
            var y = (int)Math.Round(y0 + t * dy);
            if (x < 0 || x >= roiSize || y < 0 || y >= roiSize) continue;
            sum += roi[y * roiSize + x];
            n++;
        }
        return n > 0 ? sum / n : 0;
    }

    /// <summary>Sub-sample offset (-0.5..0.5) of the true maximum near
    /// index <paramref name="i"/>, from a parabola through i-1, i, i+1.
    /// 0 at the array ends or on a flat top.</summary>
    internal static double ParabolicOffset(double[] v, int i) {
        if (i <= 0 || i >= v.Length - 1) return 0;
        var denom = v[i - 1] - 2 * v[i] + v[i + 1];
        if (denom >= 0) return 0;
        var off = 0.5 * (v[i - 1] - v[i + 1]) / denom;
        return Math.Clamp(off, -0.5, 0.5);
    }

    /// <summary>Find up to <paramref name="topN"/> local maxima in
    /// <paramref name="signal"/> (circular, 180° = 0°; a point counts
    /// when it is not below either neighbour, so the shoulder of a
    /// strong peak is never picked) with each pair separated by at least
    /// <paramref name="minSepDeg"/> degrees. Returns the angles in
    /// degrees (using the index → degree mapping
    /// <c>angle = index * stepDeg</c>).</summary>
    internal static List<double> FindTopPeaks(double[] signal, double stepDeg,
                                               double minSepDeg, int topN) {
        var minSepIdx = (int)Math.Round(minSepDeg / stepDeg);
        // Pair (index, value) sorted by value desc, then walk taking
        // any peak that's far enough from the already-taken set.
        var indices = Enumerable.Range(0, signal.Length).ToArray();
        Array.Sort(indices, (a, b) => signal[b].CompareTo(signal[a]));
        var picked = new List<int>(topN);
        var len = signal.Length;
        foreach (var i in indices) {
            if (signal[i] < signal[(i + len - 1) % len] ||
                signal[i] < signal[(i + 1) % len]) continue;
            bool ok = true;
            foreach (var p in picked) {
                var d = Math.Abs(i - p);
                if (d > signal.Length / 2) d = signal.Length - d;   // wrap (180° = 0°)
                if (d < minSepIdx) { ok = false; break; }
            }
            if (ok) picked.Add(i);
            if (picked.Count >= topN) break;
        }
        return picked.Select(i => i * stepDeg).ToList();
    }

    /// <summary>Intersect two lines given as <see cref="BahtinovSpike"/>
    /// (angle θ + perpendicular offset ρ from the ROI centre).
    /// Returns the intersection point in ROI coordinates, or null when
    /// the lines are parallel (deltas of θ near 0° or 180°).</summary>
    internal static (double X, double Y)? SolveIntersection(BahtinovSpike a, BahtinovSpike b,
                                                              double cx, double cy) {
        var thA = a.AngleDeg * Math.PI / 180.0;
        var thB = b.AngleDeg * Math.PI / 180.0;
        var dxA = Math.Cos(thA); var dyA = Math.Sin(thA);
        var dxB = Math.Cos(thB); var dyB = Math.Sin(thB);
        var nxA = -dyA; var nyA = dxA;
        var nxB = -dyB; var nyB = dxB;
        // Point on each line
        var p1x = cx + a.Rho * nxA; var p1y = cy + a.Rho * nyA;
        var p2x = cx + b.Rho * nxB; var p2y = cy + b.Rho * nyB;
        // Solve p1 + s * dA = p2 + t * dB
        var denom = dxA * (-dyB) - dyA * (-dxB);
        if (Math.Abs(denom) < 1e-6) return null;
        var s = ((p2x - p1x) * (-dyB) - (p2y - p1y) * (-dxB)) / denom;
        return (p1x + s * dxA, p1y + s * dyA);
    }
}

public record BahtinovSpike(double AngleDeg, double Rho, double Intensity);

public sealed record BahtinovResult {
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public int StarX { get; init; }
    public int StarY { get; init; }
    public int RoiHalf { get; init; }
    public double Spike1Angle { get; init; }
    public double Spike1Rho { get; init; }
    public double Spike2Angle { get; init; }
    public double Spike2Rho { get; init; }
    public double Spike3Angle { get; init; }
    public double Spike3Rho { get; init; }
    public int CentreSpikeIndex { get; init; }
    public double OffsetPx { get; init; }
    public double InFocusThresholdPx { get; init; }
    public double IntersectionX { get; init; }
    public double IntersectionY { get; init; }

    public static BahtinovResult Fail(string error) => new() { Ok = false, Error = error };
}