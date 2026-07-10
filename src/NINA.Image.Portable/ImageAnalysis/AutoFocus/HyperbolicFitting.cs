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
//   NINA.WPF.Base/Utility/AutoFocus/HyperbolicFitting.cs
// Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com>
// and the N.I.N.A. contributors. That source code is subject to the terms of
// the Mozilla Public License, v. 2.0 (http://mozilla.org/MPL/2.0/).

using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Image.ImageAnalysis.AutoFocus;

/// <summary>
/// Hyperbolic V-curve fit. The HFR of an imaged star disk as a function of
/// focuser position is a hyperbola, not a parabola:
///     y = a · cosh(asinh((p − x) / b))
/// where p is the perfect-focus position, a the HFR at focus, and b defines
/// the asymptote slopes (y → ±x·a/b). Fitted with the desktop's shrinking
/// grid search: start from data-derived guesses, halve the (a, b, p) search
/// ranges each cycle with 20 sub-steps per dimension, minimizing the
/// error-weighted RMS of residuals; stop when the improvement falls below
/// 1e-4, the error itself does, or after 30 cycles.
/// </summary>
public sealed class HyperbolicFitting {

    public double A { get; private set; }
    public double B { get; private set; }
    public double P { get; private set; }
    public (double X, double Y) Minimum { get; private set; }
    public double RSquared { get; private set; }
    /// <summary>False when no fit could be computed (all-zero or degenerate
    /// input — the desktop "BadData" guards).</summary>
    public bool HasFit { get; private set; }

    public double Evaluate(double x) => A * Math.Cosh(Math.Asinh((P - x) / B));

    public HyperbolicFitting Calculate(IReadOnlyList<FocusPoint> points) {
        double lowestError = double.MaxValue;

        var nonZeroPoints = points.Where(dp => dp.Y >= 0.1).ToList();
        if (nonZeroPoints.Count == 0) {
            // No non-zero points in curve. No fit can be calculated.
            return this;
        }
        int n = nonZeroPoints.Count;

        var lowestPoint = nonZeroPoints.Aggregate((l, r) => l.Y < r.Y ? l : r);
        var highestPoint = nonZeroPoints.Aggregate((l, r) => l.Y > r.Y ? l : r);
        double highestPosition = highestPoint.X;
        double highestHfr = highestPoint.Y;
        double lowestPosition = lowestPoint.X;
        double lowestHfr = lowestPoint.Y;
        double oldError = double.MaxValue;

        if (highestPosition < lowestPosition) {
            highestPosition = 2 * lowestPosition - highestPosition; // Always go up
        }

        // Good starting values for a, b and p. Alternative hyperbola formula:
        // y²/a² − x²/b² = 1  ==>  b² = x²·a²/(y² − a²)
        double a = lowestHfr;
        double b = Math.Sqrt((highestPosition - lowestPosition) * (highestPosition - lowestPosition)
                             * a * a / (highestHfr * highestHfr - a * a));
        double p = lowestPosition;

        int iterationCycles = 0;

        // Starting test ranges; p gets large steps since the slope may carry error.
        double aRange = a;
        double bRange = b;
        double pRange = highestPosition - lowestPosition;

        if (double.IsNaN(aRange) || double.IsNaN(bRange) || aRange == 0 || bRange == 0 || pRange == 0) {
            // Not enough valid data points to fit a curve.
            return this;
        }

        do {
            double p0 = p, b0 = b, a0 = a;

            // Reduce range by 50%
            aRange *= 0.5;
            bRange *= 0.5;
            pRange *= 0.5;

            double p1 = p0 - pRange;
            while (p1 <= p0 + pRange) {           // position loop
                double a1 = a0 - aRange;
                while (a1 <= a0 + aRange) {       // a loop
                    double b1 = b0 - bRange;
                    while (b1 <= b0 + bRange) {   // b loop
                        double error1 = ScaledErrorHyperbola(nonZeroPoints, p1, a1, b1);
                        if (error1 < lowestError) {
                            oldError = lowestError;
                            lowestError = error1;
                            a = a1; b = b1; p = p1;
                        }
                        b1 += bRange * 0.1;       // 20 steps within the range
                    }
                    a1 += aRange * 0.1;
                }
                p1 += pRange * 0.1;
            }
            iterationCycles++;
        } while (oldError - lowestError >= 0.0001 && lowestError > 0.0001 && iterationCycles < 30);

        A = a; B = b; P = p;
        HasFit = true;
        Minimum = ((int)Math.Round(p), a);

        // R² over the non-zero points. Deliberately UNWEIGHTED — the desktop
        // implementation has the weights commented out, and the ported
        // reference tests pin the unweighted values.
        double meanY = nonZeroPoints.Average(dp => dp.Y);
        double ssRes = 0, ssTot = 0;
        for (int i = 0; i < n; i++) {
            double predicted = Evaluate(nonZeroPoints[i].X);
            ssRes += (nonZeroPoints[i].Y - predicted) * (nonZeroPoints[i].Y - predicted);
            ssTot += (nonZeroPoints[i].Y - meanY) * (nonZeroPoints[i].Y - meanY);
        }
        RSquared = ssTot <= 0 ? 0 : 1 - ssRes / ssTot;

        return this;
    }

    private static double ScaledErrorHyperbola(IReadOnlyList<FocusPoint> points,
            double perfectFocusPosition, double a, double b) {
        double sum = 0;
        foreach (var dp in points) {
            double model = a * Math.Cosh(Math.Asinh((perfectFocusPosition - dp.X) / b));
            double e = (model - dp.Y) / dp.ErrorY;
            sum += e * e;
        }
        return Math.Sqrt(sum);
    }
}
