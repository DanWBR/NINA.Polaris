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
/// What the predictive algorithm learned about a mount's periodic error, in the
/// algorithm's own units (pixels). The worm period and the shape of the error are
/// mechanical properties, so they are worth keeping between sessions; the caller
/// persists this and hands it back through the constructor.
///
/// <para><see cref="Coefficients"/> holds the harmonic pairs [a1, b1, a2, b2, ...]
/// of a·cos(kωt) + b·sin(kωt). Their ABSOLUTE phase is meaningless in a later
/// session (t restarts, and the worm is wherever it happens to be), which is why
/// a restored model is re-phased against the first samples rather than used
/// as-is. The amplitudes and the period are what carry over.</para>
/// </summary>
public sealed record PredictiveModel(double PeriodSec, double[] Coefficients, double Quality) {
    public bool IsUsable => PeriodSec > 0
        && Coefficients is { Length: >= 2 }
        && Coefficients.Length % 2 == 0
        && Coefficients.All(c => !double.IsNaN(c) && !double.IsInfinity(c))
        && Quality > 0;
}

/// <summary>
/// Predictive guide algorithm: models the measured error as a slow linear
/// <b>drift</b> plus the <b>periodic error</b> of the worm gear, then feeds
/// forward a correction that cancels the error predicted for the <i>next</i>
/// frame instead of only chasing the current one. Inspired by PHD2's Predictive
/// PEC, but self-contained and tuned for the native guider.
///
/// <para>The periodic term is a harmonic series (ω, 2ω, 3ω), not a single
/// sinusoid: real worm PE is not a clean sine, and the second and third
/// harmonics are often a good part of its amplitude. Harmonics stay linear in
/// their coefficients, so this is still plain least squares with no training
/// step, no model file and no new dependency. How many harmonics to use is
/// decided per fit by adjusted R², so a mount whose error IS a clean sine does
/// not get two extra terms fitted to its noise.</para>
///
/// <para>It always blends with a reactive baseline (hysteresis) and falls back to
/// that baseline during warm-up or when the periodic fit is not confident, so it
/// never guides worse than the reactive default. Most valuable on the RA axis
/// (PE-dominant); on Dec the drift term still helps.</para>
/// </summary>
public sealed class PredictiveAlgorithm : IGuideAlgorithm {
    // Minimum samples before the full fit kicks in (pure reactive until then,
    // unless a stored model lets the shortcut below run earlier).
    private const int MinSamples = 16;
    // Minimum samples before a RESTORED model can be re-phased. Fewer unknowns
    // (one time shift plus an offset) than the full fit, so it needs far less
    // data and far less of the cycle.
    private const int MinPriorSamples = 6;
    // A periodic fit must explain at least this fraction of the detrended
    // variance to be trusted; otherwise only drift is fed forward.
    private const double PeConfidence = 0.15;
    // Re-phasing a restored model is held to a stricter bar than the full fit,
    // and to a minimum arc of the cycle. A handful of samples covering 2% of a
    // worm turn is nearly a straight line: several shifts fit it about equally
    // well, and the one the scan happens to pick can be half a cycle out, which
    // feeds forward a correction pointing the wrong way. Waiting for a real arc
    // costs a few frames of reactive guiding and removes that failure.
    private const double PriorConfidence = 0.50;
    private const double MinPriorSpanFraction = 0.08;
    // Samples required per fitted harmonic coefficient. The harmonic count grows
    // with the buffer rather than being fixed, so an 18-sample buffer fits one
    // harmonic and a 40-sample buffer fits three.
    private const int SamplesPerParam = 4;
    // Candidate time shifts tried when re-phasing a restored model. 96 steps
    // over one worm period is ~5 s of shift resolution on a 480 s worm, well
    // under the per-frame prediction it feeds.
    private const int ShiftCandidates = 96;

    private readonly double _minMove;
    private readonly double _aggression;
    private readonly int _window;
    private readonly double _predBlend;      // 0..1 feed-forward weight
    private readonly double _manualPeriodSec; // 0 = auto-estimate
    private readonly int _maxHarmonics;
    private readonly PredictiveModel? _prior;
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
    // Last fit that cleared the confidence bar, for ExportModel.
    private double[]? _goodCoef;
    private double _goodPeriodSec;
    private double _goodQuality;

