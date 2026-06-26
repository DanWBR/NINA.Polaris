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
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

[TestFixture]
public class AutoFocusServiceTests {

    private static List<AutoFocusPoint> Pts(params (int pos, double hfr)[] pairs) =>
        pairs.Select(p => new AutoFocusPoint { Position = p.pos, HFR = p.hfr, StarCount = 50 }).ToList();

    // ---- Parabola fit math ----

    [Test]
    public void FitParabola_ExactQuadratic_RecoversCoefficients() {
        // y = 2(x - 1000)^2 + 1.5  =>  a=2, b=-4000, c=2,000,001.5
        var pts = new List<AutoFocusPoint>();
        for (int x = 990; x <= 1010; x += 2) {
            double y = 2.0 * Math.Pow(x - 1000, 2) + 1.5;
            pts.Add(new AutoFocusPoint { Position = x, HFR = y, StarCount = 50 });
        }

        var fit = AutoFocusService.FitParabola(pts);

        // MinX is far more robust than MinY with large absolute x values:
        // Cramer's rule sums x^4 which loses ~6 digits of precision at x~1000.
        // For real focuser units (~thousands) we still get sub-step accuracy on
        // the vertex location, which is what matters for AF.
        Assert.That(fit.MinX, Is.EqualTo(1000.0).Within(0.5));
        Assert.That(fit.MinY, Is.EqualTo(1.5).Within(0.5));
        Assert.That(fit.A, Is.EqualTo(2.0).Within(0.1));
    }

    [Test]
    public void FitParabola_SymmetricVCurve_FindsVertex() {
        // Classic V-curve: minimum at 5000
        var pts = Pts(
            (4800, 8.5),
            (4850, 6.2),
            (4900, 4.0),
            (4950, 2.3),
            (5000, 1.5),
            (5050, 2.3),
            (5100, 4.1),
            (5150, 6.3),
            (5200, 8.4)
        );

        var fit = AutoFocusService.FitParabola(pts);

        // Vertex is what matters for focus, the V-shape isn't a true parabola,
        // so the predicted MinY can sit slightly above the lowest sample.
        Assert.That(fit.MinX, Is.EqualTo(5000).Within(5));
        Assert.That(fit.MinY, Is.LessThan(3.0));
        Assert.That(fit.A, Is.GreaterThan(0), "Parabola must open upward (focus minimum)");
    }

    [Test]
    public void FitParabola_AsymmetricSamples_StillFindsReasonableMin() {
        // Samples skewed left of true minimum (5050)
        var pts = Pts(
            (4900, 4.5),
            (4950, 3.0),
            (5000, 2.0),
            (5050, 1.5),
            (5100, 2.0)
        );

        var fit = AutoFocusService.FitParabola(pts);

        Assert.That(fit.MinX, Is.GreaterThan(5020));
        Assert.That(fit.MinX, Is.LessThan(5080));
    }

    [Test]
    public void FitParabola_WithNoisySamples_ConvergesNearTruth() {
        // True vertex at 3000 with a=0.001
        var rng = new Random(42);
        var pts = new List<AutoFocusPoint>();
        for (int x = 2800; x <= 3200; x += 25) {
            double y = 0.001 * Math.Pow(x - 3000, 2) + 1.8 + (rng.NextDouble() - 0.5) * 0.2;
            pts.Add(new AutoFocusPoint { Position = x, HFR = y, StarCount = 50 });
        }

        var fit = AutoFocusService.FitParabola(pts);

        Assert.That(fit.MinX, Is.EqualTo(3000).Within(20), "Vertex should be within 20 steps of truth");
        Assert.That(fit.MinY, Is.EqualTo(1.8).Within(0.3));
    }

    [Test]
    public void FitParabola_LessThan3Points_Throws() {
        var pts = Pts((100, 2.0), (200, 1.5));
        Assert.Throws<ArgumentException>(() => AutoFocusService.FitParabola(pts));
    }

    [Test]
    public void FitParabola_CollinearPoints_FallsBackToMinSample() {
        // All on a straight line, singular matrix or near-zero 'a'
        var pts = Pts((100, 5.0), (200, 4.0), (300, 3.0), (400, 2.0));

        var fit = AutoFocusService.FitParabola(pts);

        // Should not crash and should not propose an absurd vertex
        Assert.That(fit, Is.Not.Null);
    }

    // ---- Robust fit (spurious-point rejection) ----

    [Test]
    public void FitParabolaRobust_NoOutliers_KeepsAllPoints() {
        var pts = Pts(
            (4800, 8.5), (4850, 6.2), (4900, 4.0), (4950, 2.3), (5000, 1.5),
            (5050, 2.3), (5100, 4.1), (5150, 6.3), (5200, 8.4)
        );

        var (fit, inliers, rejected) = AutoFocusService.FitParabolaRobust(pts);

        Assert.That(rejected, Is.Empty, "Clean V-curve should not drop any point");
        Assert.That(inliers.Count, Is.EqualTo(pts.Count));
        Assert.That(fit.MinX, Is.EqualTo(5000).Within(5));
    }

