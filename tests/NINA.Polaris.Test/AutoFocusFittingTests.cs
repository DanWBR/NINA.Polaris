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
// Reference values ported from N.I.N.A. desktop (MPL-2.0) test suite:
//   NINA.Test/Autofocus/HyperbolicFittingTest.cs
//   NINA.Test/Autofocus/QuadraticFittingTest.cs
//   NINA.Test/Autofocus/TrendlineFittingTest.cs

using NUnit.Framework;
using NINA.Image.ImageAnalysis.AutoFocus;

namespace NINA.Polaris.Test;

[TestFixture]
public class AutoFocusFittingTests {
    private const double Tolerance = 1e-12;

    private static List<FocusPoint> Pts(params (double x, double y)[] pairs) =>
        pairs.Select(p => new FocusPoint(p.x, p.y, 1)).ToList();

    // ---- Hyperbolic (desktop HyperbolicFittingTest) ----

    [Test]
    public void Hyperbolic_PerfectVCurve_FindsMinimum() {
        var points = Pts((1, 18), (2, 11), (3, 6), (4, 3), (5, 2), (6, 3), (7, 6), (8, 11), (9, 18));

        var sut = new HyperbolicFitting().Calculate(points);

        Assert.That(sut.HasFit, Is.True);
        Assert.That(sut.Minimum.X, Is.EqualTo(5).Within(Tolerance));
        Assert.That(sut.Minimum.Y, Is.EqualTo(1.2).Within(Tolerance));
        Assert.That(sut.Evaluate(sut.Minimum.X), Is.EqualTo(sut.Minimum.Y));
    }

    [Test]
    public void Hyperbolic_BadData_PreventInfiniteLoop() {
        // A single non-zero point: no curve can be fitted; must return an
        // empty fit instead of hanging in the grid search.
        var points = Pts((1000, 18), (1100, 0), (1200, 0));

        var sut = new HyperbolicFitting().Calculate(points);

        Assert.That(sut.HasFit, Is.False);
        Assert.That(sut.Minimum.X, Is.EqualTo(0));
        Assert.That(sut.Minimum.Y, Is.EqualTo(0));
    }

    [Test]
    public void Hyperbolic_BadData2_PreventInfiniteLoop() {
        var points = Pts((1000, 18), (1000, 18), (1000, 18), (1100, 0), (1200, 0));
        var sut = new HyperbolicFitting().Calculate(points);
        Assert.That(sut.HasFit, Is.False);
    }

    [Test]
    public void Hyperbolic_BadData3_PreventInfiniteLoop() {
        var points = Pts((900, 18), (1000, 18), (1000, 18), (1100, 0), (1200, 0));
        var sut = new HyperbolicFitting().Calculate(points);
        Assert.That(sut.HasFit, Is.False);
    }

    [Test]
    public void Hyperbolic_BadData4_PreventInfiniteLoop() {
        var points = Pts((800, 18), (900, 0), (1000, 0), (1000, 18), (1000, 18), (1100, 0), (1200, 0));
        var sut = new HyperbolicFitting().Calculate(points);
        Assert.That(sut.HasFit, Is.False);
    }

    [Test]
    public void Hyperbolic_AllZero_ReturnsEmptyFit() {
        var points = Pts((1, 0), (2, 0), (3, 0));
        var sut = new HyperbolicFitting().Calculate(points);
        Assert.That(sut.HasFit, Is.False);
    }

    // ---- Quadratic (desktop QuadraticFittingTest) ----

    [Test]
    public void Quadratic_PerfectVCurve_FindsMinimum() {
        // (x-5)² + 2
        var points = Pts((1, 18), (2, 11), (3, 6), (4, 3), (5, 2), (6, 3), (7, 6), (8, 11), (9, 18));

        var sut = new QuadraticFitting().Calculate(points);

        Assert.That(sut.HasFit, Is.True);
        Assert.That(sut.Minimum.X, Is.EqualTo(5).Within(Tolerance));
        Assert.That(sut.Minimum.Y, Is.EqualTo(2).Within(1e-9));
        Assert.That(sut.RSquared, Is.EqualTo(1).Within(1e-9));
    }

    [Test]
    public void Quadratic_HugePositionOffset_StaysWellConditioned() {
        // (x-500000)²/2500 + 1.5 sampled at ±150 — the centered normal
        // equations must keep the vertex accurate despite x ~ 5e5 (the raw
        // {1,x,x²} basis cancels catastrophically without centering).
        var pts = new List<FocusPoint>();
        for (int i = -3; i <= 3; i++) {
            double x = 500000 + i * 50;
            double y = (x - 500000) * (x - 500000) / 2500.0 + 1.5;
            pts.Add(new FocusPoint(x, y, 1));
        }

        var sut = new QuadraticFitting().Calculate(pts);

        Assert.That(sut.HasFit, Is.True);
        Assert.That(sut.Minimum.X, Is.EqualTo(500000).Within(1));
        Assert.That(sut.RSquared, Is.EqualTo(1).Within(1e-6));
    }