    public PredictiveAlgorithm(double minMove = 0.15, double aggression = 0.70,
                               double hysteresis = 0.10, double wormPeriodSec = 0.0,
                               int windowSamples = 256, double predBlend = 0.7,
                               int maxHarmonics = 3, PredictiveModel? prior = null) {
        _minMove = Math.Max(0.0, minMove);
        _aggression = Math.Clamp(aggression, 0.0, 2.0);
        _window = Math.Clamp(windowSamples, 32, 4096);
        _predBlend = Math.Clamp(predBlend, 0.0, 1.0);
        _manualPeriodSec = Math.Max(0.0, wormPeriodSec);
        _maxHarmonics = Math.Clamp(maxHarmonics, 1, 4);
        _prior = prior is { IsUsable: true } ? prior : null;
        _baseline = new HysteresisAlgorithm(hysteresis, aggression, minMove);
    }

    public string Name => "predictive";

    /// <summary>Predicted error (pixels) for the next frame from the last call;
    /// 0 during warm-up. Surfaced for the predicted-curve chart overlay.</summary>
    public double LastPredictedError { get; private set; }

    /// <summary>Estimated worm period in seconds (auto mode); 0 until estimated.</summary>
    public double EstimatedPeriodSec => _estPeriodSec;

    /// <summary>Harmonics in the last accepted periodic fit (0 = none yet, or the
    /// fit was not confident). Observability: a mount stuck at 1 has either a
    /// clean sine or too little history for more.</summary>
    public int ChosenHarmonics { get; private set; }

    /// <summary>True while predictions come from a RESTORED model that has only
    /// been re-phased, before enough samples exist for the full fit.</summary>
    public bool UsingPriorModel { get; private set; }

    /// <summary>The learned periodic model, or null while no fit has cleared the
    /// confidence bar. Persist it per rig and pass it back as the constructor's
    /// <c>prior</c> next session.</summary>
    public PredictiveModel? ExportModel() =>
        _goodCoef != null && _goodPeriodSec > 0
            ? new PredictiveModel(_goodPeriodSec, (double[])_goodCoef.Clone(), _goodQuality)
            : null;

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

        double tNext = _tAccum + dt;
        // Once the buffer is long enough, the full fit takes over: measured
        // against the re-phased shape it predicted better, because it also
        // tracks tonight's drift and tonight's amplitude instead of last
        // night's. The shortcut's job is the window before that.
        Func<double, double>? model = _buf.Count >= MinSamples
            ? BuildFullModel()
            : BuildPriorModel();

        if (model == null) {
            LastPredictedError = 0.0;
            _appliedCum += baseCorr;
            return baseCorr;
        }

        // Feed forward the predicted *change* over the next frame (bias-robust:
        // any offset in the reconstructed disturbance cancels in the difference).
        double delta = model(tNext) - model(_tAccum);
        if (double.IsNaN(delta) || double.IsInfinity(delta)) delta = 0.0;

