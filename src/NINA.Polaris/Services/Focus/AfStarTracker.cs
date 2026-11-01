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
// Concept ported from N.I.N.A. desktop (MPL-2.0):
//   NINA.Image/ImageAnalysis/StarDetection.cs (BrightestStarPositions +
//   MatchStarPositions used when AutoFocusUseBrightestStars > 0).
// Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com>
// and the N.I.N.A. contributors. That source code is subject to the terms of
// the Mozilla Public License, v. 2.0 (http://mozilla.org/MPL/2.0/).

using NINA.Image.ImageAnalysis;

namespace NINA.Polaris.Services;

/// <summary>
/// Tracks the N brightest stars across an autofocus sweep so every point
/// measures the SAME physical stars. On the first frame that detects stars it
/// ranks them by flux and anchors the top-N positions; on every later frame
/// it picks, for each anchor, the detected star sitting at that position. This
/// removes the noise introduced by the detected-star population changing
/// between points (faint stars popping in/out as focus changes), which shifts
/// a plain mean/median HFR around. N &lt;= 0 disables tracking (all stars used).
/// The anchors are fixed for the whole run, reattempts included: a reference
/// star that is not found where it was anchored leaves its frame unmeasured,
/// and the tracker never goes looking for it elsewhere in the field.
/// </summary>
public sealed class AfStarTracker {
    /// <summary>How far, in detection-grid pixels, a star may sit from its
    /// anchor and still be taken for it: a little drift and seeing wobble
    /// between frames. A defocused donut gets its own HFR on top, since its
    /// centroid is less sharply defined.</summary>
    public const double MatchRadius = 15;

    private readonly int _n;
    private List<(double X, double Y)>? _anchors;

    public AfStarTracker(int brightestN) => _n = brightestN;

    /// <summary>The anchored positions, null until the first starry frame.</summary>
    public IReadOnlyList<(double X, double Y)>? Anchors => _anchors;

    /// <summary>Filter a frame's detections down to the tracked subset.
    /// Pass-through until anchors exist or when tracking is disabled. Empty
    /// when no anchor has a star at its position.</summary>
    public List<DetectedStar> Filter(List<DetectedStar> stars) {
        if (_n <= 0) return stars;
        if (stars.Count == 0) return stars;

        if (_anchors == null) {
            _anchors = stars.OrderByDescending(s => s.Flux)
                .Take(_n)
                .Select(s => (s.X, s.Y))
                .ToList();
            return stars.OrderByDescending(s => s.Flux).Take(_n).ToList();
        }

        // Nearest detected star per anchor, within reach of it. Distinct: two
        // anchors falling on the same star (heavy defocus merging neighbours)
        // count once.
        var picked = new List<DetectedStar>(_anchors.Count);
        foreach (var (ax, ay) in _anchors) {
            DetectedStar? best = null;
            double bestD2 = double.MaxValue;
            foreach (var s in stars) {
                double dx = s.X - ax, dy = s.Y - ay;
                double d2 = dx * dx + dy * dy;
                double reach = MatchRadius + Math.Max(0, s.HFR);
                if (d2 <= reach * reach && d2 < bestD2) { bestD2 = d2; best = s; }
            }
            if (best != null && !picked.Contains(best)) picked.Add(best);
        }
        return picked;
    }
}
