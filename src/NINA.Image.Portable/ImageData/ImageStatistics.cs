// Copyright (C) 2016-2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This file is derived from N.I.N.A. - Nighttime Imaging 'N' Astronomy.
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

// Copyright (C) 2016-2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This file is derived from N.I.N.A. - Nighttime Imaging 'N' Astronomy.
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

using System.Collections.Concurrent;
using System.Threading.Tasks;
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
    /// Convenience overload for callers that have just the pixel data
    /// (e.g. the WASM client-side stacker which never wraps the buffer
    /// in IImageData). Computes median + MAD internally via the same
    /// histogram passes that <see cref="Create"/> uses, then delegates
    /// to the main <see cref="ComputeBackgroundSnr(ushort[], double, double)"/>
    /// overload. Two 65536-int histograms allocated, ~0.5 MB transient,
    /// negligible for the per-frame live-stack path.
    /// </summary>
    public static double ComputeBackgroundSnrFromData(ushort[] data) {
        if (data == null || data.Length == 0) return 0;
        var median = ComputeMedianViaHistogram(data);
        var mad = ComputeMAD(data, median);
        return ComputeBackgroundSnr(data, median, mad);
    }

    /// <summary>
    /// Background SNR plus the median (a robust sky-background proxy, ADU) and
    /// the peak pixel — computed together so the live path can derive the
    /// sky-background rate (for the sub-exposure advisor) and a saturation
    /// ceiling without a redundant median pass. The median histogram is already
    /// built for the SNR; only the max adds a light memory-bound scan.
    /// </summary>
    public static (double snr, double median, ushort max) ComputeBackgroundSnrWithStats(ushort[] data) {
        if (data == null || data.Length == 0) return (0, 0, 0);
        var median = ComputeMedianViaHistogram(data);
        var mad = ComputeMAD(data, median);
        var snr = ComputeBackgroundSnr(data, median, mad);
        ushort max = 0;
        for (int i = 0; i < data.Length; i++) { var v = data[i]; if (v > max) max = v; }
        return (snr, median, max);
    }

    /// <summary>
    /// Plain arithmetic mean of the pixel values. Used for the live-view
    /// "Mean" readout. A single memory-bound pass, parallelized in local
    /// partition sums so it's cheap enough to run on every live-stack frame
    /// even on a Pi (much lighter than the median/MAD SNR pass above).
    /// </summary>
    public static double ComputeMean(ushort[] data) {
        if (data == null || data.Length == 0) return 0;
        long total = 0;
        object gate = new();
        System.Threading.Tasks.Parallel.ForEach(
            System.Collections.Concurrent.Partitioner.Create(0, data.Length),
            () => 0L,
            (range, _, local) => {
                for (int i = range.Item1; i < range.Item2; i++) local += data[i];
                return local;
            },
            local => { lock (gate) total += local; });
        return (double)total / data.Length;
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
        // BENCH-PERF: parallel partition-local histogram, merged once.
        // Counts are order-independent so the result is identical to the
        // old serial scan; on the per-frame live-stack path this is one
        // of three full-frame passes, so fanning it out matters.
        var histogram = BuildHistogramParallel(data);
        long half = data.Length / 2;
        long cumulative = 0;
        for (int i = 0; i < histogram.Length; i++) {
            cumulative += histogram[i];
            if (cumulative > half) return i;
        }
        return 0;
    }

    private static double ComputeMAD(ushort[] data, double median) {
        // BENCH-PERF: build the |v - median| histogram directly in a
        // parallel pass. The old code first allocated a full ushort[N]
        // deviations array (e.g. 32 MB for a 16 MP frame) and made an
        // extra pass to fill it; counting in-place removes that
        // allocation and pass while producing the identical histogram.
        int med = (int)median;
        var histogram = new int[65536];
        var hl = new object();
        // MEMOPT: partition-local bins are rented, not allocated — a fresh
        // int[65536] is 256 KB (LOH) PER PARTITION per pass, and this runs
        // several times per live-stack frame; on a many-core host that was
        // tens of MB of per-frame churn.
        Parallel.ForEach(Partitioner.Create(0, data.Length), RentClearedHistogram,
            (range, _, bins) => {
                for (int i = range.Item1; i < range.Item2; i++) {
                    int d = Math.Abs(data[i] - med);
                    if (d < 65536) bins[d]++;
                }
                return bins;
            },
            bins => {
                lock (hl) { for (int b = 0; b < 65536; b++) histogram[b] += bins[b]; }
                System.Buffers.ArrayPool<int>.Shared.Return(bins);
            });

        long half = data.Length / 2;
        long cumulative = 0;
        for (int i = 0; i < histogram.Length; i++) {
            cumulative += histogram[i];
            if (cumulative > half) return i;
        }
        return 0;
    }

    /// <summary>Parallel partition-local value histogram (0..65535).
    /// Merged once; identical to a serial count. MEMOPT: partition-local
    /// bins come from the shared pool (see ComputeMAD note); only the
    /// merged result, which escapes to the caller, is allocated.</summary>
    private static int[] BuildHistogramParallel(ushort[] data) {
        var histogram = new int[65536];
        var hl = new object();
        Parallel.ForEach(Partitioner.Create(0, data.Length), RentClearedHistogram,
            (range, _, bins) => {
                for (int i = range.Item1; i < range.Item2; i++) bins[data[i]]++;
                return bins;
            },
            bins => {
                lock (hl) { for (int b = 0; b < 65536; b++) histogram[b] += bins[b]; }
                System.Buffers.ArrayPool<int>.Shared.Return(bins);
            });
        return histogram;
    }

    /// <summary>Rent a 65536-bin histogram from the shared pool, cleared.
    /// The pool may hand back a longer array; every consumer indexes
    /// strictly below 65536 and merges exactly 65536 bins, so the extra
    /// tail is inert.</summary>
    internal static int[] RentClearedHistogram() {
        var bins = System.Buffers.ArrayPool<int>.Shared.Rent(65536);
        Array.Clear(bins, 0, 65536);
        return bins;
    }
}