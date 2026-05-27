namespace NINA.Polaris.Services.Studio;

/// <summary>
/// Pure-math helpers for color calibration. Kept separate from
/// <see cref="ColorCalibrationService"/> so the formulas can be
/// unit-tested without touching the file I/O or job-tracking
/// machinery (and so PCC math added in CCALB-3 lives next to its
/// BG-neutral / manual cousins).
///
/// All inputs are plane-sequential ushort[] (R plane, then G, then B),
/// matching <see cref="NINA.Image.FileFormat.FITS.FITSReader"/> and
/// <see cref="NINA.Image.FileFormat.FITS.FITSWriter"/>.
/// </summary>
public static class ColorCalibrationMath {

    /// <summary>
    /// Per-channel offsets to subtract so that the sampled background
    /// region becomes neutral grey.
    ///
    /// Two sample modes:
    ///   "auto"  - lowest-luminance 5% of pixels across the frame.
    ///             Picks the darkest portion automatically; works on
    ///             any frame without user input.
    ///   "patch" - median of the user-supplied ROI. Use when the
    ///             auto-sample picks up unwanted content (very dim
    ///             nebula, vignette corner, ...).
    ///
    /// Two output semantics, picked via <paramref name="zeroBackground"/>:
    ///   false (BG-only mode): offsets bring all three channels to
    ///         <c>min(medians)</c>. Background becomes neutral grey
    ///         at the dimmest channel's original brightness; nothing
    ///         else in the frame changes brightness. This is what the
    ///         user expects from a "Neutralize background" button
    ///         that runs in isolation.
    ///   true  (Manual mode): offsets equal each channel's full
    ///         median. Background pushes to zero across the board so
    ///         the subsequent white-reference gain does not pull the
    ///         background back off-neutral. Mirrors how Siril chains
    ///         BG neutralisation into Color Calibration.
    /// </summary>
    public static double[] ComputeBgOffsets(ushort[] planeSeq, int w, int h,
            string sampleMode, ColorCalibrationService.PatchRoi? patch,
            bool zeroBackground = false) {
        int n = w * h;
        if (planeSeq.Length != n * 3) {
            throw new ArgumentException(
                $"ComputeBgOffsets expects plane-sequential RGB ushort[] of " +
                $"length {n * 3} (w*h*3), got {planeSeq.Length}.");
        }
        var medians = string.Equals(sampleMode, "patch", StringComparison.OrdinalIgnoreCase)
            ? MediansInPatch(planeSeq, w, h, ClampPatch(patch!, w, h))
            : MediansLowestLuminance(planeSeq, w, h, fraction: 0.05);

        if (zeroBackground) {
            return new[] { medians[0], medians[1], medians[2] };
        }
        double minMedian = Math.Min(medians[0], Math.Min(medians[1], medians[2]));
        return new[] {
            medians[0] - minMedian,
            medians[1] - minMedian,
            medians[2] - minMedian,
        };
    }

    /// <summary>
    /// Per-channel gains so that the mean of the white-reference
    /// patch becomes neutral after applying BG offsets first. Each
    /// gain is `max(mean) / mean[c]`, anchoring the brightest channel
    /// at 1.0 so we never amplify above the source dynamic range
    /// (clipping protects the highlights).
    ///
    /// The mean is computed AFTER subtracting the BG offsets that
    /// were already computed (otherwise the white-balance gain would
    /// have to fight the background pedestal).
    /// </summary>
    public static double[] ComputeWhiteGains(ushort[] planeSeq, int w, int h,
            ColorCalibrationService.PatchRoi whitePatch, double[] bgOffsets) {
        if (bgOffsets == null || bgOffsets.Length != 3) {
            throw new ArgumentException("bgOffsets must be a 3-element array.");
        }
        var p = ClampPatch(whitePatch, w, h);
        int n = w * h;

        // Per-channel running sum minus offset, then divide by count.
        double[] means = new double[3];
        long count = 0;
        for (int c = 0; c < 3; c++) {
            int baseIdx = c * n;
            double sum = 0;
            count = 0;
            for (int yy = p.Y; yy < p.Y + p.H; yy++) {
                int rowOff = yy * w;
                for (int xx = p.X; xx < p.X + p.W; xx++) {
                    double v = planeSeq[baseIdx + rowOff + xx] - bgOffsets[c];
                    if (v < 0) v = 0;
                    sum += v;
                    count++;
                }
            }
            means[c] = count > 0 ? sum / count : 0;
        }

        double maxMean = Math.Max(means[0], Math.Max(means[1], means[2]));
        // Protect against an all-black white patch (user picked the
        // wrong region); return identity gains rather than dividing
        // by zero. The service catches it via the offsets-only path.
        if (maxMean <= 0) return new double[] { 1, 1, 1 };
        return new[] {
            means[0] > 0 ? maxMean / means[0] : 1,
            means[1] > 0 ? maxMean / means[1] : 1,
            means[2] > 0 ? maxMean / means[2] : 1,
        };
    }

