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

using System;
using System.Collections.Generic;

namespace NINA.Image.ImageAnalysis.AutoFocus;

/// <summary>
/// Weighted least-squares primitives shared by the autofocus fittings.
/// Replaces the Accord.NET regressions the N.I.N.A. desktop classes use
/// (OrdinaryLeastSquares / PolynomialLeastSquares with sample weights),
/// keeping the same semantics: minimize Σ wᵢ·(yᵢ − ŷᵢ)² with wᵢ = 1/ErrorYᵢ²,
/// and R² computed against the weighted mean.
/// </summary>
internal static class WeightedRegression {

    /// <summary>Weighted simple linear regression y = slope·x + intercept.
    /// Weights are 1/ErrorY². Returns (0, 0, 0) for fewer than 2 points or a
    /// degenerate (all same x) input, matching the desktop Trendline's
    /// "unset" state.</summary>
    public static (double slope, double intercept, double rSquared)
            SimpleLinear(IReadOnlyList<FocusPoint> points) {
        if (points.Count < 2) return (0, 0, 0);

        double sw = 0, swx = 0, swy = 0, swxx = 0, swxy = 0;
        foreach (var p in points) {
            double w = 1.0 / (p.ErrorY * p.ErrorY);
            sw += w;
            swx += w * p.X;
            swy += w * p.Y;
            swxx += w * p.X * p.X;
            swxy += w * p.X * p.Y;
        }

        double denom = sw * swxx - swx * swx;
        if (Math.Abs(denom) < 1e-12) return (0, 0, 0);

        double slope = (sw * swxy - swx * swy) / denom;
        double intercept = (swy - slope * swx) / sw;

        // Weighted coefficient of determination around the weighted mean —
        // same definition Accord's CoefficientOfDetermination(inputs,
        // outputs, weights) applies.
        double ybarW = swy / sw;
        double ssRes = 0, ssTot = 0;
        foreach (var p in points) {
            double w = 1.0 / (p.ErrorY * p.ErrorY);
            double pred = slope * p.X + intercept;
            ssRes += w * (p.Y - pred) * (p.Y - pred);
            ssTot += w * (p.Y - ybarW) * (p.Y - ybarW);
        }
        double r2 = ssTot <= 1e-12 ? 1.0 : 1.0 - ssRes / ssTot;
        return (slope, intercept, r2);
    }

    /// <summary>Weighted degree-2 polynomial fit y = a2·x² + a1·x + a0.
    /// Solved on x centered at the weighted mean for numerical conditioning
    /// (focuser positions reach 10⁵–10⁶ steps with tiny sweep spans; the raw
    /// {1, x, x²} basis is then nearly collinear and Cramer's rule cancels
    /// catastrophically — the same trap the old Polaris FitParabola hit),
    /// with coefficients mapped back to raw x.</summary>
    public static (double a2, double a1, double a0, double rSquared)
            Poly2(IReadOnlyList<FocusPoint> points) {
        if (points.Count < 3) return (0, 0, 0, 0);

        double sw = 0, swx = 0, swy = 0;
        foreach (var p in points) {
            double w = 1.0 / (p.ErrorY * p.ErrorY);
            sw += w;
            swx += w * p.X;
            swy += w * p.Y;
        }
        double xbar = swx / sw;

        // Weighted moments in u = x - x̄w.
        double s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0;
        double t0 = 0, t1 = 0, t2 = 0;
        foreach (var p in points) {
            double w = 1.0 / (p.ErrorY * p.ErrorY);
            double u = p.X - xbar;
            double u2 = u * u;
            s0 += w;
            s1 += w * u;
            s2 += w * u2;
            s3 += w * u2 * u;
            s4 += w * u2 * u2;
            t0 += w * p.Y;
            t1 += w * u * p.Y;
            t2 += w * u2 * p.Y;
        }

        // Normal equations in u:
        // | s0 s1 s2 | |c|   |t0|
        // | s1 s2 s3 |·|b| = |t1|
        // | s2 s3 s4 | |a|   |t2|
        double det = Det3(s0, s1, s2, s1, s2, s3, s2, s3, s4);
        if (Math.Abs(det) < 1e-12) return (0, 0, 0, 0);
        double cu = Det3(t0, s1, s2, t1, s2, s3, t2, s3, s4) / det;
        double bu = Det3(s0, t0, s2, s1, t1, s3, s2, t2, s4) / det;
        double au = Det3(s0, s1, t0, s1, s2, t1, s2, s3, t2) / det;

        // Map y = au·u² + bu·u + cu back to raw x (u = x − x̄):
        double a2 = au;
        double a1 = bu - 2 * au * xbar;
        double a0 = au * xbar * xbar - bu * xbar + cu;

        double ybarW = swy / sw;
        double ssRes = 0, ssTot = 0;
        foreach (var p in points) {
            double w = 1.0 / (p.ErrorY * p.ErrorY);
            double u = p.X - xbar;
            double pred = au * u * u + bu * u + cu;
            ssRes += w * (p.Y - pred) * (p.Y - pred);
            ssTot += w * (p.Y - ybarW) * (p.Y - ybarW);
        }
        double r2 = ssTot <= 1e-12 ? 1.0 : 1.0 - ssRes / ssTot;
        return (a2, a1, a0, r2);
    }

    private static double Det3(
            double m00, double m01, double m02,
            double m10, double m11, double m12,
            double m20, double m21, double m22) {
        return m00 * (m11 * m22 - m12 * m21)
             - m01 * (m10 * m22 - m12 * m20)
             + m02 * (m10 * m21 - m11 * m20);
    }
}
