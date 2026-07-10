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
//   NINA.WPF.Base/Utility/AutoFocus/QuadraticFitting.cs
// Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com>
// and the N.I.N.A. contributors. That source code is subject to the terms of
// the Mozilla Public License, v. 2.0 (http://mozilla.org/MPL/2.0/).

using System;
using System.Collections.Generic;

namespace NINA.Image.ImageAnalysis.AutoFocus;

/// <summary>Weighted (1/ErrorY²) parabola fit y = a2·x² + a1·x + a0 with the
/// vertex X rounded to a whole focuser step (desktop parity).</summary>
public sealed class QuadraticFitting {

    public double A2 { get; private set; }
    public double A1 { get; private set; }
    public double A0 { get; private set; }
    public (double X, double Y) Minimum { get; private set; }
    public double RSquared { get; private set; }
    /// <summary>False when no fit could be computed (degenerate input).</summary>
    public bool HasFit { get; private set; }

    public double Evaluate(double x) => A2 * x * x + A1 * x + A0;

    public QuadraticFitting Calculate(IReadOnlyList<FocusPoint> points) {
        var (a2, a1, a0, r2) = WeightedRegression.Poly2(points);
        if (a2 == 0 && a1 == 0 && a0 == 0) return this;   // degenerate

        A2 = a2; A1 = a1; A0 = a0;
        RSquared = r2;
        HasFit = true;
        int minimumX = (int)Math.Round(a1 / (2 * a2) * -1);
        Minimum = (minimumX, Evaluate(minimumX));
        return this;
    }
}