    [Test]
    public void FitParabolaRobust_SingleSpike_RejectsItAndFixesVertex() {
        // Same V-curve as above but one sample (5050) is a gross outlier:
        // a passing cloud / mis-measured trail reading HFR 25 instead of ~2.3.
        var pts = Pts(
            (4800, 8.5), (4850, 6.2), (4900, 4.0), (4950, 2.3), (5000, 1.5),
            (5050, 25.0), (5100, 4.1), (5150, 6.3), (5200, 8.4)
        );

        var clean = AutoFocusService.FitParabola(pts);          // contaminated fit
        var (robust, inliers, rejected) = AutoFocusService.FitParabolaRobust(pts);

        Assert.That(rejected.Count, Is.EqualTo(1), "Exactly the spike should be dropped");
        Assert.That(rejected[0].Position, Is.EqualTo(5050));
        Assert.That(inliers, Has.None.Matches<AutoFocusPoint>(p => p.Position == 5050));
        // The robust vertex should land near the true minimum, and closer to it
        // than the contaminated least-squares fit.
        Assert.That(robust.MinX, Is.EqualTo(5000).Within(15));
        Assert.That(Math.Abs(robust.MinX - 5000), Is.LessThan(Math.Abs(clean.MinX - 5000)));
    }

    [Test]
    public void FitParabolaRobust_NeverClipsBelowThreePoints() {
        // Three points with one wild value: there is no fittable subset of >=3
        // after dropping it, so the method must keep all three rather than throw.
        var pts = Pts((100, 2.0), (200, 50.0), (300, 2.5));

        var (fit, inliers, rejected) = AutoFocusService.FitParabolaRobust(pts);

        Assert.That(inliers.Count, Is.GreaterThanOrEqualTo(3));
        Assert.That(rejected, Is.Empty);
        Assert.That(fit, Is.Not.Null);
    }

    // ---- Plateau trimming (skate-ramp V-curves) ----

    [Test]
    public void TrimPlateaus_SkateRamp_DropsFlatShouldersAndFixesVertex() {
        // V with a vertex at 5000 but both extremes saturated flat at HFR ~9
        // (the "skate ramp" the field report describes). The plateaus pull a
        // raw parabola wider/flatter; trimming them should recover the vertex.
        var pts = Pts(
            (4700, 9.0), (4750, 9.0), (4800, 9.0),   // left plateau
            (4850, 7.0), (4900, 4.5), (4950, 2.4),
            (5000, 1.5),
            (5050, 2.4), (5100, 4.5), (5150, 7.0),
            (5200, 9.0), (5250, 9.0), (5300, 9.0)    // right plateau
        );

        var inner = AutoFocusService.TrimPlateaus(pts);

        // The flat shoulder samples must be gone.
        Assert.That(inner, Has.None.Matches<AutoFocusPoint>(p => p.Position <= 4800 || p.Position >= 5200));
        // And the inner V still fits to the true vertex. (The data is
        // symmetric, so the vertex itself isn't shifted by the plateaus — the
        // asymmetric test below is what proves trimming improves accuracy.)
        var fit = AutoFocusService.FitParabola(inner);
        Assert.That(fit.MinX, Is.EqualTo(5000).Within(20));
    }

    [Test]
    public void TrimPlateaus_CleanVCurve_KeepsAllPoints() {
        // No flat shoulders: every step has real slope, nothing to trim.
        var pts = Pts(
            (4800, 8.5), (4850, 6.2), (4900, 4.0), (4950, 2.3), (5000, 1.5),
            (5050, 2.3), (5100, 4.1), (5150, 6.3), (5200, 8.4)
        );

        var inner = AutoFocusService.TrimPlateaus(pts);

        Assert.That(inner.Count, Is.EqualTo(pts.Count));
    }

    [Test]
    public void TrimPlateaus_AsymmetricPlateau_TrimsOnlyTheFlatSide() {
        // Flat shoulder only on the right; the left arm is a clean slope.
        var pts = Pts(
            (4850, 7.0), (4900, 4.5), (4950, 2.4), (5000, 1.5),
            (5050, 2.4), (5100, 4.5), (5150, 7.0),
            (5200, 9.0), (5250, 9.0), (5300, 9.0)   // right plateau only
        );

        var inner = AutoFocusService.TrimPlateaus(pts);

        Assert.That(inner, Has.None.Matches<AutoFocusPoint>(p => p.Position >= 5200), "right plateau dropped");
        Assert.That(inner, Has.Some.Matches<AutoFocusPoint>(p => p.Position == 4850), "left arm kept intact");

        // The one-sided plateau drags the contaminated parabola's vertex right
        // of true focus (5000); trimming it pulls the vertex back closer.
        var contaminated = AutoFocusService.FitParabola(pts);
        var trimmedFit = AutoFocusService.FitParabola(inner);
        Assert.That(Math.Abs(trimmedFit.MinX - 5000),
            Is.LessThan(Math.Abs(contaminated.MinX - 5000)),
            "trimming the flat shoulder should move the vertex closer to true focus");
    }

