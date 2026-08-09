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

using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Telling a guider that is fighting itself apart from one merely having a hard
/// night.
///
/// A detector that fires on bad seeing would restart guiding over and over for
/// nothing and lose more frames than the wind did, so most of these cases are
/// about NOT firing. Wind data is a sign pattern, not a size.
/// </summary>
[TestFixture]
public class GuideOscillationDetectorTests {

    /// <summary>Runaway overshoot: big, and reversing every single frame.</summary>
    private static List<double> Runaway(int n, double amp = 3.0) {
        var v = new List<double>(n);
        for (int i = 0; i < n; i++) v.Add(i % 2 == 0 ? amp : -amp);
        return v;
    }

    /// <summary>Uncorrelated noise of a given RMS, deterministic seed so a
    /// failure is reproducible.</summary>
    private static List<double> Seeing(int n, double sigma, int seed = 42) {
        var rng = new Random(seed);
        var v = new List<double>(n);
        for (int i = 0; i < n; i++) {
            // Box-Muller, so the distribution is actually gaussian rather than
            // uniform noise wearing a sigma label.
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            v.Add(sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }
        return v;
    }

    private static List<double> Drift(int n, double perFrame = 0.4) {
        var v = new List<double>(n);
        for (int i = 0; i < n; i++) v.Add(perFrame * (i + 1));
        return v;
    }

    // ── the case this exists for ────────────────────────────────────────

    [Test]
    public void RunawayOscillationIsCaught() {
        var v = GuideOscillationDetector.Judge(Runaway(12));

        Assert.That(v.Oscillating, Is.True);
        Assert.That(v.AlternationRate, Is.EqualTo(1.0).Within(0.001));
    }

    // ── and the cases it must leave alone ───────────────────────────────

    /// <summary>Bad seeing at the SAME amplitude as the runaway case. If the
    /// detector cannot separate these two it is an amplitude alarm wearing a
    /// different name, and it would restart guiding all night.</summary>
    [Test]
    public void BadSeeingAtTheSameAmplitudeIsNotOscillation() {
        var noise = Seeing(400, sigma: 3.0);

        var v = GuideOscillationDetector.Judge(noise);

        Assert.That(v.RmsArcsec, Is.GreaterThan(2.0), "precondition: it is big");
        Assert.That(v.AlternationRate, Is.LessThan(0.75),
            "uncorrelated noise reverses about half the time");
        Assert.That(v.Oscillating, Is.False);
    }

    [Test]
    public void DriftIsNotOscillation() {
        var v = GuideOscillationDetector.Judge(Drift(20));

        Assert.That(v.RmsArcsec, Is.GreaterThan(2.0), "precondition: it is big");
        Assert.That(v.Oscillating, Is.False,
            "a run in one direction is a polar-alignment or flexure problem, "
            + "and restarting guiding would not touch it");
    }

    /// <summary>Small alternating error is a guider working: every correction
    /// slightly overshoots and the next one takes it back. Firing here would
    /// restart a healthy session.</summary>
    [Test]
    public void SmallAlternationIsHealthyGuiding() {
        var v = GuideOscillationDetector.Judge(Runaway(20, amp: 0.4));

        Assert.That(v.AlternationRate, Is.EqualTo(1.0).Within(0.001));
        Assert.That(v.Oscillating, Is.False, "amplitude is the other half of the test");
    }

    [Test]
    public void APerfectlyGuidedAxisIsQuiet() {
        var v = GuideOscillationDetector.Judge(new double[20]);

        Assert.That(v.Oscillating, Is.False);
        Assert.That(v.RmsArcsec, Is.EqualTo(0));
    }

    // ── window handling ─────────────────────────────────────────────────

    /// <summary>One gust is not a runaway. The state worth acting on is the one
    /// that does not recover, and that takes frames to establish.</summary>
    [Test]
    public void TooFewSamplesNeverFires() {
        var v = GuideOscillationDetector.Judge(Runaway(4), minSamples: 8);

        Assert.That(v.Oscillating, Is.False);
    }

    [Test]
    public void AnEmptyOrNullWindowIsSafe() {
        Assert.That(GuideOscillationDetector.Judge(Array.Empty<double>()).Oscillating, Is.False);
        Assert.That(GuideOscillationDetector.Judge(null!).Oscillating, Is.False);
    }

    /// <summary>Zeros break the run rather than counting as reversals: an axis
    /// sitting at zero error is the opposite of oscillating, and counting those
    /// pairs would inflate the rate.</summary>
    [Test]
    public void ZeroSamplesDoNotCountAsReversals() {
        var v = GuideOscillationDetector.Judge(
            new double[] { 3, 0, -3, 0, 3, 0, -3, 0, 3, 0 });

        Assert.That(v.AlternationRate, Is.EqualTo(0),
            "no pair here is a sign change: every other sample is zero");
        Assert.That(v.Oscillating, Is.False);
    }

    // ── thresholds ──────────────────────────────────────────────────────

    [Test]
    public void RaisingTheAmplitudeThresholdSilencesASmallerSwing() {
        var errors = Runaway(12, amp: 2.5);

        Assert.That(GuideOscillationDetector.Judge(errors, rmsThresholdArcsec: 2.0).Oscillating,
            Is.True);
        Assert.That(GuideOscillationDetector.Judge(errors, rmsThresholdArcsec: 4.0).Oscillating,
            Is.False);
    }

    // ── two axes ────────────────────────────────────────────────────────

    [Test]
    public void EitherAxisOscillatingIsEnough() {
        var quiet = new double[12];

        var raOnly = GuideOscillationDetector.JudgeWorst(Runaway(12), quiet);
        var decOnly = GuideOscillationDetector.JudgeWorst(quiet, Runaway(12));

        Assert.That(raOnly.Oscillating, Is.True, "wind moves the tube, not one motor");
        Assert.That(decOnly.Oscillating, Is.True);
    }

    [Test]
    public void WithBothAxesQuietNothingFires() {
        Assert.That(
            GuideOscillationDetector.JudgeWorst(Seeing(200, 0.6), Seeing(200, 0.6, seed: 7))
                .Oscillating,
            Is.False);
    }

    /// <summary>When both axes are in trouble the report should describe the
    /// worse one, since that is the number worth logging.</summary>
    [Test]
    public void WithBothOscillatingTheWorseAxisIsReported() {
        var v = GuideOscillationDetector.JudgeWorst(Runaway(12, amp: 2.5), Runaway(12, amp: 6.0));

        Assert.That(v.Oscillating, Is.True);
        Assert.That(v.RmsArcsec, Is.EqualTo(6.0).Within(0.01));
    }
}
