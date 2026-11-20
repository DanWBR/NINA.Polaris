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
using System.Linq;
using NUnit.Framework;
using NINA.Image.ImageAnalysis.AutoFocus;

namespace NINA.Polaris.Test;

/// <summary>
/// The fine autofocus pass against a focuser with real slack: the drawtube
/// only moves when the motor pushes it out, or pulls it in after crossing the
/// gap. The tests run what AutoFocusService runs (an inward coarse sweep, the
/// coarse fit, the outward fine walk and the outward final approach) and
/// check where the DRAWTUBE ends up, not where the motor says it is.
/// </summary>
[TestFixture]
public class FocusRefineWalkTests {

    private const double Focus = 10_000;   // true focus, drawtube coordinates
    private const double MinHfr = 2.0;
    private const double Width = 200;      // hyperbola width: about 1 px HFR per 100 steps on the arms
    private const int CoarseStep = 100;
    private const int FineStep = 25;

    /// <summary>A focuser with a gap of <c>Slack</c> steps between the motor and the drawtube.</summary>
    private sealed class SlackFocuser {
        public double Motor;
        public double Tube;
        private readonly double _slack;
        public SlackFocuser(double start, double slack) { Motor = Tube = start; _slack = slack; }
        public void MoveTo(double m) {
            if (m > Tube) Tube = m;                        // pushing out
            else if (m < Tube - _slack) Tube = m + _slack; // pulling in, past the gap
            Motor = m;
        }
    }

    private static double Hfr(double tube, Random rng, double noise) {
        double h = MinHfr * Math.Sqrt(1 + Math.Pow((tube - Focus) / Width, 2));
        // Box-Muller: seeing scatter, a fraction of the HFR.
        double g = Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        return h * (1 + noise * g);
    }

    private sealed record Run(FocusRefineOutcome Outcome, double CoarseVertex, double FinalTube, int FinePoints, string Trace);

    /// <summary>The autofocus sequence, as AutoFocusService drives it with no backlash compensation.</summary>
    private static Run Autofocus(double slack, int seed, double noise = 0.02, double startOffset = 0) {
        var rng = new Random(seed);
        double start = Focus + startOffset;
        var f = new SlackFocuser(start + 800, slack);

        // Coarse sweep: from start + 5 steps, inward through 11 points.
        var coarse = new List<FocusPoint>();
        for (int i = 0; i <= 10; i++) {
            double m = start + 5 * CoarseStep - i * CoarseStep;
            f.MoveTo(m);
            double h = Hfr(f.Tube, rng, noise);
            coarse.Add(new FocusPoint(m, h, 0.02 * h + 0.01));
        }
        var fit = new HyperbolicFitting().Calculate(coarse);
        Assert.That(fit.HasFit, Is.True);
        double v = fit.Minimum.X;

        // Fine walk outward, after taking up the slack from below (margin 2 coarse steps).
        var walk = new FocusRefineWalk(v, CoarseStep, FineStep, direction: +1);
        f.MoveTo(walk.StartPosition - 2 * CoarseStep);
        while (walk.Next() is int t) {
            f.MoveTo(t);
            double h = Hfr(f.Tube, rng, noise);
            walk.Add(t, h, 0.02 * h + 0.01);
        }
        var outcome = walk.Finish();

        // Final approach from the same side as the fine points.
        if (outcome.Ok) {
            double target = Math.Round(outcome.Vertex);
            f.MoveTo(target - 2 * CoarseStep);
            f.MoveTo(target);
        }
        string trace = $"coarse vertex {v:F0}; fine " + string.Join(" ", walk.Points.Select(p => $"{p.X:F0}:{p.Y:F2}"));
        return new Run(outcome, v, f.Tube, walk.Points.Count, trace);
    }

