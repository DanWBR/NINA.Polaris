namespace NINA.Polaris.Services;

/// <summary>
/// Estimate how many more frames / how many more seconds the live
/// stacker needs to reach a target SNR.
///
/// <para>Model: a noise-limited running-mean stack grows its SNR as
/// √N (signal accumulates linearly, noise as √N), so on a log/log
/// plot of (frameCount, cumulativeSnr) the relationship should look
/// like a straight line with slope 0.5:
/// <code>
///   log(snr) = log(k) + 0.5·log(N)
/// </code>
/// </para>
///
/// <para>We fit log(snr) against log(N) by ordinary least squares.
/// We don't pin the slope at 0.5 — letting it float compensates for
/// (a) frames the stacker rejects mid-session (alignment fail,
/// guiding hiccup), (b) seeing degrading the per-frame SNR, and
/// (c) the user changing exposure / gain mid-stack. The R² of the
/// fit doubles as a confidence gate: weak fits return a null ETA
/// so the UI shows "—" instead of a fantasy number.</para>
///
/// <para>Pure functional helper — no state, fully testable.</para>
/// </summary>
public static class SnrEtaCalculator {
    /// <summary>Minimum samples before we even attempt a fit. With
    /// fewer than 3 points the regression is meaningless.</summary>
    public const int MinSamples = 3;

    /// <summary>R² floor below which we hide the ETA. 0.6 catches
    /// "warm-up" stacks where the first few frames are dominated by
    /// alignment / star-detector noise, and degenerate stacks where
    /// every frame keeps failing.</summary>
    public const double MinConfidence = 0.6;

    /// <summary>Hard cap on how far we'll project — past ~1000 frames
    /// the √N model breaks down anyway (atmosphere, drift, etc.) and
    /// returning "23,847 frames" is worse than returning null.</summary>
    public const int MaxProjection = 1000;

    public sealed record EtaResult(
        int RemainingFrames,
        double RemainingSeconds,
        double Confidence,
        /// <summary>Slope of the log-log fit. Ideal noise-limited
        /// stack = 0.5. Lower means the stack is gaining SNR slower
        /// than √N (something's eating into the integration); higher
        /// is suspicious (small-sample effect or wrong model).</summary>
        double Slope);

    /// <summary>
    /// Return null when the input is too short, the fit is too weak,
    /// the projection exceeds the cap, or the target is already
    /// achieved. Callers render "—" on null.
    /// </summary>
    public static EtaResult? Estimate(
            IReadOnlyList<(int frame, double snr)> samples,
            double targetSnr,
            double averageExposureSeconds) {
        if (samples == null || samples.Count < MinSamples) return null;
        if (targetSnr <= 0) return null;

        // Already there. The widget can render "✓ target reached"
        // separately; here we just say "no ETA needed".
        var last = samples[samples.Count - 1];
        if (last.snr >= targetSnr) {
            return new EtaResult(0, 0, 1.0, 0.5);
        }

        // log-log OLS. Skip frames where snr <= 0 because log(0) is
        // -infinity and would poison the fit; those usually come from
        // dropped / mis-aligned frames the WASM stacker reported as
        // snr=0.
        double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0, sumYY = 0;
        int n = 0;
        for (int i = 0; i < samples.Count; i++) {
            var (frame, snr) = samples[i];
            if (frame <= 0 || snr <= 0) continue;
            double x = Math.Log(frame);
            double y = Math.Log(snr);
            sumX += x; sumY += y;
            sumXX += x * x; sumYY += y * y;
            sumXY += x * y;
            n++;
        }
        if (n < MinSamples) return null;

        double meanX = sumX / n;
        double meanY = sumY / n;
        double varX = sumXX / n - meanX * meanX;
        if (varX < 1e-9) return null;   // all x identical → no fit
        double slope = (sumXY / n - meanX * meanY) / varX;
        double intercept = meanY - slope * meanX;
        // R² = 1 − SS_res/SS_tot. Equivalent closed form via
        // correlation coefficient squared. Guard against zero
        // y-variance (all snr identical — flat stack, no growth).
        double varY = sumYY / n - meanY * meanY;
        if (varY < 1e-9) return null;
        double r = (sumXY / n - meanX * meanY) / Math.Sqrt(varX * varY);
        double r2 = r * r;
        if (r2 < MinConfidence) return null;

        // Slope sanity check. Negative or zero slope means SNR is
        // flat or decreasing — fit doesn't extrapolate to anything
        // useful, and the user probably has a different problem
        // (clouds, focus drift) the LIVE chart is more honest about.
        if (slope <= 0.05) return null;

        // Solve targetSnr = exp(intercept) · N^slope for N.
        // N_target = exp((log(target) − intercept) / slope)
        double nTarget = Math.Exp((Math.Log(targetSnr) - intercept) / slope);
        int nTargetInt = (int)Math.Ceiling(nTarget);
        int remaining = nTargetInt - last.frame;
        if (remaining <= 0) {
            return new EtaResult(0, 0, r2, slope);
        }
        if (remaining > MaxProjection) return null;

        double remainingSec = remaining * Math.Max(0.001, averageExposureSeconds);
        return new EtaResult(remaining, remainingSec, r2, slope);
    }
}
