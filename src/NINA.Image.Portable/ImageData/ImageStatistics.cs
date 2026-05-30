using NINA.Image.Interfaces;

namespace NINA.Image.ImageData;

public class ImageStatistics : IImageStatistics {
    public int Width { get; private set; }
    public int Height { get; private set; }
    public double Mean { get; private set; }
    public double Median { get; private set; }
    public double StDev { get; private set; }
    public double MAD { get; private set; }
    public int Min { get; private set; }
    public int Max { get; private set; }
    public long StarCount { get; set; }
    public double HFR { get; set; }
    /// <summary>
    /// Background signal-to-noise ratio:
    ///   SNR = (mean(signal) − mean(background)) / σ(background).
    /// Computed from the same pixel pass that fills mean/median/MAD,
    /// so the cost is one extra histogram-driven classification (no
    /// second full-image iteration). Saturated pixels (== maxVal) and
    /// zero pixels are excluded from both populations to avoid biasing
    /// the numerator with hot pixels or the borders.
    ///
    /// Returns 0 when there is no detectable signal (e.g. a flat dark
    /// frame or a dropped capture) so downstream UI can render "--"
    /// instead of a misleading number.
    /// </summary>
    public double SNR { get; set; }

    private ImageStatistics() { }

    public static ImageStatistics Create(IImageData imageData) {
        var data = imageData.Data;
        var props = imageData.Properties;
        var stats = new ImageStatistics {
            Width = props.Width,
            Height = props.Height
        };

        if (data.Length == 0) return stats;

        long sum = 0;
        int min = int.MaxValue;
        int max = int.MinValue;

        for (int i = 0; i < data.Length; i++) {
            int val = data[i];
            sum += val;
            if (val < min) min = val;
            if (val > max) max = val;
        }

        stats.Min = min;
        stats.Max = max;
        stats.Mean = (double)sum / data.Length;

        // Standard deviation
        double sumSqDiff = 0;
        for (int i = 0; i < data.Length; i++) {
            double diff = data[i] - stats.Mean;
            sumSqDiff += diff * diff;
        }
        stats.StDev = Math.Sqrt(sumSqDiff / data.Length);

        // Median via histogram (faster than sort for 16-bit data)
        stats.Median = ComputeMedianViaHistogram(data);

        // MAD (Median Absolute Deviation)
        stats.MAD = ComputeMAD(data, stats.Median);

        // Background-population SNR: classify pixels into a robust
        // background window (median ± 1·MAD, ≈50% central) and a
        // signal window (above median + 5·MAD, same threshold the
        // StarDetector uses). Cheap single pass.
        stats.SNR = ComputeBackgroundSnr(data, stats.Median, stats.MAD);

        return stats;
    }

    /// <summary>
    /// Background SNR. Two-pass single-iteration: pass 1 classifies
    /// pixels + accumulates background mean/M2 (Welford's algorithm
    /// for numerically stable stdev) and signal sum/count. SNR =
    /// (μ_signal − μ_bg) / σ_bg, with safe floors for the
    /// degenerate cases (no signal pixels, MAD ≈ 0, etc.).
    /// </summary>
    public static double ComputeBackgroundSnr(ushort[] data, double median, double mad) {
        if (data == null || data.Length == 0) return 0;
        // MAD floor protects against frames with a histogram spike
        // (all pixels in a single bucket — DSLR flat black, dropped
        // frame, simulator returning constant). Without it, a few
        // outliers blow up SNR to ridiculous values.
        var madFloored = Math.Max(1.0, mad);
        var bgLo = median - madFloored;
        var bgHi = median + madFloored;
        // 5σ-equivalent (since 1.4826·MAD ≈ σ for a gaussian → 5·MAD
        // is conservative pra evitar incluir borda de estrela no fundo)
        var signalThreshold = median + 5.0 * madFloored;
        // Anything at the bit-depth ceiling is saturated; pin
        // conservatively at 65535 so the check works for any
        // depth ≤ 16-bit. Anything == 0 is a likely black border or
        // dropped read.
        const int maxVal = 65535;

        long bgCount = 0;
        double bgMean = 0;
        double bgM2 = 0;   // sum of squared deviations (Welford)
        long sigCount = 0;
        double sigSum = 0;

        for (int i = 0; i < data.Length; i++) {
            int v = data[i];
            if (v == 0 || v >= maxVal) continue;
            if (v >= bgLo && v <= bgHi) {
                // Welford incremental: numerically stable for huge N
                bgCount++;
                double delta = v - bgMean;
                bgMean += delta / bgCount;
                bgM2 += delta * (v - bgMean);
            } else if (v >= signalThreshold) {
                sigCount++;
                sigSum += v;
            }
        }

        if (sigCount == 0 || bgCount == 0) return 0;
        var bgStdev = Math.Sqrt(bgM2 / bgCount);
        if (bgStdev < 1e-6) bgStdev = 1.0;   // pathological flat fundo
        var sigMean = sigSum / sigCount;
        var snr = (sigMean - bgMean) / bgStdev;
        // Guard against NaN / infinity creeping through (defensive).
        if (double.IsNaN(snr) || double.IsInfinity(snr)) return 0;
        return Math.Max(0, snr);
    }

    private static double ComputeMedianViaHistogram(ushort[] data) {
        var histogram = new int[65536];
        for (int i = 0; i < data.Length; i++) {
            histogram[data[i]]++;
        }

        long half = data.Length / 2;
        long cumulative = 0;
        for (int i = 0; i < histogram.Length; i++) {
            cumulative += histogram[i];
            if (cumulative > half) return i;
        }
        return 0;
    }

    private static double ComputeMAD(ushort[] data, double median) {
        var deviations = new ushort[data.Length];
        for (int i = 0; i < data.Length; i++) {
            deviations[i] = (ushort)Math.Abs(data[i] - median);
        }

        var histogram = new int[65536];
        for (int i = 0; i < deviations.Length; i++) {
            histogram[deviations[i]]++;
        }

        long half = deviations.Length / 2;
        long cumulative = 0;
        for (int i = 0; i < histogram.Length; i++) {
            cumulative += histogram[i];
            if (cumulative > half) return i;
        }
        return 0;
    }
}
