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

namespace NINA.Image.ImageAnalysis;

public static class AutoStretch {
    // GX-12: defaults aligned with GraXpert's "15% Bg, 3 sigma" preset.
    // Empirically gives a nicer dark-grey background that doesn't crush
    // the faint structure of nebulae/galaxies while still presenting
    // pleasant star contrast. Old PixInsight-ish "0.25 / 2.8" defaults
    // produced thumbnails that looked muddy on most masters; users had
    // to manually re-stretch every preview.
    public const double DefaultTargetBg = 0.15;   // GraXpert: bg
    public const double DefaultSigma    = 3.0;    // GraXpert: sigma

    /// <summary>
    /// Auto-stretch using GraXpert's "15% Bg, 3 sigma" algorithm,
    /// sigma-clipped median + MAD on non-saturated samples, MTF mapping
    /// median to a 15% target background. Drop-in default for the
    /// FILES / STUDIO previews.
    /// </summary>
    public static byte[] Apply(ushort[] data, int width, int height, int bitDepth = 16) {
        var p = ComputeAutoStretchParams(data, width, height, bitDepth);
        return ApplyManual(data, width, height, p.Black, p.Mid, p.White, bitDepth);
    }

    /// <summary>
    /// Stretch tuned for the native guider's guide-camera preview.
    ///
    /// The DSO auto-stretch above lifts the background to a 15% grey so faint
    /// nebulosity shows; on a guide frame that just amplifies the sensor noise
    /// floor to a grainy grey wash (the symptom: native guide preview looks far
    /// noisier than PHD2's of the same camera). PHD2 instead keeps the
    /// background DARK and only lets stars stand out. We mirror that by placing
    /// the black point ABOVE the background median (median + k*MAD), so the
    /// noise floor clips to black, and mapping the brightest pixel to white.
    /// </summary>
    public static byte[] ApplyGuide(ushort[] data, int width, int height, int bitDepth = 16) {
        var p = ComputeGuideStretchParams(data, width, height, bitDepth);
        return ApplyManual(data, width, height, p.Black, p.Mid, p.White, bitDepth);
    }

    /// <summary>
    /// PHD2-faithful guide auto-stretch. Mirrors PHD2's
    /// <c>usImage::CalcStats</c> + <c>buildGammaLookupTable</c>: a 3x3 median
    /// filter gives robust black/white points (FiltMin/FiltMax) that ignore
    /// hot/cold single pixels, then a gamma map between them
    /// (<c>((v-min)/(max-min))^gamma</c>). gamma=1.0 reproduces PHD2's default
    /// (linear). Unlike <see cref="ApplyGuide"/> this keeps the real sky
    /// background visible instead of crushing it to black, and — like PHD2 —
    /// applies the LUT to the raw pixels, so the median filter only robustifies
    /// the levels, not the displayed image.
    /// </summary>
    public static byte[] ApplyGuidePhd2(ushort[] data, int width, int height,
                                        int bitDepth = 16, double gamma = 1.0) {
        int pixelCount = width * height;
        var result = new byte[pixelCount];
        if (data.Length < pixelCount || width < 3 || height < 3)
            return ApplyGuide(data, width, height, bitDepth);   // too small for a 3x3 median

        // FiltMin/FiltMax = min/max of the 3x3 median-filtered image (interior
        // pixels). Computed without materialising the filtered frame — we only
        // need the extremes. Parallel per-row reduction.
        int filtMin = 65535, filtMax = 0;
        object lk = new object();
        Parallel.For(1, height - 1, () => (lo: 65535, hi: 0), (y, _, local) => {
            var w9 = new ushort[9];
            int lo = local.lo, hi = local.hi;
            int row = y * width;
            for (int x = 1; x < width - 1; x++) {
                int idx = row + x, up = idx - width, dn = idx + width;
                w9[0] = data[up - 1]; w9[1] = data[up]; w9[2] = data[up + 1];
                w9[3] = data[idx - 1]; w9[4] = data[idx]; w9[5] = data[idx + 1];
                w9[6] = data[dn - 1]; w9[7] = data[dn]; w9[8] = data[dn + 1];
                ushort m = Median9(w9);
                if (m < lo) lo = m;
                if (m > hi) hi = m;
            }
            return (lo, hi);
        }, local => { lock (lk) { if (local.lo < filtMin) filtMin = local.lo; if (local.hi > filtMax) filtMax = local.hi; } });

        if (filtMax <= filtMin) filtMax = Math.Min(65535, filtMin + 1);

        // PHD2 buildGammaLookupTable(blevel=filtMin, wlevel=filtMax, power=gamma).
        var lut = new byte[65536];
        double range = filtMax - filtMin;
        for (int i = 0; i <= filtMin; i++) lut[i] = 0;
        for (int i = filtMin + 1; i < filtMax; i++) {
            double d = (i - filtMin) / range;
            lut[i] = (byte)Math.Clamp(Math.Pow(d, gamma) * 255.0, 0, 255);
        }
        for (int i = filtMax; i < 65536; i++) lut[i] = 255;

        int n = Math.Min(data.Length, pixelCount);
        Parallel.ForEach(Partitioner.Create(0, n), rg => {
            for (int i = rg.Item1; i < rg.Item2; i++) result[i] = lut[data[i]];
        });
        return result;
    }

