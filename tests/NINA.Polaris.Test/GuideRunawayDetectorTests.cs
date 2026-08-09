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
/// Calibrated against a paired field experiment: two rigs on the same night at
/// the same site, one SV503 on a soft tripod that the wind reached (23087
/// frames, 14 sessions, most ended by hand) and one small Askar FRA400 that it
/// did not (30387 frames, 12 sessions). Same sky, so the only variable is the
/// rig. The figures here come from that data rather than being invented,
/// because two plausible theories died against it:
///
/// <list type="number">
///   <item>sign-alternating over-correction: in 23087 frames not one window
///         above 8 arcsec RMS had alternation even above 0.5, so the failure is
///         a monotonic excursion, not an oscillation</item>
///   <item>a modest absolute threshold: at 8 arcsec the SHELTERED rig fires
///         almost as often as the wind-hit one (1/1266 frames vs 1/699), so
///         anything that low is an alarm on the shared sky rather than on the
///         rig that was actually in trouble</item>
/// </list>
/// </summary>
[TestFixture]
public class GuideRunawayDetectorTests {

    private static List<double> Constant(int n, double v) =>
        Enumerable.Repeat(v, n).ToList();

    private static List<double> Growing(int n, double start, double perFrame) =>
        Enumerable.Range(0, n).Select(i => start + perFrame * i).ToList();

    private static List<double> Seeing(int n, double sigma, int seed = 42) {
        var rng = new Random(seed);
        return Enumerable.Range(0, n).Select(_ => {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            return sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }).ToList();
    }

    // ── the real failure ────────────────────────────────────────────────

    /// <summary>The state the operator kept restarting by hand. Session
    /// 2026-08-08 05:28 ended at a median 61.9 arcsec RMS with alternation
    /// 0.00: the star was simply gone and staying gone.</summary>
    [Test]
    public void AnErrorParkedFarOutsideUsableIsARunaway() {
        var v = GuideRunawayDetector.Judge(Constant(12, 62.0));

        Assert.That(v.RunAway, Is.True);
        Assert.That(v.AlternationRate, Is.EqualTo(0),
            "and it does not alternate, which is exactly why the first "
            + "detector missed this");
    }

    /// <summary>An excursion still growing is the clearest case: nothing is
    /// pulling it back.</summary>
    [Test]
    public void AGrowingExcursionIsARunaway() {
        Assert.That(GuideRunawayDetector.Judge(Growing(12, 20.0, 4.0)).RunAway, Is.True);
    }

    // ── and what must not fire ──────────────────────────────────────────

    /// <summary>73% of that night's windows were under 1 arcsec RMS. Normal
    /// guiding must never be interrupted.</summary>
    [Test]
    public void HealthyGuidingIsLeftAlone() {
        Assert.That(GuideRunawayDetector.Judge(Seeing(12, 0.6)).RunAway, Is.False);
    }

    /// <summary>Bad seeing is a hard night, not a broken loop, and restarting
    /// costs settle time and changes nothing. The values are measured: 2.15 and
    /// 4.70 are the peaks of the SV503's two long healthy sessions, and 8.0 to
    /// 11.4 is the band the sheltered FRA400 lived in under the same sky.
    /// Firing anywhere in here is what makes a guard a nuisance.</summary>
    [TestCase(2.15)]
    [TestCase(4.70)]
    [TestCase(8.0)]
    [TestCase(11.4)]
    [TestCase(15.0)]
    public void ARoughButWorkingSessionDoesNotFire(double rms) {
        Assert.That(GuideRunawayDetector.Judge(Constant(12, rms)).RunAway, Is.False);
    }

    /// <summary>A big error already collapsing is a loop doing its job, most
    /// often the frames right after a dither or a slew. Restarting mid-recovery
    /// would throw away the recovery.</summary>
    [Test]
    public void AnErrorAlreadyComingBackIsNotTouched() {
        var recovering = Growing(12, 90.0, -6.0);   // 90" down to ~24"

        var v = GuideRunawayDetector.Judge(recovering);

        Assert.That(v.RmsArcsec, Is.GreaterThan(30.0), "precondition: still large");
        Assert.That(v.TrendArcsecPerFrame, Is.LessThan(0));
        Assert.That(v.RunAway, Is.False);
    }

    /// <summary>One bad frame in an otherwise fine window is a gust, not a
    /// runaway: the RMS of the window is what counts.</summary>
    [Test]
    public void ASingleSpikeDoesNotFire() {
        var v = Constant(12, 0.5);
        v[6] = 80.0;

        Assert.That(GuideRunawayDetector.Judge(v).RunAway, Is.False);
    }

    // ── window handling ─────────────────────────────────────────────────

    [Test]
    public void TooFewSamplesNeverFires() {
        Assert.That(GuideRunawayDetector.Judge(Constant(6, 60.0), minSamples: 12).RunAway,
            Is.False);
    }

    [Test]
    public void AnEmptyOrNullWindowIsSafe() {
        Assert.That(GuideRunawayDetector.Judge(Array.Empty<double>()).RunAway, Is.False);
        Assert.That(GuideRunawayDetector.Judge(null!).RunAway, Is.False);
    }

    // ── threshold ───────────────────────────────────────────────────────

    [Test]
    public void TheThresholdIsWhatDecides() {
        var errors = Constant(12, 20.0);

        Assert.That(GuideRunawayDetector.Judge(errors, rmsThresholdArcsec: 12.0).RunAway,
            Is.True);
        Assert.That(GuideRunawayDetector.Judge(errors, rmsThresholdArcsec: 30.0).RunAway,
            Is.False);
    }

    // ── two axes ────────────────────────────────────────────────────────

    [Test]
    public void EitherAxisRunningAwayIsEnough() {
        var quiet = Constant(12, 0.4);

        Assert.That(GuideRunawayDetector.JudgeWorst(Constant(12, 62.0), quiet).RunAway, Is.True);
        Assert.That(GuideRunawayDetector.JudgeWorst(quiet, Constant(12, 62.0)).RunAway, Is.True);
    }

    [Test]
    public void WithBothAxesQuietNothingFires() {
        Assert.That(
            GuideRunawayDetector.JudgeWorst(Seeing(12, 0.6), Seeing(12, 0.6, seed: 7)).RunAway,
            Is.False);
    }

    [Test]
    public void WithBothRunningAwayTheWorseAxisIsReported() {
        var v = GuideRunawayDetector.JudgeWorst(Constant(12, 40.0), Constant(12, 90.0));

        Assert.That(v.RunAway, Is.True);
        Assert.That(v.RmsArcsec, Is.EqualTo(90.0).Within(0.01));
    }

    /// <summary>Alternation is reported but must NOT gate the verdict: gating
    /// on it is the bug this detector was rewritten to fix, and a test that
    /// pins the number is what stops it coming back.</summary>
    [Test]
    public void AlternationIsReportedButDoesNotGate() {
        var alternating = Enumerable.Range(0, 12)
            .Select(i => i % 2 == 0 ? 62.0 : -62.0).ToList();

        var flat = GuideRunawayDetector.Judge(Constant(12, 62.0));
        var swinging = GuideRunawayDetector.Judge(alternating);

        Assert.That(swinging.AlternationRate, Is.EqualTo(1.0).Within(0.001));
        Assert.That(flat.AlternationRate, Is.EqualTo(0));
        Assert.That(swinging.RunAway, Is.True);
        Assert.That(flat.RunAway, Is.True,
            "both are runaways; the sign pattern is diagnostic colour, not the test");
    }
}
