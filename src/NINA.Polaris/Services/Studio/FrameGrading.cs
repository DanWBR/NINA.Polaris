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

namespace NINA.Polaris.Services.Studio;

/// <summary>
/// Pure subframe-grading math: turn per-frame quality metrics (star
/// count, median HFR, median eccentricity) into a ranked, interpretable
/// quality score and a keep/reject selection. Kept free of any FITS or
/// disk IO so it can be unit-tested directly with synthetic metrics;
/// <see cref="FrameGradingService"/> owns the pixel reading and star
/// detection and hands finished metrics here.
///
/// Score model (0..1, higher is better), combining the two things that
/// most affect a light's contribution to the stack:
///   - sharpness: bestHfr / hfr  (1.0 for the sharpest sub, &lt;1 worse)
///   - depth:     stars / maxStars (proxy for transparency; clouds and
///                haze crush the detectable star count)
/// weighted 0.65 sharpness / 0.35 depth. Frames with no detected stars or
/// a non-positive HFR (blank / cloudy / unreadable) score 0 and are never
/// kept.
///
/// Selection precedence:
///   1. keepBest N        -> the N highest-scoring valid frames.
///   2. hfrTolerancePct T -> every valid frame within T% of the best HFR.
///   3. default           -> within 20% of the best HFR AND at least half
///                           the median star count (drops the obvious
///                           cloudy / trailed subs, keeps the rest).
/// </summary>
public static class FrameGrading {
    public const double SharpnessWeight = 0.65;
    public const double DepthWeight = 0.35;
    public const double DefaultHfrTolerancePct = 20.0;
    public const double DefaultMinStarFraction = 0.5;

    /// <summary>One frame's measured quality, produced by the service.</summary>
    public record FrameMetric(
        string Path, string FileName, int Stars, double Hfr, double Eccentricity);

    /// <summary>A frame after ranking: its 0..1 quality score and whether
    /// the selection rule keeps it for integration.</summary>
    public record GradedFrame(
        string Path, string FileName, int Stars, double Hfr,
        double Eccentricity, double Score, bool Keep);

    private static bool IsValid(FrameMetric m) =>
        m.Stars > 0 && m.Hfr > 0 && !double.IsNaN(m.Hfr) && !double.IsInfinity(m.Hfr);

    /// <summary>
    /// Rank <paramref name="metrics"/> by quality (best first) and mark the
    /// keep set per the selection precedence above. A <paramref name="keepBest"/>
    /// of null/&lt;=0 means "no fixed count"; a null
    /// <paramref name="hfrTolerancePct"/> falls through to the default rule.
    /// </summary>
    public static IReadOnlyList<GradedFrame> Rank(
            IReadOnlyList<FrameMetric> metrics,
            int? keepBest = null,
            double? hfrTolerancePct = null) {
        if (metrics == null || metrics.Count == 0)
            return Array.Empty<GradedFrame>();

        var valid = metrics.Where(IsValid).ToList();

        // Score normalisers over the valid set.
        double bestHfr = valid.Count > 0 ? valid.Min(m => m.Hfr) : 0;
        int maxStars = valid.Count > 0 ? valid.Max(m => m.Stars) : 0;

        double Score(FrameMetric m) {
            if (!IsValid(m) || bestHfr <= 0 || maxStars <= 0) return 0;
            double sharp = bestHfr / m.Hfr;                 // (0,1]
            double depth = (double)m.Stars / maxStars;      // (0,1]
            double s = SharpnessWeight * sharp + DepthWeight * depth;
            return Math.Clamp(s, 0, 1);
        }

        // Rank first (score desc, then sharper, then more stars as tie-breaks).
        var ranked = metrics
            .Select(m => new { m, score = Score(m) })
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.m.Hfr)
            .ThenByDescending(x => x.m.Stars)
            .ToList();

        // Decide the keep set among the valid frames.
        var keepPaths = SelectKeepSet(valid, keepBest, hfrTolerancePct, bestHfr);

        return ranked.Select(x => new GradedFrame(
            x.m.Path, x.m.FileName, x.m.Stars, x.m.Hfr, x.m.Eccentricity,
            Math.Round(x.score, 4), keepPaths.Contains(x.m.Path))).ToList();
    }

    /// <summary>The subset of ranked frames marked keep, in ranked order —
    /// the paths to hand straight to batch integration.</summary>
    public static IReadOnlyList<string> Selected(IReadOnlyList<GradedFrame> ranked) =>
        ranked.Where(f => f.Keep).Select(f => f.Path).ToList();

    private static HashSet<string> SelectKeepSet(
            List<FrameMetric> valid, int? keepBest, double? hfrTolerancePct, double bestHfr) {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (valid.Count == 0) return keep;

        // 1. Fixed count of the best.
        if (keepBest is int n && n > 0) {
            foreach (var m in valid
                    .OrderBy(m => m.Hfr).ThenByDescending(m => m.Stars).Take(n))
                keep.Add(m.Path);
            return keep;
        }

        // 2. HFR tolerance band around the sharpest.
        if (hfrTolerancePct is double tol && tol >= 0) {
            double cutoff = bestHfr * (1.0 + tol / 100.0);
            foreach (var m in valid.Where(m => m.Hfr <= cutoff)) keep.Add(m.Path);
            return keep;
        }

        // 3. Default: within 20% of best HFR AND >= half the median star count.
        double defCutoff = bestHfr * (1.0 + DefaultHfrTolerancePct / 100.0);
        double medianStars = Median(valid.Select(m => (double)m.Stars));
        double minStars = medianStars * DefaultMinStarFraction;
        foreach (var m in valid.Where(m => m.Hfr <= defCutoff && m.Stars >= minStars))
            keep.Add(m.Path);
        return keep;
    }

    private static double Median(IEnumerable<double> values) {
        var s = values.OrderBy(v => v).ToList();
        if (s.Count == 0) return 0;
        int mid = s.Count / 2;
        return s.Count % 2 == 1 ? s[mid] : (s[mid - 1] + s[mid]) / 2.0;
    }
}
