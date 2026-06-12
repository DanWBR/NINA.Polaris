// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

namespace NINA.Guider.Portable;

/// <summary>
/// Predictive guide algorithm: models the measured error as a slow linear
/// <b>drift</b> plus a dominant <b>periodic error</b> (worm-gear PE) sinusoid,
/// then feeds forward a correction that cancels the error predicted for the
/// <i>next</i> frame instead of only chasing the current one. Inspired by PHD2's
/// Predictive PEC, but self-contained and tuned for the native guider.
///
/// <para>It always blends with a reactive baseline (hysteresis) and falls back to
/// that baseline during warm-up or when the periodic fit is not confident, so it
/// never guides worse than the reactive default. Most valuable on the RA axis
/// (PE-dominant); on Dec the drift term still helps.</para>
/// </summary>
public sealed class PredictiveAlgorithm : IGuideAlgorithm {
    // Minimum samples before any prediction kicks in (pure reactive until then).
    private const int MinSamples = 16;
    // Periodic fit must explain at least this fraction of the detrended variance
    // to be trusted; otherwise only drift is fed forward.
    private const double PeConfidence = 0.15;

    private readonly double _minMove;
    private readonly double _aggression;
    private readonly int _window;
    private readonly double _predBlend;      // 0..1 feed-forward weight
    private readonly double _manualPeriodSec; // 0 = auto-estimate
    private readonly HysteresisAlgorithm _baseline;

    private readonly List<(double t, double e)> _buf = new();
    private double _tAccum;
    private double _lastDt;
    private double _estPeriodSec;
    // Cumulative correction this algorithm has commanded. Adding it back to the
    // measured residual reconstructs the underlying disturbance (PE + drift),
    // which is what the model must learn — the raw residual is the controller's
    // own already-corrected output.
    private double _appliedCum;

    public PredictiveAlgorithm(double minMove = 0.15, double aggression = 0.70,
                               double hysteresis = 0.10, double wormPeriodSec = 0.0,
                               int windowSamples = 256, double predBlend = 0.7) {
        _minMove = Math.Max(0.0, minMove);
        _aggression = Math.Clamp(aggression, 0.0, 2.0);
        _window = Math.Clamp(windowSamples, 32, 4096);
        _predBlend = Math.Clamp(predBlend, 0.0, 1.0);
        _manualPeriodSec = Math.Max(0.0, wormPeriodSec);
        _baseline = new HysteresisAlgorithm(hysteresis, aggression, minMove);
    }

    public string Name => "predictive";

    /// <summary>Predicted error (pixels) for the next frame from the last call;
    /// 0 during warm-up. Surfaced for the predicted-curve chart overlay.</summary>
    public double LastPredictedError { get; private set; }

    /// <summary>Estimated worm period in seconds (auto mode); 0 until estimated.</summary>
    public double EstimatedPeriodSec => _estPeriodSec;

    // Interface requires the no-dt overload; assume the last known / fallback cadence.
    public double Result(double input) => Result(input, _lastDt > 0 ? _lastDt : 2.0);

    public double Result(double input, double dtSeconds) {
        double dt = (dtSeconds > 0 && !double.IsNaN(dtSeconds)) ? dtSeconds
                  : (_lastDt > 0 ? _lastDt : 2.0);
        _lastDt = dt;
        _tAccum += dt;

        // Reconstruct the disturbance from the residual + what we've commanded.
        double disturbance = input + _appliedCum;
        _buf.Add((_tAccum, disturbance));
        while (_buf.Count > _window) _buf.RemoveAt(0);

        // Reactive feedback term (drives the residual to zero); always advanced so
        // its state stays warm.
        double baseCorr = _baseline.Result(input);

        if (_buf.Count < MinSamples) {
            LastPredictedError = 0.0;
            _appliedCum += baseCorr;
            return baseCorr;
        }

        double tNext = _tAccum + dt;

        // --- Drift: least-squares line over the reconstructed disturbance ---
        var (slope, intercept) = LinearFit();

        // --- Periodic: fit a sinusoid to the detrended disturbance ---
        double a = 0, b = 0, w = 0;
        double period = _manualPeriodSec > 0 ? _manualPeriodSec : EstimatePeriod(slope, intercept);
        if (period > 0) {
            if (_manualPeriodSec <= 0)
                _estPeriodSec = _estPeriodSec > 0 ? 0.8 * _estPeriodSec + 0.2 * period : period;
            double usePeriod = _manualPeriodSec > 0 ? _manualPeriodSec : _estPeriodSec;
            w = 2.0 * Math.PI / usePeriod;
            var (fa, fb, reduction) = FitSinusoid(slope, intercept, w);
            if (reduction >= PeConfidence) { a = fa; b = fb; }
        }

        // Disturbance model D(τ) = drift + periodic. Feed forward the predicted
        // *change* over the next frame (bias-robust: any offset in the
        // reconstructed disturbance cancels in the difference).
        double Model(double tau) => intercept + slope * tau
            + (w > 0 ? a * Math.Cos(w * tau) + b * Math.Sin(w * tau) : 0.0);
        double delta = Model(tNext) - Model(_tAccum);
        if (double.IsNaN(delta) || double.IsInfinity(delta)) delta = 0.0;

        // Expected next residual if we did nothing this frame (for the overlay).
        LastPredictedError = Model(tNext) - _appliedCum;
        if (double.IsNaN(LastPredictedError) || double.IsInfinity(LastPredictedError))
            LastPredictedError = 0.0;

        double corr = baseCorr + _predBlend * delta;

        // Runaway guard.
        double cap = Math.Abs(input) + Math.Abs(delta) + _minMove;
        if (Math.Abs(corr) > cap) corr = Math.Sign(corr) * cap;

        _appliedCum += corr;
        return corr;
    }