    // Median of 9 (in-place insertion sort, return the middle element).
    private static ushort Median9(ushort[] a) {
        for (int i = 1; i < 9; i++) {
            ushort key = a[i];
            int j = i - 1;
            while (j >= 0 && a[j] > key) { a[j + 1] = a[j]; j--; }
            a[j + 1] = key;
        }
        return a[4];
    }

    /// <summary>
    /// Compute the guide-preview stretch (see <see cref="ApplyGuide"/>).
    /// <paramref name="blackSigma"/> is how far above the background median (in
    /// MADs) the black point sits — higher crushes more noise to black.
    /// <paramref name="midtone"/> &lt; 0.5 lifts the surviving star signal a
    /// little so dim guide stars stay visible. A single serial pass is fine
    /// here: guide frames are small and this runs roughly once per exposure.
    /// </summary>
    public static StretchParams ComputeGuideStretchParams(
            ushort[] data, int width, int height, int bitDepth = 16,
            double blackSigma = 2.0, double midtone = 0.35) {
        int pixelCount = width * height;
        if (data.Length == 0) return new StretchParams(0, 0.5, 1);
        int n = Math.Min(data.Length, pixelCount);
        double maxVal = (1 << bitDepth) - 1;

        // Histogram over non-zero samples (ignore dead/border pixels) +
        // the observed maximum (the brightest star → white point).
        var hist = new int[65536];
        long count = 0;
        ushort observedMax = 0;
        for (int i = 0; i < n; i++) {
            ushort v = data[i];
            if (v > observedMax) observedMax = v;
            if (v == 0) continue;
            hist[v]++;
            count++;
        }
        if (count == 0) return new StretchParams(0, 0.5, 1);

        long half = count / 2;
        long cum = 0;
        double median = 0;
        for (int i = 0; i < hist.Length; i++) {
            cum += hist[i];
            if (cum > half) { median = i; break; }
        }

        // MAD: median of |v - median| over the same sample.
        var dev = new int[65536];
        double median0 = median;
        for (int i = 0; i < n; i++) {
            ushort v = data[i];
            if (v == 0) continue;
            int d = (int)Math.Abs(v - median0);
            if (d < 65536) dev[d]++;
        }
        cum = 0;
        double mad = 0;
        for (int i = 0; i < dev.Length; i++) {
            cum += dev[i];
            if (cum > half) { mad = i; break; }
        }

        // White point: use a high percentile of the histogram rather than
        // the single brightest sample. Uncooled guide cameras almost always
        // have a few hot / amp-glow pixels pinned near saturation; anchoring
        // the white point on the absolute max (observedMax) lets one such
        // pixel pull white to ~1.0, which crushes every real star down toward
        // black -- the "native guide preview is all black, stars barely show
        // at any brightness/contrast or even in auto, although guiding works"
        // field report (star *detection* runs on the raw pixels, so it is
        // unaffected; only this preview render was). The genuine bright stars
        // still clip to white, which is what we want; the hot pixels just clip
        // alongside them instead of defining the scale.
        long whiteTarget = (long)(count * 0.995);
        long cumW = 0;
        int percentileMax = observedMax;
        for (int i = 0; i < hist.Length; i++) {
            cumW += hist[i];
            if (cumW >= whiteTarget) { percentileMax = i; break; }
        }
        // Never let the percentile collapse onto the background (uniform /
        // near-empty fields): keep at least a small span above black.
        double black = Math.Clamp((median + blackSigma * mad) / maxVal, 0.0, 1.0);
        double white = Math.Clamp(percentileMax / maxVal, black + 1e-3, 1.0);
        return new StretchParams(black, Math.Clamp(midtone, 0.001, 0.999), white);
    }

