// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using NINA.Guider.Portable;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>Unit tests for the native predictive guide algorithm (PE + drift
/// feed-forward). The math core lives in NINA.Guider.Portable so it tests
/// headless with no devices.</summary>
[TestFixture]
public class PredictiveAlgorithmTests {

    // Disturbance the star would experience without guiding: periodic error + drift.
    private static double Disturbance(double t, double amp, double periodSec, double driftPerSec)
        => amp * Math.Sin(2.0 * Math.PI * t / periodSec) + driftPerSec * t;

    [Test]
    public void ClosedLoop_tracks_pe_to_low_rms() {
        const double amp = 2.0, period = 100.0, drift = 0.01, dt = 2.0;
        // Manual period removes auto-estimation flakiness; this isolates tracking.
        var algo = new PredictiveAlgorithm(minMove: 0.0, aggression: 0.7,
            hysteresis: 0.1, wormPeriodSec: period, windowSamples: 256, predBlend: 1.0);
        double rms = ClosedLoopRms(algo, amp, period, drift, dt);
        Assert.That(rms, Is.LessThan(0.35 * amp),
            "feed-forward should drive the residual well below the PE amplitude");
    }

    [Test]
    public void ClosedLoop_rms_beats_hysteresis_on_pe_plus_drift() {
        const double amp = 2.0, period = 100.0, drift = 0.01, dt = 2.0;
        var predictive = new PredictiveAlgorithm(0.0, 0.7, 0.1, period, 256, 1.0);
        var hysteresis = new HysteresisAlgorithm(0.10, 0.70, 0.0);

        double rmsPred = ClosedLoopRms(predictive, amp, period, drift, dt);
        double rmsHyst = ClosedLoopRms(hysteresis, amp, period, drift, dt);

        Assert.That(rmsPred, Is.LessThan(rmsHyst),
            "predictive feed-forward should leave less residual than reactive hysteresis");
        Assert.That(rmsPred, Is.LessThan(0.6 * rmsHyst),
            "the improvement should be substantial on a strong PE + drift signal");
    }

    // Perfect-actuator closed loop: a correction c moves the star by -c. Returns
    // the RMS of the measured error over the counted window (by default the back
    // half of a 400-frame run, i.e. steady state).
    private static double ClosedLoopRms(IGuideAlgorithm algo, double amp, double period,
                                        double drift, double dt,
                                        Func<double, double, double, double, double>? signal = null,
                                        int frames = 400, int fromFrame = -1) {
        signal ??= Disturbance;
        int countFrom = fromFrame >= 0 ? fromFrame : frames / 2;
        double t = 0, applied = 0, sumSq = 0; int counted = 0;
        for (int i = 0; i < frames; i++) {
            t += dt;
            double measured = signal(t, amp, period, drift) - applied;
            double corr = algo.Result(measured, dt);
            applied += corr;
            if (i >= countFrom) { sumSq += measured * measured; counted++; }
        }
        return counted > 0 ? Math.Sqrt(sumSq / counted) : 0.0;
    }

    [Test]
    public void WarmUp_is_finite_and_predicts_zero_before_min_samples() {
        var algo = new PredictiveAlgorithm(0.1, 0.7, 0.1, 0.0, 256, 0.7);
        var rng = new Random(1);
        for (int i = 0; i < 10; i++) {
            double r = algo.Result((rng.NextDouble() - 0.5) * 4.0, 2.0);
            Assert.That(double.IsFinite(r), Is.True, "correction must never be NaN/Inf");
            Assert.That(algo.LastPredictedError, Is.EqualTo(0.0),
                "no prediction is emitted before the minimum sample count");
        }
    }

    [Test]
    public void Reset_clears_history() {
        const double period = 100.0, dt = 2.0;
        var algo = new PredictiveAlgorithm(0.0, 0.7, 0.1, period, 256, 1.0);
        double t = 0;
        for (int i = 0; i < 100; i++) { t += dt; algo.Result(Disturbance(t, 2, period, 0.01), dt); }
        Assert.That(algo.LastPredictedError, Is.Not.EqualTo(0.0));

        algo.Reset();
        Assert.That(algo.LastPredictedError, Is.EqualTo(0.0));
        Assert.That(algo.EstimatedPeriodSec, Is.EqualTo(0.0));
        // First post-reset frame is back in warm-up: finite, no prediction.
        double r = algo.Result(1.5, dt);
        Assert.That(double.IsFinite(r), Is.True);
        Assert.That(algo.LastPredictedError, Is.EqualTo(0.0));
    }

