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

using NUnit.Framework;
using NINA.Image.ImageAnalysis.AutoFocus;
using NINA.Polaris.Services;
using Planner = NINA.Polaris.Services.AutoFocusSweepPlanner;

namespace NINA.Polaris.Test;

[TestFixture]
public class AutoFocusSweepPlannerTests {

    private static List<FocusPoint> Pts(params (double x, double y)[] pairs) =>
        pairs.Select(p => new FocusPoint(p.x, p.y, 1)).ToList();

    private static (Planner.SweepAction action, int target) Step(
            List<FocusPoint> points, int offsetSteps = 3, int stepSize = 50, int maxPoints = 30) {
        var trend = new TrendlineFitting().Calculate(points);
        return Planner.NextStep(points, trend, offsetSteps, stepSize, maxPoints);
    }

    /// <summary>Simulated true V-curve (hyperbola-ish) around focus=1000.</summary>
    private static double TrueHfr(double pos) =>
        2 * Math.Cosh(Math.Asinh((1000 - pos) / 60.0));

    // ---- THE lopsided-curve regression test (the field report) ----
    // Start far to the RIGHT of true focus: the initial pass lands entirely
    // on the descending right flank. The planner must keep requesting LEFT
    // samples until the left arm actually exists, instead of declaring the
    // curve done (or growing the wrong side like the old vertex-count logic).
    [Test]
    public void LopsidedStart_RightFlankOnly_GrowsLeftUntilBothArmsExist() {
        const int offsetSteps = 3, stepSize = 50;
        // Initial out-then-in pass starting at 1300 (300 past focus):
        // samples at 1450, 1400, 1350, 1300 — all on the right arm.
        var points = Pts((1450, TrueHfr(1450)), (1400, TrueHfr(1400)),
                         (1350, TrueHfr(1350)), (1300, TrueHfr(1300)));

        int leftSamples = 0;
        for (int guard = 0; guard < 40; guard++) {
            var (action, target) = Step(points, offsetSteps, stepSize);
            if (action == Planner.SweepAction.Done) break;
            Assert.That(action, Is.AnyOf(Planner.SweepAction.SampleLeft, Planner.SweepAction.SampleRight),
                "planner must keep sampling, not fail");
            if (action == Planner.SweepAction.SampleLeft) {
                Assert.That(target, Is.EqualTo((int)points.Min(p => p.X) - stepSize));
                leftSamples++;
            }
            points.Add(new FocusPoint(target, TrueHfr(target), 1));
        }

        // Both arms must have reached the quota.
        var trend = new TrendlineFitting().Calculate(points);
        Assert.That(trend.LeftTrend.DataPoints.Count, Is.GreaterThanOrEqualTo(offsetSteps),
            "left arm was never built — the lopsided-curve bug");
        Assert.That(trend.RightTrend.DataPoints.Count, Is.GreaterThanOrEqualTo(offsetSteps));
        Assert.That(leftSamples, Is.GreaterThan(0));
        // And the sweep must have crossed to the left of true focus.
        Assert.That(points.Min(p => p.X), Is.LessThan(1000));
    }

    [Test]
    public void LopsidedStart_LeftFlankOnly_GrowsRight() {
        const int offsetSteps = 3, stepSize = 50;
        var points = Pts((550, TrueHfr(550)), (600, TrueHfr(600)),
                         (650, TrueHfr(650)), (700, TrueHfr(700)));

        for (int guard = 0; guard < 40; guard++) {
            var (action, target) = Step(points, offsetSteps, stepSize);
            if (action == Planner.SweepAction.Done) break;
            points.Add(new FocusPoint(target, TrueHfr(target), 1));
        }

        var trend = new TrendlineFitting().Calculate(points);
        Assert.That(trend.LeftTrend.DataPoints.Count, Is.GreaterThanOrEqualTo(offsetSteps));
        Assert.That(trend.RightTrend.DataPoints.Count, Is.GreaterThanOrEqualTo(offsetSteps));
        Assert.That(points.Max(p => p.X), Is.GreaterThan(1000));
    }