    /// <summary>
    /// Apply an explicit MTF stretch with caller-chosen black / mid / white
    /// points (each normalised 0..1). Used by the STUDIO viewer so slider
    /// drags don't require re-computing stats every frame.
    ///
    /// midtone is the midtone *balance* (target normalised value the
    /// midpoint maps to). 0.5 = linear; &lt;0.5 stretches shadows (typical
    /// for DSO); &gt;0.5 compresses shadows.
    /// </summary>
    public static byte[] ApplyManual(ushort[] data, int width, int height,
                                     double black, double mid, double white, int bitDepth = 16) {
        int pixelCount = width * height;
        var result = new byte[pixelCount];
        if (data.Length == 0) return result;

        black = Math.Clamp(black, 0.0, 1.0);
        white = Math.Clamp(white, 0.0, 1.0);
        if (white <= black) white = Math.Min(1.0, black + 1e-6);
        mid = Math.Clamp(mid, 0.001, 0.999);

        double maxVal = (1 << bitDepth) - 1;
        var lut = new byte[65536];
        for (int i = 0; i < 65536; i++) {
            double normalized = i / maxVal;
            double clipped = Math.Clamp((normalized - black) / (white - black), 0, 1);
            double stretched = MTF(clipped, mid);
            lut[i] = (byte)(stretched * 255);
        }

        // BENCH-PERF: the LUT apply is a pure per-pixel map (each output
        // cell depends only on its own input), so it fans out across cores.
        // For an RGB preview this runs three times over the full frame, so
        // it is one of the hottest pixel loops in the encode path.
        int n = Math.Min(data.Length, pixelCount);
        Parallel.ForEach(Partitioner.Create(0, n), range => {
            for (int i = range.Item1; i < range.Item2; i++)
                result[i] = lut[data[i]];
        });
        return result;
    }

    /// <summary>
    /// Float variant of <see cref="ApplyManual"/> for producing a real
    /// (non-display) stretched image rather than an 8-bit preview: applies
    /// the same black/mid/white MTF but returns a normalised [0,1] float per
    /// pixel, preserving full precision. Operates over the whole input array
    /// (mono buffer or a single RGB plane slice), so the caller controls
    /// plane layout. Used by ImageBlendService so the saved FITS isn't
    /// crushed to 8 bits.
    /// </summary>
    public static float[] ApplyManualFloat(ushort[] data,
                                           double black, double mid, double white,
                                           int bitDepth = 16) {
        var result = new float[data.Length];
        if (data.Length == 0) return result;

        black = Math.Clamp(black, 0.0, 1.0);
        white = Math.Clamp(white, 0.0, 1.0);
        if (white <= black) white = Math.Min(1.0, black + 1e-6);
        mid = Math.Clamp(mid, 0.001, 0.999);

        double maxVal = (1 << bitDepth) - 1;
        double invRange = 1.0 / (white - black);
        Parallel.ForEach(Partitioner.Create(0, data.Length), range => {
            for (int i = range.Item1; i < range.Item2; i++) {
                double normalized = data[i] / maxVal;
                double clipped = Math.Clamp((normalized - black) * invRange, 0, 1);
                result[i] = (float)MTF(clipped, mid);
            }
        });
        return result;
    }

