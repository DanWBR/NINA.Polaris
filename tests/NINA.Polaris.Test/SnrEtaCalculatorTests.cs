using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class SnrEtaCalculatorTests {
    [Test]
    public void Estimate_NotEnoughSamples_ReturnsNull() {
        var samples = new List<(int, double)> { (1, 5.0), (2, 7.0) };
        Assert.That(SnrEtaCalculator.Estimate(samples, 50.0, 30.0), Is.Null);
    }

    [Test]
    public void Estimate_PerfectSqrtN_HighConfidence() {
        // Build a perfect √N curve: snr = 10 * sqrt(N) for N=1..10.
        // Slope on log-log = 0.5, intercept = log(10).
        var samples = new List<(int, double)>();
        for (int n = 1; n <= 10; n++) samples.Add((n, 10.0 * Math.Sqrt(n)));
        // Target 50 → N = (50/10)^2 = 25. At N=10 already, so 15 more.
        // Math.Ceiling on the log-log fit's reconstructed N can flip
        // a perfect 25.0 to 25.000…1 → ceiling 26 → remaining 16,
        // so allow ±1 tolerance on the count (and the converted
        // seconds, which is just 15..16 × 30 s).
        var eta = SnrEtaCalculator.Estimate(samples, 50.0, 30.0);
        Assert.That(eta, Is.Not.Null);
        Assert.That(eta!.Confidence, Is.GreaterThan(0.99));
        Assert.That(eta.RemainingFrames, Is.EqualTo(15).Within(1));
        Assert.That(eta.RemainingSeconds, Is.EqualTo(450.0).Within(31.0));
        // Slope close to the ideal 0.5
        Assert.That(eta.Slope, Is.EqualTo(0.5).Within(0.01));
    }

    [Test]
    public void Estimate_TargetAlreadyReached_ReturnsZero() {
        var samples = new List<(int, double)>();
        for (int n = 1; n <= 10; n++) samples.Add((n, 10.0 * Math.Sqrt(n)));
        // Target 20 — at N=10 snr ≈ 31.6, already past 20.
        var eta = SnrEtaCalculator.Estimate(samples, 20.0, 30.0);
        Assert.That(eta, Is.Not.Null);
        Assert.That(eta!.RemainingFrames, Is.EqualTo(0));
        Assert.That(eta.RemainingSeconds, Is.EqualTo(0));
    }

    [Test]
    public void Estimate_FlatStack_ReturnsNull() {
        // SNR didn't grow at all between frames — slope ≈ 0, fail
        // the slope sanity check. Could be clouds rolled in, focus
        // drifted, target framing changed — UI shows "—" instead
        // of an infinite ETA.
        var samples = new List<(int, double)>();
        for (int n = 1; n <= 8; n++) samples.Add((n, 12.0));
        Assert.That(SnrEtaCalculator.Estimate(samples, 50.0, 30.0), Is.Null);
    }

    [Test]
    public void Estimate_NoTarget_ReturnsNull() {
        var samples = new List<(int, double)>();
        for (int n = 1; n <= 10; n++) samples.Add((n, 10.0 * Math.Sqrt(n)));
        Assert.That(SnrEtaCalculator.Estimate(samples, 0, 30), Is.Null);
        Assert.That(SnrEtaCalculator.Estimate(samples, -10, 30), Is.Null);
    }

    [Test]
    public void Estimate_OutOfReach_ReturnsNull() {
        // Slow growth (slope=0.5, intercept giving snr=2 at N=1) → to
        // hit 200 we'd need (200/2)^2 = 10 000 frames, way past the
        // 1000-frame cap.
        var samples = new List<(int, double)>();
        for (int n = 1; n <= 10; n++) samples.Add((n, 2.0 * Math.Sqrt(n)));
        Assert.That(SnrEtaCalculator.Estimate(samples, 200.0, 30.0), Is.Null);
    }
}
