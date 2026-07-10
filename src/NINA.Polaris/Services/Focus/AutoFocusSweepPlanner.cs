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
//
// Extension semantics ported from N.I.N.A. desktop (MPL-2.0):
//   NINA.WPF.Base/ViewModel/AutoFocus/AutoFocusVM.cs (StartAutoFocus, the
//   "when datapoints are not sufficient analyze and take more" loop).
// Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com>
// and the N.I.N.A. contributors. That source code is subject to the terms of
// the Mozilla Public License, v. 2.0 (http://mozilla.org/MPL/2.0/).

using NINA.Image.ImageAnalysis.AutoFocus;

namespace NINA.Polaris.Services;

/// <summary>
/// Pure decision logic for extending the autofocus sweep. After the initial
/// out-then-in pass, the sweep keeps adding ONE point at a time on whichever
/// side of the V-curve minimum still has fewer than <c>offsetSteps</c>
/// trendline-arm points. Arm membership comes from
/// <see cref="TrendlineFitting"/> (points meaningfully ABOVE the minimum on
/// that side), NOT from counting points beyond a fitted vertex — that is what
/// fixes the "curve built only to one side" field report: a start far from
/// focus keeps yielding SampleLeft/SampleRight for the empty arm until the
/// curve actually has two usable slopes.
///
/// Soft-rejected points (Y == 0, no stars) count toward the quota on their
/// side so a clouded-out run terminates instead of marching forever.
/// </summary>
public static class AutoFocusSweepPlanner {

    public enum SweepAction {
        /// <summary>Sample one more point at min(X) − stepSize.</summary>
        SampleLeft,
        /// <summary>Sample one more point at max(X) + stepSize.</summary>
        SampleRight,
        /// <summary>Both arms have enough points; proceed to the final fit.</summary>
        Done,
        /// <summary>No trend points on either side — the sweep has no usable
        /// slope at all (all-cloud / hopeless). Desktop parity: restore and
        /// give up WITHOUT reattempting.</summary>
        FailNoTrend,
        /// <summary>The absolute point cap was hit (zig-zag guard).</summary>
        FailPointLimit
    }

    /// <summary>Desktop cap formula: framesPerPoint * offsetSteps * 10.</summary>
    public static int MaxPointsFor(int framesPerPoint, int offsetSteps) =>
        Math.Max(1, framesPerPoint) * Math.Max(1, offsetSteps) * 10;

    /// <summary>
    /// Decide the next sweep step. <paramref name="points"/> is every sampled
    /// point (including soft-rejected Y==0 ones); <paramref name="trend"/> is
    /// the trendline fitting computed over those same points.
    /// </summary>
    public static (SweepAction action, int target) NextStep(
            IReadOnlyList<FocusPoint> points,
            TrendlineFitting trend,
            int offsetSteps,
            int stepSize,
            int maxPoints) {
        if (points.Count == 0) return (SweepAction.FailNoTrend, 0);
        if (points.Count > maxPoints) return (SweepAction.FailPointLimit, 0);

        int leftCount = trend.LeftTrend.DataPoints.Count;
        int rightCount = trend.RightTrend.DataPoints.Count;

        // Not a single usable slope point anywhere: reattempting is very
        // likely meaningless (desktop parity).
        if (leftCount == 0 && rightCount == 0) return (SweepAction.FailNoTrend, 0);

        double minX = points.Min(p => p.X);
        double maxX = points.Max(p => p.X);

        // Zero-measure (soft-rejected) points count toward each side's quota,
        // capping how far the sweep chases a side that yields no stars.
        int leftZero = points.Count(p => p.X < trend.Minimum.X && p.Y == 0);
        int rightZero = points.Count(p => p.X > trend.Minimum.X && p.Y == 0);

        // Termination is the desktop while-condition: each side needs
        // trend + zero points >= offsetSteps.
        if (leftCount + leftZero >= offsetSteps && rightCount + rightZero >= offsetSteps) {
            return (SweepAction.Done, 0);
        }

        // Fill the left arm first (moving further in), then the right
        // (moving back out) — mirrors the desktop's one-at-a-time order.
        if (leftCount < offsetSteps && leftZero < offsetSteps) {
            return (SweepAction.SampleLeft, (int)Math.Round(minX) - stepSize);
        }
        if (rightCount < offsetSteps && rightZero < offsetSteps) {
            return (SweepAction.SampleRight, (int)Math.Round(maxX) + stepSize);
        }
        // Neither guard fires but a side's sum quota is unmet (all-zero side):
        // nothing productive left to sample. Treat as done — the fit +
        // R² gate downstream decides whether the curve is usable.
        return (SweepAction.Done, 0);
    }
}