        // Expected next residual if we did nothing this frame (for the overlay).
        LastPredictedError = model(tNext) - _appliedCum;
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
        ChosenHarmonics = 0;
        UsingPriorModel = false;
        // _goodCoef survives on purpose: Reset happens on a dither or a settings
        // change mid-session, and throwing away a good model there would cost
        // another warm-up for no reason. A new session builds a new instance.
    }

    // ----- model construction -----

    /// <summary>Drift + harmonic fit over the whole buffer. Always returns a
    /// model once there are enough samples: at minimum the drift line, which is
    /// worth feeding forward on its own.</summary>
    private Func<double, double>? BuildFullModel() {
        UsingPriorModel = false;
        var (slope, intercept) = LinearFit();

        double period = WorkingPeriod(slope, intercept);
        double[]? coef = null;
        double w = 0;
        if (period > 0) {
            w = 2.0 * Math.PI / period;
            coef = FitBestHarmonics(slope, intercept, w, out double quality, out int harmonics);
            ChosenHarmonics = coef != null ? harmonics : 0;
            if (coef != null) {
                _goodCoef = coef;
                _goodPeriodSec = period;
                _goodQuality = quality;
            }
        } else {
            ChosenHarmonics = 0;
        }

        double wLocal = w;
        double[]? c = coef;
        return tau => intercept + slope * tau
                    + (c != null ? Harmonic(c, wLocal, tau) : 0.0);
    }

    /// <summary>Restored-model shortcut for the warm-up window: the shape and the
    /// period are known, so the only free parameters are a time shift and a
    /// constant offset. That is identifiable from a short arc of the cycle,
    /// whereas the full fit needs enough span to separate several harmonics from
    /// the drift, which on a 480 s worm at 2 s frames is minutes of guiding.
    ///
    /// <para>The offset absorbs where the star happens to sit; it cancels in the
    /// per-frame difference the caller feeds forward.</para></summary>
    private Func<double, double>? BuildPriorModel() {
        UsingPriorModel = false;
        if (_prior == null || _buf.Count < MinPriorSamples) return null;
        double period = _manualPeriodSec > 0 ? _manualPeriodSec : _prior.PeriodSec;
        if (period <= 0) return null;
        double span = _tAccum - _buf[0].t;
        if (span < MinPriorSpanFraction * period) return null;

        double w = 2.0 * Math.PI / period;
        var (shift, offset, reduction) = FitPriorShift(w, period, _prior.Coefficients);
        if (reduction < PriorConfidence) return null;

        UsingPriorModel = true;
        ChosenHarmonics = _prior.Coefficients.Length / 2;
        double[] c = _prior.Coefficients;
        return tau => offset + Harmonic(c, w, tau - shift);
    }

    /// <summary>Period to fit at: an explicit rig setting wins, then a restored
    /// model (the worm period is a mechanical constant, so re-deriving it every
    /// session buys nothing), then the estimator.</summary>
    private double WorkingPeriod(double slope, double intercept) {
        if (_manualPeriodSec > 0) return _manualPeriodSec;
        if (_prior != null) {
            _estPeriodSec = _prior.PeriodSec;
            return _prior.PeriodSec;
        }
        double period = EstimatePeriod(slope, intercept);
        if (period > 0) {
            _estPeriodSec = _estPeriodSec > 0 ? 0.8 * _estPeriodSec + 0.2 * period : period;
        }
        return _estPeriodSec;
    }

    // ----- math helpers -----

    /// <summary>Sum of the harmonic series a_k·cos(kωτ) + b_k·sin(kωτ).</summary>
    private static double Harmonic(double[] coef, double w, double tau) {
        double sum = 0;
        for (int k = 0; k < coef.Length / 2; k++) {
            double kw = (k + 1) * w * tau;
            sum += coef[2 * k] * Math.Cos(kw) + coef[2 * k + 1] * Math.Sin(kw);
        }
        return sum;
    }

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

    /// <summary>Fits 1..MaxHarmonics harmonics at ω and keeps the count with the
    /// best ADJUSTED R². Plain R² can only improve when parameters are added, so
    /// selecting on it would always pick the largest series and hand three
    /// harmonics' worth of freedom to what may be seeing noise. Returns null when
    /// even the best candidate fails the confidence bar.</summary>
    private double[]? FitBestHarmonics(double slope, double intercept, double w,
                                       out double quality, out int harmonics) {
        quality = 0;
        harmonics = 0;
        int n = _buf.Count;
        // Cap the series by the data on hand: two coefficients per harmonic,
        // SamplesPerParam samples each, and the two drift parameters already spent.
        int maxH = Math.Min(_maxHarmonics, (n - 2) / (2 * SamplesPerParam));
        if (maxH < 1) return null;

        double[]? best = null;
        double bestAdj = PeConfidence;
        for (int h = 1; h <= maxH; h++) {
            var coef = FitHarmonics(slope, intercept, w, h, out double r2);
            if (coef == null) continue;
            int p = 2 * h;
            if (n - p - 1 <= 0) continue;
            double adj = 1.0 - (1.0 - r2) * (n - 1.0) / (n - p - 1.0);
            if (adj > bestAdj) { bestAdj = adj; best = coef; harmonics = h; quality = adj; }
        }
        return best;
    }

    /// <summary>Least-squares fit of res(t) ≈ Σ a_k·cos(kωt) + b_k·sin(kωt) over
    /// the detrended buffer. Returns the coefficients and the fraction of the
    /// detrended variance they explain.</summary>
    private double[]? FitHarmonics(double slope, double intercept, double w, int harmonics,
                                   out double r2) {
        r2 = 0;
        int m = 2 * harmonics;
        var ata = new double[m, m];
        var atb = new double[m];
        var basis = new double[m];
        double var0 = 0;
        foreach (var (t, e) in _buf) {
            double res = e - (intercept + slope * t);
            for (int k = 0; k < harmonics; k++) {
                double kw = (k + 1) * w * t;
                basis[2 * k] = Math.Cos(kw);
                basis[2 * k + 1] = Math.Sin(kw);
            }
            for (int i = 0; i < m; i++) {
                atb[i] += basis[i] * res;
                for (int j = i; j < m; j++) ata[i, j] += basis[i] * basis[j];
            }
            var0 += res * res;
        }
        if (var0 < 1e-12) return null;
        // Mirror the symmetric half.
        for (int i = 0; i < m; i++)
            for (int j = 0; j < i; j++) ata[i, j] = ata[j, i];

        var coef = SolveSymmetric(ata, atb, m);
        if (coef == null) return null;

        double resid = 0;
        foreach (var (t, e) in _buf) {
            double r = (e - (intercept + slope * t)) - Harmonic(coef, w, t);
            resid += r * r;
        }
        r2 = 1.0 - resid / var0;
        return r2 > 0 ? coef : null;
    }

    /// <summary>Gaussian elimination with partial pivoting on the normal
    /// equations. At most 8x8 here, so the simple dense solve is the right
    /// trade; returns null when the system is singular (a period that makes two
    /// basis columns collinear over this buffer).</summary>
    private static double[]? SolveSymmetric(double[,] a, double[] b, int n) {
        var m = new double[n, n + 1];
        for (int i = 0; i < n; i++) {
            for (int j = 0; j < n; j++) m[i, j] = a[i, j];
            m[i, n] = b[i];
        }
        for (int col = 0; col < n; col++) {
            int piv = col;
            for (int r = col + 1; r < n; r++)
                if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
            if (Math.Abs(m[piv, col]) < 1e-12) return null;
            if (piv != col)
                for (int j = col; j <= n; j++) (m[col, j], m[piv, j]) = (m[piv, j], m[col, j]);
            for (int r = col + 1; r < n; r++) {
                double f = m[r, col] / m[col, col];
                if (f == 0) continue;
                for (int j = col; j <= n; j++) m[r, j] -= f * m[col, j];
            }
        }
        var x = new double[n];
        for (int i = n - 1; i >= 0; i--) {
            double s = m[i, n];
            for (int j = i + 1; j < n; j++) s -= m[i, j] * x[j];
            x[i] = s / m[i, i];
            if (double.IsNaN(x[i]) || double.IsInfinity(x[i])) return null;
        }
        return x;
    }

    /// <summary>Scans candidate time shifts of a known harmonic shape and keeps
    /// the best, with the constant offset that goes with it. Returns the fraction
    /// of the buffer's variance the shifted shape explains.
    ///
    /// <para>Only two free parameters on purpose. Letting a drift line float here
    /// as well was tried and measured WORSE: over the handful of frames this path
    /// covers, a straight line and a slow arc are nearly the same thing, so the
    /// extra freedom buys the fit nothing and costs it the very identifiability
    /// the shortcut depends on. Drift is picked up by the full fit a few frames
    /// later, where there is enough span to tell the two apart.</para></summary>
    private (double shift, double offset, double reduction) FitPriorShift(
            double w, double period, double[] coef) {
        int n = _buf.Count;
        double mean = 0;
        foreach (var (_, e) in _buf) mean += e;
        mean /= n;
        double var0 = 0;
        foreach (var (_, e) in _buf) var0 += (e - mean) * (e - mean);
        if (var0 < 1e-12) return (0, 0, 0);

        double bestSse = double.MaxValue, bestShift = 0, bestOffset = 0;
        for (int i = 0; i < ShiftCandidates; i++) {
            double shift = period * i / ShiftCandidates;
            double off = 0;
            foreach (var (t, e) in _buf) off += e - Harmonic(coef, w, t - shift);
            off /= n;
            double sse = 0;
            foreach (var (t, e) in _buf) {
                double r = e - (off + Harmonic(coef, w, t - shift));
                sse += r * r;
            }
            if (sse < bestSse) { bestSse = sse; bestShift = shift; bestOffset = off; }
        }
        return (bestShift, bestOffset, 1.0 - bestSse / var0);
    }

    /// <summary>Pick the worm period that best explains the detrended residual,
    /// scanning a bounded set of candidate periods (log-spaced). Returns 0 when no
    /// candidate clears the confidence bar.
    ///
    /// <para>The scan deliberately fits a SINGLE harmonic per candidate even
    /// though the model uses a series. A candidate at twice the true period has
    /// the true period as its own second harmonic, so scanning with harmonics
    /// would let the octave explain the data just as well while also spending
    /// unpenalised parameters — the estimator would drift to the wrong
    /// fundamental. One harmonic per candidate makes the fundamental, which
    /// carries most of the amplitude, win on its own.</para></summary>
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
            var coef = FitHarmonics(slope, intercept, 2.0 * Math.PI / p, 1, out double red);
            if (coef != null && red > bestRed) { bestRed = red; best = p; }
        }
        return best;
    }
}
