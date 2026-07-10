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
// Method dispatch + validation ported from N.I.N.A. desktop (MPL-2.0):
//   NINA.WPF.Base/ViewModel/AutoFocus/AutoFocusVM.cs
//   (DetermineFinalFocusPoint + ValidateCalculatedFocusPosition)
// Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com>
// and the N.I.N.A. contributors. That source code is subject to the terms of
// the Mozilla Public License, v. 2.0 (http://mozilla.org/MPL/2.0/).

using System;
using System.Collections.Generic;

namespace NINA.Image.ImageAnalysis.AutoFocus;

/// <summary>Curve-fitting method for star-HFR autofocus (desktop
/// AFCurveFittingEnum). Trend+X methods average the trendline intersection X
/// with the model vertex X, cross-validating the two estimates.</summary>
public enum AFCurveFittingMethod {
    Trendlines,
    Parabolic,
    TrendParabolic,
    Hyperbolic,
    TrendHyperbolic
}

/// <summary>
/// Computes every fitting over a sweep's focus points and derives the final
/// focus point for the selected method. Trendlines are always computed (the
/// sweep planner needs the arm memberships anyway); the parabola is always
/// computed too (it is cheap and feeds the legacy FitA/B/C wire fields); the
/// hyperbola is computed for 3+ points.
/// </summary>
public sealed class AutoFocusFitting {

    public AFCurveFittingMethod Method { get; private set; }
    public TrendlineFitting Trendlines { get; private set; } = new();
    public QuadraticFitting Quadratic { get; private set; } = new();
    public HyperbolicFitting Hyperbolic { get; private set; } = new();
    public (double X, double Y) FinalFocusPoint { get; private set; }

    public static AutoFocusFitting Calculate(IReadOnlyList<FocusPoint> points,
            AFCurveFittingMethod method) {
        var f = new AutoFocusFitting { Method = method };
        f.Trendlines = new TrendlineFitting().Calculate(points);
        if (points.Count >= 3) {
            f.Quadratic = new QuadraticFitting().Calculate(points);
            f.Hyperbolic = new HyperbolicFitting().Calculate(points);
        }
        f.FinalFocusPoint = method switch {
            AFCurveFittingMethod.Trendlines => f.Trendlines.Intersection,
            AFCurveFittingMethod.Parabolic => f.Quadratic.Minimum,
            AFCurveFittingMethod.Hyperbolic => f.Hyperbolic.Minimum,
            AFCurveFittingMethod.TrendParabolic => (
                Math.Round((f.Trendlines.Intersection.X + f.Quadratic.Minimum.X) / 2),
                (f.Trendlines.Intersection.Y + f.Quadratic.Minimum.Y) / 2),
            AFCurveFittingMethod.TrendHyperbolic => (
                Math.Round((f.Trendlines.Intersection.X + f.Hyperbolic.Minimum.X) / 2),
                (f.Trendlines.Intersection.Y + f.Hyperbolic.Minimum.Y) / 2),
            _ => (0, 0)
        };
        return f;
    }

    /// <summary>R² validation per method (desktop
    /// ValidateCalculatedFocusPosition). Returns null when the curve passes,
    /// otherwise a human-readable failure reason. A threshold of 0 disables
    /// the gate entirely.</summary>
    public string? Validate(double rSquaredThreshold) {
        if (rSquaredThreshold <= 0) return null;

        bool hyperbolicBad = Hyperbolic.RSquared < rSquaredThreshold;
        bool quadraticBad = Quadratic.RSquared < rSquaredThreshold;
        bool trendlineBad = Trendlines.LeftTrend.RSquared < rSquaredThreshold
                         || Trendlines.RightTrend.RSquared < rSquaredThreshold;

        if ((Method == AFCurveFittingMethod.Hyperbolic || Method == AFCurveFittingMethod.TrendHyperbolic)
                && hyperbolicBad) {
            return $"hyperbolic fit R²={Hyperbolic.RSquared:F2} < {rSquaredThreshold:F2}";
        }
        if ((Method == AFCurveFittingMethod.Parabolic || Method == AFCurveFittingMethod.TrendParabolic)
                && quadraticBad) {
            return $"parabolic fit R²={Quadratic.RSquared:F2} < {rSquaredThreshold:F2}";
        }
        if ((Method == AFCurveFittingMethod.Trendlines || Method == AFCurveFittingMethod.TrendHyperbolic
                || Method == AFCurveFittingMethod.TrendParabolic) && trendlineBad) {
            return $"trendline R² left={Trendlines.LeftTrend.RSquared:F2} "
                 + $"right={Trendlines.RightTrend.RSquared:F2} < {rSquaredThreshold:F2}";
        }
        return null;
    }

    /// <summary>Parse a method name from the wire/profile ("TRENDHYPERBOLIC",
    /// "hyperbolic", …). Unknown or empty falls back to TrendHyperbolic, the
    /// Polaris default.</summary>
    public static AFCurveFittingMethod ParseMethod(string? name) {
        return (name ?? "").Trim().ToUpperInvariant() switch {
            "TRENDLINES" => AFCurveFittingMethod.Trendlines,
            "PARABOLIC" => AFCurveFittingMethod.Parabolic,
            "TRENDPARABOLIC" => AFCurveFittingMethod.TrendParabolic,
            "HYPERBOLIC" => AFCurveFittingMethod.Hyperbolic,
            "TRENDHYPERBOLIC" => AFCurveFittingMethod.TrendHyperbolic,
            _ => AFCurveFittingMethod.TrendHyperbolic
        };
    }
}