    [Test]
    public void SymmetricWellSpreadCurve_IsDoneImmediately() {
        var points = Pts(
            (850, TrueHfr(850)), (900, TrueHfr(900)), (950, TrueHfr(950)),
            (1000, TrueHfr(1000)),
            (1050, TrueHfr(1050)), (1100, TrueHfr(1100)), (1150, TrueHfr(1150)));

        var (action, _) = Step(points, offsetSteps: 3);
        Assert.That(action, Is.EqualTo(Planner.SweepAction.Done));
    }

    [Test]
    public void AllZeroPoints_FailNoTrend() {
        var points = Pts((900, 0), (950, 0), (1000, 0), (1050, 0));
        var (action, _) = Step(points);
        Assert.That(action, Is.EqualTo(Planner.SweepAction.FailNoTrend));
    }

    [Test]
    public void ZeroPoints_CountTowardArmQuota_SoCloudsTerminate() {
        // Left of the minimum: three no-star (0) samples; right arm complete.
        // The zero quota stops the planner from marching left forever.
        var points = Pts(
            (850, 0), (900, 0), (950, 0),
            (1000, TrueHfr(1000)),
            (1050, TrueHfr(1050)), (1100, TrueHfr(1100)), (1150, TrueHfr(1150)));
        foreach (var i in Enumerable.Range(0, 3)) {
            // give the zero points the soft-reject sigma
            points[i] = points[i] with { ErrorY = 1000 };
        }

        var (action, _) = Step(points, offsetSteps: 3);
        Assert.That(action, Is.EqualTo(Planner.SweepAction.Done));
    }

    [Test]
    public void PointCap_FailPointLimit() {
        var points = new List<FocusPoint>();
        for (int i = 0; i < 31; i++) points.Add(new FocusPoint(900 + i * 10, TrueHfr(900 + i * 10), 1));
        var (action, _) = Step(points, maxPoints: 30);
        Assert.That(action, Is.EqualTo(Planner.SweepAction.FailPointLimit));
    }

    [Test]
    public void MaxPointsFor_DesktopFormula() {
        Assert.That(Planner.MaxPointsFor(1, 4), Is.EqualTo(40));
        Assert.That(Planner.MaxPointsFor(2, 3), Is.EqualTo(60));
        Assert.That(Planner.MaxPointsFor(0, 0), Is.EqualTo(10)); // floors at 1
    }

    // ---- Low-wing outlier flagging (kept from FIELD2-1, now soft) ----

    private static List<AutoFocusPoint> AfPts(params (int pos, double hfr)[] pairs) =>
        pairs.Select(p => new AutoFocusPoint { Position = p.pos, HFR = p.hfr, StarCount = 50 }).ToList();

    [Test]
    public void MarkLowWingOutliers_DefocusDip_FlagsTheLowFarPoint() {
        // Right wing climbs 2→4→6→8 then DROPS to 2.5 (detector missed the
        // donut): the far sample must be flagged Rejected, not deleted.
        var pts = AfPts((100, 8), (150, 6), (200, 4), (250, 2), (300, 4), (350, 6), (400, 8), (450, 2.5));

        AutoFocusService.MarkLowWingOutliers(pts);

        Assert.That(pts.Single(p => p.Position == 450).Rejected, Is.True);
        Assert.That(pts.Count(p => p.Rejected), Is.EqualTo(1));
        Assert.That(pts.Count, Is.EqualTo(8), "points are flagged, never removed");
    }

    [Test]
    public void MarkLowWingOutliers_CleanVCurve_FlagsNothing() {
        var pts = AfPts((100, 8), (150, 6), (200, 4), (250, 2), (300, 4), (350, 6), (400, 8));
        AutoFocusService.MarkLowWingOutliers(pts);
        Assert.That(pts.Any(p => p.Rejected), Is.False);
    }
}
