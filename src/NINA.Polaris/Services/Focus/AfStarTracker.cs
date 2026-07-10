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
/// it picks, for each anchor, the nearest detected star. This removes the
/// noise introduced by the detected-star population changing between points
/// (faint stars popping in/out as focus changes), which shifts a plain
/// mean/median HFR around. N &lt;= 0 disables tracking (all stars used).
/// </summary>
public sealed class AfStarTracker {
    private readonly int _n;
    private List<(double X, double Y)>? _anchors;

    public AfStarTracker(int brightestN) => _n = brightestN;

    /// <summary>Forget the anchors (call at the start of each attempt — the
    /// sweep returns to the start position, so the field is framed the same
    /// but the reference frame should be re-picked fresh).</summary>
    public void Reset() => _anchors = null;

    /// <summary>Filter a frame's detections down to the tracked subset.
    /// Pass-through until anchors exist or when tracking is disabled.</summary>
    public List<DetectedStar> Filter(List<DetectedStar> stars) {
        if (_n <= 0 || stars.Count == 0) return stars;

        if (_anchors == null) {
            _anchors = stars.OrderByDescending(s => s.Flux)
                .Take(_n)
                .Select(s => (s.X, s.Y))
                .ToList();
            return stars.OrderByDescending(s => s.Flux).Take(_n).ToList();
        }

        // Nearest detected star per anchor (desktop MatchStarPositions has no
        // radius cap either — a defocused donut drifts a little but stays the
        // closest blob to its anchor). Distinct: two anchors falling on the
        // same nearest star (heavy defocus merging neighbours) count once.
        var picked = new List<DetectedStar>(_anchors.Count);
        foreach (var (ax, ay) in _anchors) {
            DetectedStar? best = null;
            double bestD2 = double.MaxValue;
            foreach (var s in stars) {
                double dx = s.X - ax, dy = s.Y - ay;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestD2) { bestD2 = d2; best = s; }
            }
            if (best != null && !picked.Contains(best)) picked.Add(best);
        }
        return picked.Count > 0 ? picked : stars;
    }
}
