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
// Ported from N.I.N.A. desktop (MPL-2.0):
//   NINA.WPF.Base/Utility/AutoFocus/Trendline.cs
//   NINA.WPF.Base/Utility/AutoFocus/TrendlineFitting.cs
// Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com>
// and the N.I.N.A. contributors. That source code is subject to the terms of
// the Mozilla Public License, v. 2.0 (http://mozilla.org/MPL/2.0/).

using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Image.ImageAnalysis.AutoFocus;

/// <summary>One arm of the V-curve: a weighted (1/ErrorY²) ordinary
/// least-squares line through the points on one side of the minimum.</summary>
public sealed class Trendline {

    public Trendline(IReadOnlyList<FocusPoint> points) {
        DataPoints = points;
        if (points.Count > 1) {
            var (slope, intercept, r2) = WeightedRegression.SimpleLinear(points);
            Slope = slope;
            Offset = intercept;
            RSquared = r2;
        }
    }

    public double Slope { get; }
    public double Offset { get; }
    public double RSquared { get; }
    public IReadOnlyList<FocusPoint> DataPoints { get; }

    public double GetY(double x) => Slope * x + Offset;

    /// <summary>Intersection with another trendline; the X is rounded to a
    /// whole focuser step (desktop parity). Parallel lines return (0, 0).</summary>
    public (double X, double Y) Intersect(Trendline line) {
        if (Slope == line.Slope) return (0, 0);
        var x = (line.Offset - Offset) / (Slope - line.Slope);
        var y = Slope * x + Offset;
        return ((int)Math.Round(x), y);
    }
}

/// <summary>
/// Splits the sweep into a left and right arm around the curve minimum and
/// fits a weighted trendline to each; their intersection estimates the focus
/// position. The arm-membership rule is also what drives the sweep planner:
/// a point belongs to an arm when it sits on that side of the minimum AND is
/// meaningfully above it (Y &gt; minimum + 0.1) — so soft-rejected zero
/// points and flat near-minimum scatter never count as arm coverage.
/// </summary>
public sealed class TrendlineFitting {

    public Trendline LeftTrend { get; private set; } = new(Array.Empty<FocusPoint>());
    public Trendline RightTrend { get; private set; } = new(Array.Empty<FocusPoint>());
    public (double X, double Y) Intersection { get; private set; }
    public FocusPoint Minimum { get; private set; }

    /// <summary>Star-HFR branch of the desktop TrendlineFitting.Calculate.
    /// The minimum is the point minimizing Y + ErrorY rather than plain Y, so
    /// a 0-HFR (no stars, ErrorY 1000) sample or a low-HFR/high-error fluke
    /// can never become the vertex.</summary>
    public TrendlineFitting Calculate(IReadOnlyList<FocusPoint> points) {
        if (points.Count == 0) return this;

        Minimum = points.Aggregate((l, r) => l.Y + l.ErrorY < r.Y + r.ErrorY ? l : r);
        var left = points.Where(p => p.X < Minimum.X && p.Y > Minimum.Y + 0.1).ToList();
        var right = points.Where(p => p.X > Minimum.X && p.Y > Minimum.Y + 0.1).ToList();
        LeftTrend = new Trendline(left);
        RightTrend = new Trendline(right);
        Intersection = LeftTrend.Intersect(RightTrend);
        return this;
    }
}