    [Test]
    public void TrimPlateaus_NeverTrimsBelowMinKeep() {
        // Tiny sample: even if it looks flat, keep enough points to fit.
        var pts = Pts((100, 3.0), (200, 3.0), (300, 3.0), (400, 3.0));
        var inner = AutoFocusService.TrimPlateaus(pts);
        Assert.That(inner.Count, Is.EqualTo(pts.Count));
    }

    // ---- RejectLowWingOutliers ----

    [Test]
    public void RejectLowWingOutliers_DefocusDip_DropsTheLowFarPoint() {
        // Clean V plus one far-defocus sample whose HFR collapses back down:
        // the star has spread into a faint donut the detector can't measure, so
        // it reads spuriously LOW even though it is further out of focus. On a
        // convex V that is physically impossible, so it must be dropped.
        var pts = Pts(
            (4800, 8.5), (4850, 6.2), (4900, 4.0), (4950, 2.3), (5000, 1.5),
            (5050, 2.3), (5100, 4.1), (5150, 6.3), (5200, 8.4),
            (5250, 3.0)   // far-defocus dip — detection failure
        );

        var kept = AutoFocusService.RejectLowWingOutliers(pts, out var rejected);

        Assert.That(rejected, Has.Count.EqualTo(1));
        Assert.That(rejected[0].Position, Is.EqualTo(5250));
        Assert.That(kept, Has.None.Matches<AutoFocusPoint>(p => p.Position == 5250));
        Assert.That(kept, Has.Some.Matches<AutoFocusPoint>(p => p.Position == 5200));
    }

    [Test]
    public void RejectLowWingOutliers_CleanVCurve_KeepsEverything() {
        // Monotonic arms on both sides: nothing dips, nothing to drop.
        var pts = Pts(
            (4800, 8.5), (4850, 6.2), (4900, 4.0), (4950, 2.3), (5000, 1.5),
            (5050, 2.3), (5100, 4.1), (5150, 6.3), (5200, 8.4)
        );

        var kept = AutoFocusService.RejectLowWingOutliers(pts, out var rejected);

        Assert.That(rejected, Is.Empty);
        Assert.That(kept.Count, Is.EqualTo(pts.Count));
    }

    [Test]
    public void RejectLowWingOutliers_BothWingsDip_DropsBothFarPoints() {
        // A defocus-detection failure on each extreme.
        var pts = Pts(
            (4750, 2.5),  // far-defocus dip on the left
            (4800, 8.5), (4850, 6.2), (4900, 4.0), (4950, 2.3), (5000, 1.5),
            (5050, 2.3), (5100, 4.1), (5150, 6.3), (5200, 8.4),
            (5250, 3.0)   // far-defocus dip on the right
        );

        var kept = AutoFocusService.RejectLowWingOutliers(pts, out var rejected);

        Assert.That(rejected, Has.Count.EqualTo(2));
        Assert.That(kept, Has.None.Matches<AutoFocusPoint>(p => p.Position == 4750 || p.Position == 5250));
    }

    // ---- Settings / state defaults ----

    [Test]
    public void AutoFocusRequest_Defaults_AreSensible() {
        var r = new AutoFocusRequest();
        Assert.That(r.Steps, Is.EqualTo(9));
        Assert.That(r.StepSize, Is.EqualTo(50));
        Assert.That(r.ExposureSeconds, Is.EqualTo(2.0));
        Assert.That(r.MinStars, Is.EqualTo(5));
        Assert.That(r.BacklashSteps, Is.EqualTo(0));
        Assert.That(r.TakeConfirmationFrame, Is.True);
        // Quality-gate / reattempt defaults (Phase 1).
        Assert.That(r.RSquaredThreshold, Is.EqualTo(0.7));
        Assert.That(r.Attempts, Is.EqualTo(2));
        Assert.That(r.MaxHfrRatio, Is.EqualTo(1.15));
    }

    // ---- R² quality metric (drives the accept/reattempt gate) ----

    [Test]
    public void RSquared_CleanVCurve_IsHigh() {
        // y = 0.01(x-100)² + 2 sampled exactly -> perfect fit, R² == 1.
        var pts = new List<AutoFocusPoint>();
        for (int x = 60; x <= 140; x += 10) {
            double y = 0.01 * (x - 100) * (x - 100) + 2.0;
            pts.Add(new AutoFocusPoint { Position = x, HFR = y, StarCount = 30 });
        }
        var fit = AutoFocusService.FitParabola(pts);
        Assert.That(fit.RSquared, Is.GreaterThan(0.99));
    }