    [TestCase(0)]
    [TestCase(40)]
    [TestCase(90)]
    [TestCase(160)]
    public void MeasuresTheSlackAndLandsTheDrawtubeOnFocus(double slack) {
        var errs = new List<double>();
        for (int seed = 1; seed <= 20; seed++) {
            var r = Autofocus(slack, seed);
            Assert.That(r.Outcome.Ok, Is.True, $"seed {seed}: {r.Outcome.Reason}; {r.Trace}");
            // The drawtube ends within one fine step of focus whatever the slack.
            Assert.That(Math.Abs(r.FinalTube - Focus), Is.LessThan(FineStep), $"seed {seed}");
            errs.Add(r.Outcome.BacklashSteps - slack);
        }
        errs.Sort();
        double median = errs[errs.Count / 2];
        // One run is within a fine step; the median of a few runs is much closer.
        Assert.That(errs, Has.All.InRange(-FineStep * 1.2, FineStep * 1.2));
        Assert.That(Math.Abs(median), Is.LessThan(FineStep * 0.4), $"median error {median:F1}");
    }

    [Test]
    public void BadSeeingCanLeaveAWalkInconclusiveButNeverLandsOffFocus() {
        // 4 % frame-to-frame scatter, twice the other tests.
        int ok = 0;
        var errs = new List<double>();
        for (int seed = 100; seed < 150; seed++) {
            var r = Autofocus(slack: 60, seed: seed, noise: 0.04);
            if (!r.Outcome.Ok) continue;   // the service keeps the coarse focus then
            ok++;
            Assert.That(Math.Abs(r.FinalTube - Focus), Is.LessThan(1.6 * FineStep), $"seed {seed}; {r.Trace}");
            errs.Add(r.Outcome.BacklashSteps - 60);
        }
        Assert.That(ok, Is.GreaterThanOrEqualTo(45));
        errs.Sort();
        Assert.That(Math.Abs(errs[errs.Count / 2]), Is.LessThan(FineStep * 0.5));
    }

    [Test]
    public void TheOldFixedWindowLandsOffFocusByTheSlack() {
        // What happened in the field: the coarse vertex, reached moving in, is
        // short of focus by the slack, and a final move that reverses again
        // loses the slack on top of that.
        var r = Autofocus(slack: 90, seed: 3);
        Assert.That(Math.Abs(r.CoarseVertex - (Focus - 90)), Is.LessThan(FineStep));
    }

    [Test]
    public void StopsSoonAfterTheMinimum() {
        var r = Autofocus(slack: 60, seed: 7);
        // From 1 coarse step (4 fine steps) before the coarse vertex to the minimum (60/25 = 2.4
        // steps past it) and a little beyond: well under the 24-point cap.
        Assert.That(r.FinePoints, Is.InRange(8, 16));
    }

    [Test]
    public void ReportsFailureWhenTheMinimumIsBeyondTheWalk() {
        // The walk reaches 3 coarse steps (300) past the coarse vertex at 1000;
        // a fine minimum at 1500 is outside it, so the HFR is still falling at
        // the last point.
        var walk = new FocusRefineWalk(1000, 100, 25, +1);
        while (walk.Next() is int t) {
            double h = MinHfr * Math.Sqrt(1 + Math.Pow((t - 1500) / Width, 2));
            walk.Add(t, h, 0.01);
        }
        Assert.That(walk.Points[^1].X, Is.EqualTo(1300));
        var outcome = walk.Finish();
        Assert.That(outcome.Ok, Is.False);
        Assert.That(outcome.Reason, Does.Contain("still falling"));
    }

    [Test]
    public void NextRefusesToRunAheadOfTheMeasurements() {
        var walk = new FocusRefineWalk(1000, 100, 25, +1);
        Assert.That(walk.Next(), Is.EqualTo(900));
        Assert.Throws<InvalidOperationException>(() => walk.Next());
    }

    [Test]
    public void InwardWalkMirrorsTheOutwardOne() {
        var walk = new FocusRefineWalk(1000, 100, 25, -1);
        Assert.That(walk.StartPosition, Is.EqualTo(1100));
        Assert.That(walk.Limit, Is.EqualTo(700));
    }
}
