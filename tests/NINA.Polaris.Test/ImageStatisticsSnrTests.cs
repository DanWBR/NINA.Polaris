using NUnit.Framework;
using NINA.Image.ImageData;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the contract of <see cref="ImageStatistics.ComputeBackgroundSnr"/>
/// + the convenience overload <c>ComputeBackgroundSnrFromData</c>. Those
/// feed every SNR readout in the UI (PREVIEW bottom bar, LIVE stack
/// overlay, ETA calculator) and the WASM client-side stacker, so a
/// regression here would silently lie to the user about session quality.
///
/// Cases cover the degenerate frames that real captures throw at us
/// (uniform / dark / saturated / dropped) plus the happy path with a
/// known signal injected on top of a noisy background.
/// </summary>
[TestFixture]
public class ImageStatisticsSnrTests {

    [Test]
    public void UniformGrayFrame_ReturnsZero() {
        // Every pixel equal → no signal population above median+5MAD,
        // so SNR collapses to 0. UI shows "--" instead of a fantasy
        // number. MAD=0 here so the floor-of-1 inside the algorithm
        // is what keeps it from blowing up.
        var data = new ushort[256 * 256];
        for (int i = 0; i < data.Length; i++) data[i] = 1000;
        var snr = ImageStatistics.ComputeBackgroundSnrFromData(data);
        Assert.That(snr, Is.EqualTo(0).Within(0.01));
    }

    [Test]
    public void EmptyData_ReturnsZero() {
        Assert.That(ImageStatistics.ComputeBackgroundSnrFromData(Array.Empty<ushort>()),
            Is.EqualTo(0));
    }

    [Test]
    public void NullData_ReturnsZero() {
        // Defensive: WASM Interop holds nullable accumulator buffers
        // until the first frame lands, the inject path could plausibly
        // see null.
        Assert.That(ImageStatistics.ComputeBackgroundSnrFromData(null!),
            Is.EqualTo(0));
    }

    [Test]
    public void SaturatedFrame_ReturnsZeroNotInfinity() {
        // All pixels at the bit-depth ceiling (== maxVal) are excluded
        // from both populations. Result: 0, not NaN/Infinity that
        // would crash the ETA log-log fit downstream.
        var data = new ushort[256 * 256];
        for (int i = 0; i < data.Length; i++) data[i] = 65535;
        var snr = ImageStatistics.ComputeBackgroundSnrFromData(data);
        Assert.That(double.IsFinite(snr), Is.True);
        Assert.That(snr, Is.EqualTo(0));
    }

    [Test]
    public void NoisyBackgroundWithSignal_ReturnsHighSnr() {
        // Background = N(2000, 50) and signal = 5x5 stamp at 30000.
        // Should give SNR >> 50 (signal mean way above background mean,
        // background stdev small).
        const int w = 200, h = 200;
        var data = new ushort[w * h];
        var rng = new Random(42);
        for (int i = 0; i < data.Length; i++) {
            // Box-Muller-ish poor man's gaussian via central limit theorem
            double sum = 0;
            for (int j = 0; j < 6; j++) sum += rng.NextDouble();
            // sum in [0..6], mean 3, stdev sqrt(0.5) ≈ 0.707
            double val = 2000 + (sum - 3) * 70.7;   // mean 2000, stdev ~50
            data[i] = (ushort)Math.Clamp(val, 0, 65535);
        }
        // Inject a bright 10x10 stamp in the middle (signal).
        for (int y = 95; y < 105; y++)
            for (int x = 95; x < 105; x++)
                data[y * w + x] = 30000;

        var snr = ImageStatistics.ComputeBackgroundSnrFromData(data);
        Assert.That(snr, Is.GreaterThan(50), $"expected SNR > 50, got {snr:F1}");
    }