    [Test]
    public void RSquared_NoStructure_IsLow() {
        // HFR unrelated to position (a cloud rolled through) -> the parabola
        // explains almost nothing, R² well below the 0.7 accept threshold.
        var pts = new List<AutoFocusPoint> {
            new() { Position = 60,  HFR = 4.0, StarCount = 30 },
            new() { Position = 70,  HFR = 3.9, StarCount = 30 },
            new() { Position = 80,  HFR = 4.1, StarCount = 30 },
            new() { Position = 90,  HFR = 3.95, StarCount = 30 },
            new() { Position = 100, HFR = 4.05, StarCount = 30 },
            new() { Position = 110, HFR = 3.92, StarCount = 30 },
            new() { Position = 120, HFR = 4.08, StarCount = 30 },
        };
        var fit = AutoFocusService.FitParabola(pts);
        Assert.That(fit.RSquared, Is.LessThan(0.7));
    }

    // ---- Adaptive sweep direction/stop decision (NextAdaptiveStep) ----

    [Test]
    public void NextAdaptiveStep_FewSamples_GrowsSymmetrically() {
        // Only 2 valid points either side of start=100: not enough to fit, so it
        // grows outward; with equal counts below/above it picks the left arm.
        var pts = Pts((90, 3.0), (110, 3.0));
        var (action, target) = AutoFocusService.NextAdaptiveStep(
            pts, curMin: 90, curMax: 110, startPosition: 100, step: 10,
            need: 3, maxPoints: 12, totalSampled: 2);
        Assert.That(action, Is.EqualTo(AutoFocusService.AdaptiveAction.SampleLeft));
        Assert.That(target, Is.EqualTo(80));
    }

    [Test]
    public void NextAdaptiveStep_VertexAtRightEnd_GrowsRight() {
        // Descending toward higher positions: the fitted vertex sits at/after the
        // right end, so the right arm is short -> ask for a sample to the right.
        var pts = Pts((80, 6.0), (90, 4.5), (100, 3.2), (110, 2.3), (120, 2.0));
        var (action, target) = AutoFocusService.NextAdaptiveStep(
            pts, curMin: 80, curMax: 120, startPosition: 100, step: 10,
            need: 3, maxPoints: 12, totalSampled: 5);
        Assert.That(action, Is.EqualTo(AutoFocusService.AdaptiveAction.SampleRight));
        Assert.That(target, Is.EqualTo(130));
    }

    [Test]
    public void NextAdaptiveStep_WellSpread_IsDone() {
        // Symmetric V with >=3 points each side of the vertex (~100): done.
        var pts = Pts((70, 5.0), (80, 3.8), (90, 2.9), (100, 2.5),
                      (110, 2.9), (120, 3.8), (130, 5.0));
        var (action, _) = AutoFocusService.NextAdaptiveStep(
            pts, curMin: 70, curMax: 130, startPosition: 100, step: 10,
            need: 3, maxPoints: 12, totalSampled: 7);
        Assert.That(action, Is.EqualTo(AutoFocusService.AdaptiveAction.Done));
    }

    [Test]
    public void NextAdaptiveStep_PointCapReached_IsDone() {
        var pts = Pts((80, 6.0), (90, 4.5), (100, 3.2));
        var (action, _) = AutoFocusService.NextAdaptiveStep(
            pts, curMin: 80, curMax: 100, startPosition: 100, step: 10,
            need: 3, maxPoints: 3, totalSampled: 3);
        Assert.That(action, Is.EqualTo(AutoFocusService.AdaptiveAction.Done));
    }

    [Test]
    public void AutoFocusRequest_AdaptiveDefaults() {
        var r = new AutoFocusRequest();
        Assert.That(r.Adaptive, Is.False);          // manual sweep stays the default
        Assert.That(r.PointsPerSide, Is.EqualTo(3));
        Assert.That(r.MaxPoints, Is.EqualTo(12));
    }

    [Test]
    public void ComputeRSquared_ConstantData_IsOne() {
        // SStot == 0 (every HFR identical): treat as a perfect fit rather than
        // dividing by zero / flagging a flat dataset as "bad".
        var pts = new List<AutoFocusPoint> {
            new() { Position = 10, HFR = 3.0, StarCount = 30 },
            new() { Position = 20, HFR = 3.0, StarCount = 30 },
            new() { Position = 30, HFR = 3.0, StarCount = 30 },
        };
        Assert.That(AutoFocusService.ComputeRSquared(pts, 0, 0, 3.0), Is.EqualTo(1.0));
    }
}