    /// <summary>
    /// Compute the auto-stretch parameters (black/mid/white, all normalised
    /// 0..1) without applying them. Used by the STUDIO viewer to seed
    /// sliders with sensible defaults before the user starts tweaking.
    ///
    /// GX-12: ports GraXpert's stretch.py algorithm (see
    /// <c>graxpert/stretch.py:calculate_mtf_stretch_parameters_for_channel</c>).
    /// Two material differences vs the prior PixInsight-style heuristic:
    ///
    ///   1. Saturated pixels (== 0 and == max) are excluded from the
    ///      median/MAD sample. Without this, an image with lots of
    ///      hot pixels or black borders skews the background estimate.
    ///   2. New defaults: <c>sigma=3</c> (was 2.8) and target
    ///      background = 15% (was 25%). The lower bg gives a darker,
    ///      higher-contrast preview that matches what GraXpert ships.
    ///
    /// Optional <paramref name="targetBg"/> + <paramref name="sigma"/>
    /// let callers pick a different preset
    /// (10% Bg 3σ / 20% Bg 3σ / 30% Bg 2σ are the other GraXpert
    /// shipped options).
    /// </summary>
    public static StretchParams ComputeAutoStretchParams(
            ushort[] data, int width, int height, int bitDepth = 16,
            double? targetBg = null, double? sigma = null) {
        int pixelCount = width * height;
        if (data.Length == 0) return new StretchParams(0, 0.5, 1);

        double bgArg    = Math.Clamp(targetBg ?? DefaultTargetBg, 0.01, 0.99);
        double sigmaArg = Math.Max(0.5, sigma ?? DefaultSigma);

        int maxVal16  = (1 << bitDepth) - 1;
        ushort topVal = (ushort)Math.Min(maxVal16, 65535);

        // First pass: find the actual maximum value present in the
        // image. The "saturation" threshold for histogram exclusion
        // is normally topVal, but drivers that pack an N-bit sensor
        // into a 16-bit buffer often cap below 65535 (a 10-bit ZWO
        // sensor shifted into the high 6 bits saturates at 65472,
        // a 14-bit CMOS at 65520, etc). Excluding only pixels at
        // EXACTLY topVal in those cases lets the real saturation
        // wall into the sample, forces shadow ≈ saturation, MAD ≈ 0,
        // and the whole frame gets mapped to BLACK.
        //
        // Heuristic: only treat observedMax as the saturation point
        // when it's basically at the top of the bit-depth range
        // (>= 99% of topVal). Saturated wall cases are very narrow:
        // 10-bit-in-16-bit ZWO sensors land at 65472/65535 = 99.9%,
        // 14-bit-in-16-bit CMOS at 65520 = 99.97%. ANY scene where
        // observedMax is below 99% means the brightest pixel is
        // legitimate signal that hasn't hit the sensor's full-well,
        // and excluding those pixels would narrow the sample, raise
        // the shadow point, and crush mid-tones to black —
        // visually shrinking the visible content. The first version
        // of this fix used 0.9 which was too loose: a bright daytime
        // preview with the object peaking around 60000 (91.5%)
        // triggered it and crushed the object's dim borders to
        // black, making the user see the image as "smaller".
        ushort observedMax = 0;
        int limit = Math.Min(data.Length, pixelCount);
        // BENCH-PERF: max-reduction over range partitions, merged once per
        // partition. Identical result to the serial scan, just fanned out.
        object maxLock = new object();
        Parallel.ForEach(Partitioner.Create(0, limit), () => (ushort)0,
            (range, _, local) => {
                for (int i = range.Item1; i < range.Item2; i++)
                    if (data[i] > local) local = data[i];
                return local;
            },
            local => { lock (maxLock) { if (local > observedMax) observedMax = local; } });
        ushort wallThreshold = (ushort)(topVal * 0.99);
        ushort satThreshold = (observedMax >= wallThreshold && observedMax < topVal)
            ? observedMax : topVal;

        // Histogram + median, restricted to NON-saturated samples
        // (drop 0 and anything at the OBSERVED saturation point).
        // Black borders from a crop / dead pixel rows shouldn't
        // bias the background; nor should saturated highlights.
        // BENCH-PERF: build the histogram with per-partition local bins
        // then merge once. Sums are order-independent so the merged
        // histogram (and sampleCount) match the serial version exactly.
        // MEMOPT: partition-local bins are rented from the shared pool
        // (256 KB LOH each; this runs on every relayed preview frame).
        var histogram = new int[65536];
        long sampleCount = 0;
        ushort satThreshold0 = satThreshold;
        object histLock = new object();
        Parallel.ForEach(Partitioner.Create(0, limit),
            () => (NINA.Image.ImageData.ImageStatistics.RentClearedHistogram(), 0L),
            (range, _, tl) => {
                var (bins, cnt) = tl;
                for (int i = range.Item1; i < range.Item2; i++) {
                    ushort v = data[i];
                    if (v == 0 || v >= satThreshold0) continue;
                    bins[v]++;
                    cnt++;
                }
                return (bins, cnt);
            },
            tl => {
                var (bins, cnt) = tl;
                lock (histLock) {
                    for (int b = 0; b < 65536; b++) histogram[b] += bins[b];
                    sampleCount += cnt;
                }
                System.Buffers.ArrayPool<int>.Shared.Return(bins);
            });
        if (sampleCount == 0) {
            // Uniformly saturated (or uniformly zero) image. Set
            // white = the observed brightness so overexposed frames
            // render WHITE (intuitive) instead of falling to the
            // shader's default which would map every pixel through
            // the (1, 0.5, 1) identity and underexpose the visible
            // result when satThreshold < maxVal.
            double white = observedMax > 0
                ? Math.Clamp((double)observedMax / topVal, 0.001, 1.0)
                : 1.0;
            return new StretchParams(0, 0.5, white);
        }

        long half = sampleCount / 2;
        long cumulative = 0;
        double median = 0;
        for (int i = 0; i < histogram.Length; i++) {
            cumulative += histogram[i];
            if (cumulative > half) {
                median = i;
                break;
            }
        }

        // MAD over the SAME restricted sample (matching the
        // satThreshold above — using topVal here would re-include
        // saturated pixels and pull MAD toward zero).
        var devHistogram = new int[65536];
        double median0 = median;
        object devLock = new object();
        Parallel.ForEach(Partitioner.Create(0, limit),
            NINA.Image.ImageData.ImageStatistics.RentClearedHistogram,
            (range, _, bins) => {
                for (int i = range.Item1; i < range.Item2; i++) {
                    ushort v = data[i];
                    if (v == 0 || v >= satThreshold0) continue;
                    int dev = (int)Math.Abs(v - median0);
                    if (dev < 65536) bins[dev]++;
                }
                return bins;
            },
            bins => {
                lock (devLock) {
                    for (int b = 0; b < 65536; b++) devHistogram[b] += bins[b];
                }
                System.Buffers.ArrayPool<int>.Shared.Return(bins);
            });
        cumulative = 0;
        double mad = 0;
        for (int i = 0; i < devHistogram.Length; i++) {
            cumulative += devHistogram[i];
            if (cumulative > half) {
                mad = i;
                break;
            }
        }

        double maxVal = (1 << bitDepth) - 1;
        double normalizedMedian = median / maxVal;
        double normalizedMAD    = mad / maxVal;
        // shadow_clipping = clamp(median - sigma * MAD, 0, 1)
        double shadow = Math.Clamp(
            normalizedMedian - sigmaArg * normalizedMAD, 0.0, 1.0);
        // midtone = MTF((median - shadow) / (1 - shadow), bg)
        double denom = Math.Max(1e-9, 1.0 - shadow);
        double midtone = MTF((normalizedMedian - shadow) / denom, bgArg);
        return new StretchParams(shadow, midtone, 1.0);
    }

    public record StretchParams(double Black, double Mid, double White);

    private static double MTF(double x, double midtone) {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        if (midtone <= 0) return 1;
        if (midtone >= 1) return 0;
        return (midtone - 1.0) * x / ((2.0 * midtone - 1.0) * x - midtone);
    }
}