    // ── samplers ─────────────────────────────────────────────────────

    private static double[] MediansInPatch(ushort[] planeSeq, int w, int h,
            ColorCalibrationService.PatchRoi p) {
        int n = w * h;
        var medians = new double[3];
        for (int c = 0; c < 3; c++) {
            int baseIdx = c * n;
            var hist = new int[65536];
            long count = 0;
            for (int yy = p.Y; yy < p.Y + p.H; yy++) {
                int rowOff = yy * w;
                for (int xx = p.X; xx < p.X + p.W; xx++) {
                    hist[planeSeq[baseIdx + rowOff + xx]]++;
                    count++;
                }
            }
            medians[c] = HistogramMedian(hist, count);
        }
        return medians;
    }

    private static double[] MediansLowestLuminance(ushort[] planeSeq, int w, int h,
            double fraction) {
        // Two-pass: first pass collects luminance values into a
        // histogram so we can find the cutoff for the lowest
        // `fraction` of pixels without sorting. Second pass tallies
        // per-channel histograms for pixels below the cutoff. The
        // cumulative count keeps us O(n) on a Pi 2's worth of pixels
        // (24 Mpx fits in ~50 ms).
        int n = w * h;
        var lumHist = new int[65536];
        for (int i = 0; i < n; i++) {
            // Rec.709 luminance; clamp to ushort range.
            double lum = 0.2126 * planeSeq[i]
                       + 0.7152 * planeSeq[n + i]
                       + 0.0722 * planeSeq[2 * n + i];
            int v = (int)Math.Clamp(lum, 0, 65535);
            lumHist[v]++;
        }

        long target = (long)(n * fraction);
        if (target < 1) target = 1;
        long cum = 0;
        int cutoff = 0;
        for (int i = 0; i < 65536; i++) {
            cum += lumHist[i];
            if (cum >= target) { cutoff = i; break; }
        }

        // Second pass: per-channel histograms for pixels with
        // luminance <= cutoff.
        var rHist = new int[65536];
        var gHist = new int[65536];
        var bHist = new int[65536];
        long counted = 0;
        for (int i = 0; i < n; i++) {
            double lum = 0.2126 * planeSeq[i]
                       + 0.7152 * planeSeq[n + i]
                       + 0.0722 * planeSeq[2 * n + i];
            int v = (int)Math.Clamp(lum, 0, 65535);
            if (v > cutoff) continue;
            rHist[planeSeq[i]]++;
            gHist[planeSeq[n + i]]++;
            bHist[planeSeq[2 * n + i]]++;
            counted++;
        }
        // counted may overshoot `target` slightly (ties at the
        // cutoff bucket), which is fine for a median estimate.
        return new[] {
            HistogramMedian(rHist, counted),
            HistogramMedian(gHist, counted),
            HistogramMedian(bHist, counted),
        };
    }

    private static double HistogramMedian(int[] hist, long count) {
        if (count <= 0) return 0;
        long half = count / 2;
        long cum = 0;
        for (int i = 0; i < hist.Length; i++) {
            cum += hist[i];
            if (cum > half) return i;
        }
        return hist.Length - 1;
    }

    private static ColorCalibrationService.PatchRoi ClampPatch(
            ColorCalibrationService.PatchRoi p, int w, int h) {
        int x = Math.Clamp(p.X, 0, w - 1);
        int y = Math.Clamp(p.Y, 0, h - 1);
        int pw = Math.Clamp(p.W, 1, w - x);
        int ph = Math.Clamp(p.H, 1, h - y);
        return new ColorCalibrationService.PatchRoi(x, y, pw, ph);
    }
}