    [Test]
    public void Quadratic_WeightedFit_IgnoresSoftRejectedPoint() {
        // A soft-rejected sample (y=0, σ=1000) must not drag the vertex: the
        // 1/σ² weight makes it ~1e-6 of a normal point.
        var pts = Pts((1, 18), (2, 11), (3, 6), (4, 3), (5, 2), (6, 3), (7, 6), (8, 11), (9, 18));
        pts.Add(new FocusPoint(10, 0, 1000));

        var sut = new QuadraticFitting().Calculate(pts);

        Assert.That(sut.Minimum.X, Is.EqualTo(5).Within(0.01));
    }

    // ---- Trendlines (desktop TrendlineFittingTest) ----

    [Test]
    public void Trendlines_PerfectVCurve_IntersectionAtMinimum() {
        var points = Pts(
            (5, 2),
            (1, 10), (2, 8), (3, 6), (4, 4),
            (9, 10), (8, 8), (7, 6), (6, 4));

        var sut = new TrendlineFitting().Calculate(points);

        Assert.That(sut.Intersection.X, Is.EqualTo(5).Within(Tolerance));
        Assert.That(sut.Intersection.Y, Is.EqualTo(2).Within(Tolerance));
        Assert.That(sut.LeftTrend.DataPoints.Count, Is.EqualTo(4));
        Assert.That(sut.RightTrend.DataPoints.Count, Is.EqualTo(4));
    }

    [Test]
    public void Trendlines_FlatTipWithMultiplePoints_ExcludesNearMinimumScatter() {
        // The flat tip points (2.1 within +0.1 of the 2.0 minimum) must NOT
        // join the arms — arm membership requires Y > minimum + 0.1.
        var points = Pts(
            (5, 2.1), (6, 2), (7, 2.1),
            (1, 10), (2, 8), (3, 6), (4, 4),
            (11, 10), (10, 8), (9, 6), (8, 4));

        var sut = new TrendlineFitting().Calculate(points);

        Assert.That(sut.Intersection.X, Is.EqualTo(6).Within(Tolerance));
        Assert.That(sut.Intersection.Y, Is.EqualTo(0).Within(Tolerance));
        Assert.That(sut.LeftTrend.DataPoints.Count, Is.EqualTo(4));
        Assert.That(sut.RightTrend.DataPoints.Count, Is.EqualTo(4));
    }

    [Test]
    public void Trendlines_ZeroErrorPoint_NeverBecomesTheMinimum() {
        // A no-stars sample (y=0, σ=1000) minimizes Y but NOT Y+ErrorY, so
        // the vertex must stay on the real curve minimum.
        var points = Pts((1, 10), (2, 8), (3, 6), (4, 4), (5, 2), (6, 4), (7, 6), (8, 8), (9, 10));
        points.Add(new FocusPoint(2.5, 0, 1000));

        var sut = new TrendlineFitting().Calculate(points);

        Assert.That(sut.Minimum.X, Is.EqualTo(5));
        Assert.That(sut.Minimum.Y, Is.EqualTo(2));
    }

    // ---- Orchestrator ----

    [Test]
    public void Fitting_TrendHyperbolic_AveragesIntersectionAndVertex() {
        var points = Pts((1, 18), (2, 11), (3, 6), (4, 3), (5, 2), (6, 3), (7, 6), (8, 11), (9, 18));

        var f = AutoFocusFitting.Calculate(points, AFCurveFittingMethod.TrendHyperbolic);

        var expectedX = Math.Round((f.Trendlines.Intersection.X + f.Hyperbolic.Minimum.X) / 2);
        Assert.That(f.FinalFocusPoint.X, Is.EqualTo(expectedX));
        // On this symmetric curve both estimates agree on x=5.
        Assert.That(f.FinalFocusPoint.X, Is.EqualTo(5).Within(0.6));
    }

    [Test]
    public void Fitting_Validate_FailsOnBadTrendArm() {
        // Right arm is pure noise → its R² tanks → TREND* methods must fail.
        var points = Pts(
            (1, 10), (2, 8), (3, 6), (4, 4), (5, 2),
            (6, 9), (7, 3), (8, 12), (9, 4));

        var f = AutoFocusFitting.Calculate(points, AFCurveFittingMethod.TrendHyperbolic);
        var reason = f.Validate(0.9);

        Assert.That(reason, Is.Not.Null);
    }

    [Test]
    public void Fitting_Validate_ZeroThreshold_AlwaysPasses() {
        var points = Pts((6, 9), (7, 3), (8, 12));
        var f = AutoFocusFitting.Calculate(points, AFCurveFittingMethod.TrendHyperbolic);
        Assert.That(f.Validate(0), Is.Null);
    }

    [Test]
    public void ParseMethod_RoundTrips_AndDefaults() {
        Assert.That(AutoFocusFitting.ParseMethod("hyperbolic"), Is.EqualTo(AFCurveFittingMethod.Hyperbolic));
        Assert.That(AutoFocusFitting.ParseMethod("TRENDPARABOLIC"), Is.EqualTo(AFCurveFittingMethod.TrendParabolic));
        Assert.That(AutoFocusFitting.ParseMethod("bogus"), Is.EqualTo(AFCurveFittingMethod.TrendHyperbolic));
        Assert.That(AutoFocusFitting.ParseMethod(null), Is.EqualTo(AFCurveFittingMethod.TrendHyperbolic));
    }
}