    // Real worm PE is not a clean sine. This is the fundamental plus a second
    // harmonic at 40% and a third at 20%, which is an ordinary shape for a
    // mass-produced worm.
    private static double HarmonicDisturbance(double t, double amp, double periodSec,
                                              double driftPerSec, double phase = 0.0) {
        double w = 2.0 * Math.PI / periodSec;
        return amp * (Math.Sin(w * t + phase)
                      + 0.40 * Math.Sin(2 * w * t + 0.7 + phase)
                      + 0.20 * Math.Sin(3 * w * t + 1.9 + phase))
               + driftPerSec * t;
    }

    // Four-argument adapter for the closed-loop harness (the phase argument
    // above has a default, which a method group cannot bind through).
    private static double Harmonic4(double t, double amp, double periodSec, double driftPerSec)
        => HarmonicDisturbance(t, amp, periodSec, driftPerSec);

    [Test]
    public void Harmonic_series_beats_a_single_sinusoid_on_non_sinusoidal_pe() {
        const double amp = 2.0, period = 100.0, drift = 0.01, dt = 2.0;
        var oneHarmonic = new PredictiveAlgorithm(0.0, 0.7, 0.1, period, 256, 1.0, maxHarmonics: 1);
        var series = new PredictiveAlgorithm(0.0, 0.7, 0.1, period, 256, 1.0, maxHarmonics: 3);

        double rmsOne = ClosedLoopRms(oneHarmonic, amp, period, drift, dt, Harmonic4);
        double rmsSeries = ClosedLoopRms(series, amp, period, drift, dt, Harmonic4);

        TestContext.Out.WriteLine($"1 harmonic RMS={rmsOne:F3}  3 harmonics RMS={rmsSeries:F3}");
        Assert.That(rmsSeries, Is.LessThan(rmsOne),
            "the harmonic series should track a non-sinusoidal worm better than one sine");
        Assert.That(series.ChosenHarmonics, Is.GreaterThan(1),
            "model selection should have accepted more than the fundamental here");
    }

    [Test]
    public void Model_selection_keeps_one_harmonic_on_a_clean_sine() {
        // Adjusted R² has to refuse the extra terms when there is nothing for
        // them to explain, otherwise the fit spends its freedom on seeing noise.
        const double amp = 2.0, period = 100.0, dt = 2.0;
        var algo = new PredictiveAlgorithm(0.0, 0.7, 0.1, period, 256, 1.0, maxHarmonics: 3);
        var rng = new Random(7);
        double t = 0, applied = 0;
        for (int i = 0; i < 300; i++) {
            t += dt;
            double noise = (rng.NextDouble() - 0.5) * 0.30;
            double measured = Disturbance(t, amp, period, 0.0) + noise - applied;
            applied += algo.Result(measured, dt);
        }
        Assert.That(algo.ChosenHarmonics, Is.EqualTo(1),
            "a clean sine plus noise should not buy a second or third harmonic");
    }