    [Test]
    public void DimSignalOverNoisyBackground_ReturnsLowerSnr() {
        // Same background noise but a much fainter signal stamp →
        // SNR strictly less than the bright-signal case. Sanity check
        // that the algorithm reflects signal strength, not just
        // presence-of-anything-above-threshold.
        const int w = 200, h = 200;
        var bright = MakeNoisyFrameWithStamp(w, h, signalValue: 30000, seed: 42);
        var dim = MakeNoisyFrameWithStamp(w, h, signalValue: 5000, seed: 42);

        var snrBright = ImageStatistics.ComputeBackgroundSnrFromData(bright);
        var snrDim = ImageStatistics.ComputeBackgroundSnrFromData(dim);

        Assert.That(snrBright, Is.GreaterThan(snrDim));
    }

    [Test]
    public void ZeroPixelsBorder_DoesNotPoisonBackground() {
        // Resampled frames have small zero borders from the affine
        // transform out-of-bounds fill. The algorithm excludes v==0
        // explicitly; a realistic ~5% border should still produce a
        // sane SNR in the same ballpark as the un-bordered version.
        //
        // Note: the limit here is the median-via-histogram pass — when
        // zeros dominate the histogram (large borders), the median
        // would shift to 0 and the algorithm loses meaning. Real
        // stacks have <5% border so this is OK in practice; the test
        // pins that small borders are tolerated.
        const int w = 200, h = 200;
        var clean = MakeNoisyFrameWithStamp(w, h, signalValue: 25000, seed: 7);
        var bordered = (ushort[])clean.Clone();
        // 5-pixel border = 5% of pixels zeroed out
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (x < 5 || x >= w - 5 || y < 5 || y >= h - 5)
                    bordered[y * w + x] = 0;

        var snrClean = ImageStatistics.ComputeBackgroundSnrFromData(clean);
        var snrBordered = ImageStatistics.ComputeBackgroundSnrFromData(bordered);

        Assert.That(double.IsFinite(snrBordered), Is.True);
        Assert.That(snrBordered, Is.GreaterThan(snrClean * 0.5));
        Assert.That(snrBordered, Is.LessThan(snrClean * 2.0));
    }

    [Test]
    public void ExplicitMedianAndMad_OverloadAgreesWithFromData() {
        // The two overloads must produce the same SNR when fed the
        // same median + MAD — guarantees ComputeBackgroundSnrFromData
        // is just a convenience wrapper, no algorithmic drift.
        var data = MakeNoisyFrameWithStamp(100, 100, signalValue: 20000, seed: 99);
        var snrFromData = ImageStatistics.ComputeBackgroundSnrFromData(data);

        // Recompute median + MAD via the same histogram path so we
        // can call the explicit overload with matching numbers.
        var hist = new int[65536];
        for (int i = 0; i < data.Length; i++) hist[data[i]]++;
        long half = data.Length / 2;
        long cum = 0;
        int median = 0;
        for (int i = 0; i < 65536; i++) {
            cum += hist[i];
            if (cum > half) { median = i; break; }
        }
        var devHist = new int[65536];
        for (int i = 0; i < data.Length; i++) {
            int d = Math.Abs(data[i] - median);
            if (d < devHist.Length) devHist[d]++;
        }
        cum = 0;
        int mad = 0;
        for (int i = 0; i < 65536; i++) {
            cum += devHist[i];
            if (cum > half) { mad = i; break; }
        }

        var snrExplicit = ImageStatistics.ComputeBackgroundSnr(data, median, mad);
        Assert.That(snrFromData, Is.EqualTo(snrExplicit).Within(0.001));
    }

    private static ushort[] MakeNoisyFrameWithStamp(int w, int h, int signalValue, int seed) {
        var data = new ushort[w * h];
        var rng = new Random(seed);
        for (int i = 0; i < data.Length; i++) {
            double sum = 0;
            for (int j = 0; j < 6; j++) sum += rng.NextDouble();
            double val = 2000 + (sum - 3) * 70.7;
            data[i] = (ushort)Math.Clamp(val, 0, 65535);
        }
        for (int y = h / 2 - 5; y < h / 2 + 5; y++)
            for (int x = w / 2 - 5; x < w / 2 + 5; x++)
                data[y * w + x] = (ushort)signalValue;
        return data;
    }
}
