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
    // RMS of the measured error over the back half of the run.
    private static double ClosedLoopRms(IGuideAlgorithm algo, double amp, double period,
                                        double drift, double dt) {
        double t = 0, applied = 0, sumSq = 0; int counted = 0;
        int n = 400;
        for (int i = 0; i < n; i++) {
            t += dt;
            double measured = Disturbance(t, amp, period, drift) - applied;
            double corr = algo.Result(measured, dt);
            applied += corr;
            if (i >= n / 2) { sumSq += measured * measured; counted++; }
        }
        return Math.Sqrt(sumSq / counted);
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