    [Test]
    public void A_restored_model_predicts_from_the_first_minute() {
        const double amp = 2.0, period = 100.0, drift = 0.01, dt = 2.0;

        // Night 1: learn the mount.
        var night1 = new PredictiveAlgorithm(0.0, 0.7, 0.1, period, 256, 1.0, maxHarmonics: 3);
        ClosedLoopRms(night1, amp, period, drift, dt, Harmonic4);
        var learned = night1.ExportModel();
        Assert.That(learned, Is.Not.Null, "night 1 should have produced a keepable model");
        Assert.That(learned!.IsUsable, Is.True);
        Assert.That(learned.PeriodSec, Is.EqualTo(period).Within(0.01));

        // Night 2: same mount, worm somewhere else, so the stored phase is
        // worthless and only the shape and period carry over. Neither instance is
        // told the period: that is what the stored model is for.
        const double phase = 2.4;
        var cold = new PredictiveAlgorithm(0.0, 0.7, 0.1, 0.0, 256, 1.0, maxHarmonics: 3);
        var warm = new PredictiveAlgorithm(0.0, 0.7, 0.1, 0.0, 256, 1.0, maxHarmonics: 3,
                                           prior: learned);

        var (coldErr, coldPeriod, _) = WarmUpRun(cold, amp, period, drift, dt, phase);
        var (warmErr, warmPeriod, usedPrior) = WarmUpRun(warm, amp, period, drift, dt, phase);

        TestContext.Out.WriteLine(
            $"first minute: cold |prediction error|={coldErr:F3} px (period {coldPeriod:F0}s), "
            + $"restored={warmErr:F3} px (period {warmPeriod:F0}s), prior path used={usedPrior}");

        Assert.That(usedPrior, Is.True,
            "the re-phasing shortcut should engage before the full fit has enough samples");
        Assert.That(warmPeriod, Is.EqualTo(period).Within(0.01),
            "a restored model hands over the worm period immediately");
        Assert.That(coldPeriod, Is.Not.EqualTo(period).Within(0.2 * period),
            "a cold start cannot know the period yet; that is the gap being closed");
        Assert.That(warmErr, Is.LessThan(0.75 * coldErr),
            "the restored model should predict the next frame's error better than a cold start");
    }

    /// <summary>Runs the first minute of a session and reports how well the
    /// algorithm predicted each next frame. The prediction is what the residual
    /// WOULD be if nothing more were applied, so the harness can compute the truth
    /// it is being compared against.</summary>
    private static (double meanAbsPredictionError, double period, bool usedPrior) WarmUpRun(
            PredictiveAlgorithm algo, double amp, double period, double drift, double dt,
            double phase) {
        double t = 0, applied = 0, sum = 0;
        int counted = 0;
        bool usedPrior = false;
        for (int i = 0; i < 30; i++) {
            t += dt;
            double measured = HarmonicDisturbance(t, amp, period, drift, phase) - applied;
            double appliedBefore = applied;
            applied += algo.Result(measured, dt);
            usedPrior |= algo.UsingPriorModel;
            // Count from the frame where a prediction is structurally possible at
            // all (the re-phasing shortcut's minimum sample count). Before that
            // both instances are blind by construction, so including those frames
            // would only dilute the comparison with a shared floor.
            if (i >= 6) {
                double truthNext = HarmonicDisturbance(t + dt, amp, period, drift, phase) - appliedBefore;
                sum += Math.Abs(algo.LastPredictedError - truthNext);
                counted++;
            }
        }
        return (sum / counted, algo.EstimatedPeriodSec, usedPrior);
    }

    [Test]
    public void A_restored_model_is_ignored_when_it_does_not_fit_the_data() {
        // Guard against a stale model (different mount, gear swapped) steering
        // the warm-up: the phase fit has to fail and leave the reactive baseline.
        const double dt = 2.0;
        var bogus = new PredictiveModel(100.0, new[] { 5.0, 5.0 }, 0.9);
        var algo = new PredictiveAlgorithm(0.0, 0.7, 0.1, 100.0, 256, 1.0, prior: bogus);
        var rng = new Random(3);
        for (int i = 0; i < 12; i++) {
            double r = algo.Result((rng.NextDouble() - 0.5) * 0.02, dt);   // pure small noise
            Assert.That(double.IsFinite(r), Is.True);
        }
        Assert.That(algo.UsingPriorModel, Is.False,
            "a model that explains none of the measured variance must not be used");
    }

    [Test]
    public void Auto_period_estimation_finds_injected_period() {
        const double amp = 2.0, period = 120.0, dt = 3.0; // 40 samples / cycle
        var algo = new PredictiveAlgorithm(0.0, 0.7, 0.1, wormPeriodSec: 0.0,
            windowSamples: 256, predBlend: 1.0);
        // Closed loop so the algorithm's internal disturbance reconstruction
        // (residual + commanded) matches the injected signal.
        double t = 0, applied = 0;
        for (int i = 0; i < 130; i++) {
            t += dt;
            double measured = amp * Math.Sin(2 * Math.PI * t / period) - applied;
            applied += algo.Result(measured, dt);
        }
        Assert.That(algo.EstimatedPeriodSec, Is.EqualTo(period).Within(0.15 * period),
            "auto-estimation should recover the injected worm period within 15%");
    }
}