    public void Reset() {
        _buf.Clear();
        _baseline.Reset();
        _tAccum = 0.0;
        _lastDt = 0.0;
        _estPeriodSec = 0.0;
        _appliedCum = 0.0;
        LastPredictedError = 0.0;
    }

    // ----- math helpers -----

    private (double slope, double intercept) LinearFit() {
        int n = _buf.Count;
        double sx = 0, sy = 0, sxy = 0, sxx = 0;
        foreach (var (t, e) in _buf) { sx += t; sy += e; sxy += t * e; sxx += t * t; }
        double denom = n * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-12) return (0.0, sy / n);
        double slope = (n * sxy - sx * sy) / denom;
        double intercept = (sy - slope * sx) / n;
        return (slope, intercept);
    }

    /// <summary>Least-squares fit of res(t) ≈ a·cos(ωt) + b·sin(ωt) over the
    /// detrended buffer. Returns (a, b, fractionOfVarianceExplained).</summary>
    private (double a, double b, double reduction) FitSinusoid(double slope, double intercept, double w) {
        int n = _buf.Count;
        double scc = 0, sss = 0, scs = 0, src = 0, srs = 0, var0 = 0;
        foreach (var (t, e) in _buf) {
            double res = e - (intercept + slope * t);
            double c = Math.Cos(w * t), s = Math.Sin(w * t);
            scc += c * c; sss += s * s; scs += c * s;
            src += res * c; srs += res * s;
            var0 += res * res;
        }
        double det = scc * sss - scs * scs;
        if (Math.Abs(det) < 1e-9 || var0 < 1e-12) return (0, 0, 0);
        double a = (src * sss - srs * scs) / det;
        double b = (srs * scc - src * scs) / det;
        // Residual variance after removing the sinusoid.
        double resid = 0;
        foreach (var (t, e) in _buf) {
            double r = (e - (intercept + slope * t)) - (a * Math.Cos(w * t) + b * Math.Sin(w * t));
            resid += r * r;
        }
        double reduction = 1.0 - resid / var0;
        return (a, b, reduction);
    }

    /// <summary>Pick the worm period that best explains the detrended residual,
    /// scanning a bounded set of candidate periods (log-spaced). Returns 0 when no
    /// candidate clears the confidence bar.</summary>
    private double EstimatePeriod(double slope, double intercept) {
        double span = _tAccum - _buf[0].t;
        if (span <= 0) return 0;
        double pMin = Math.Max(4.0 * _lastDt, 20.0);     // need a few samples/cycle
        double pMax = Math.Min(800.0, 0.9 * span);        // need ~1 cycle in window
        if (pMax <= pMin) return 0;

        const int candidates = 48;
        double best = 0, bestRed = PeConfidence;
        double logMin = Math.Log(pMin), logMax = Math.Log(pMax);
        for (int i = 0; i < candidates; i++) {
            double p = Math.Exp(logMin + (logMax - logMin) * i / (candidates - 1));
            var (_, _, red) = FitSinusoid(slope, intercept, 2.0 * Math.PI / p);
            if (red > bestRed) { bestRed = red; best = p; }
        }
        return best;
    }
}
