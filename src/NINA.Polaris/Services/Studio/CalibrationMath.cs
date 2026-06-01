using NINA.Image.ImageData;

namespace NINA.Polaris.Services.Studio;

/// <summary>
/// Pure-function calibration helpers, factored out of
/// <see cref="CalibrationService"/> so per-frame consumers (notably
/// <c>LiveStackPreProcessor</c>, LSPP-2) can apply the same math
/// against in-memory ushort[] buffers without spinning up a batch
/// job. Algorithmic behavior identical to the original inline code
/// in <c>CalibrationService.CalibrateOne</c> (LSPP-1 refactor) --
/// existing batch flow remains byte-identical after the move.
///
/// The pipeline is:
///   calibrated = (light - dark) / normalised_flat
///   where normalised_flat = flat_corrected / mean(flat_corrected)
///         flat_corrected   = master_flat - (master_dark_flat ?? master_bias)
///
/// Bias is only subtracted directly when there's no dark; darks
/// already contain the bias signal, so subtracting both
/// double-counts. The helper enforces this at call time -- callers
/// pass bias OR dark, not both.
/// </summary>
public static class CalibrationMath {
    /// <summary>Public surface of the parallel pixel loop. Allocates
    /// a fresh ushort[] same size as <paramref name="light"/> and
    /// returns the calibrated copy; the input buffer is never
    /// mutated (callers downstream still need the raw frame for
    /// other purposes, e.g. live preview before calibration).
    ///
    /// Throws InvalidOperationException if any of dark/bias/flat
    /// don't match the light's pixel count -- caller must validate
    /// dimensions OR catch and fall back to the raw frame.</summary>
    public static ushort[] CalibratePixels(
            ushort[] light,
            ushort[]? dark,
            ushort[]? bias,
            (double[] norm, double mean)? flat) {
        if (light == null) throw new ArgumentNullException(nameof(light));
        if (dark != null && dark.Length != light.Length)
            throw new InvalidOperationException("Master dark dimensions don't match light.");
        if (bias != null && bias.Length != light.Length)
            throw new InvalidOperationException("Master bias dimensions don't match light.");
        if (flat.HasValue && flat.Value.norm.Length != light.Length)
            throw new InvalidOperationException("Master flat dimensions don't match light.");

        var pixels = new ushort[light.Length];
        // Local copies so the lambda doesn't capture nullable structs each iteration.
        var darkPx = dark;
        var biasPx = (dark == null) ? bias : null;   // dark wins over bias
        var hasFlat = flat.HasValue;
        var flatNorm = hasFlat ? flat!.Value.norm : null;
        Parallel.For(0, pixels.Length, idx => {
            double v = light[idx];
            if (darkPx != null) v -= darkPx[idx];
            else if (biasPx != null) v -= biasPx[idx];
            if (hasFlat) {
                var n = flatNorm![idx];
                if (n > 1e-6) v /= n;
            }
            pixels[idx] = (ushort)Math.Clamp(Math.Round(v), 0, 65535);
        });
        return pixels;
    }

    /// <summary>Build the normalised flat: subtract a bias/dark-flat
    /// calibrator if available, divide by mean. Returns a per-pixel
    /// double[] (precision matters for the division) plus the mean
    /// for diagnostics. Caller caches the result -- it's expensive
    /// to recompute and identical across all lights of the same
    /// (filter, gain).</summary>
    public static (double[] norm, double mean) NormalizeFlat(BaseImageData flat, BaseImageData? cal) {
        var n = flat.Data.Length;
        var corrected = new double[n];
        double sum = 0;
        if (cal != null && cal.Data.Length == n) {
            for (int i = 0; i < n; i++) {
                var v = (double)flat.Data[i] - cal.Data[i];
                if (v < 0) v = 0;
                corrected[i] = v;
                sum += v;
            }
        } else {
            for (int i = 0; i < n; i++) {
                corrected[i] = flat.Data[i];
                sum += flat.Data[i];
            }
        }
        var mean = sum / n;
        if (mean < 1) mean = 1;   // pathological flat; avoid divide-by-zero
        for (int i = 0; i < n; i++) corrected[i] /= mean;
        return (corrected, mean);
    }

    /// <summary>Closest dark by exposure-time delta, gain must match
    /// exactly. Returns null when no dark of the right gain exists.
    /// Caller decides how to communicate "no match" (UI banner,
    /// skip calibration, etc).</summary>
    public static FrameRow? FindNearestDark(IReadOnlyList<FrameRow> darks, double exposure, int gain) {
        if (darks.Count == 0) return null;
        FrameRow? best = null;
        double bestDelta = double.MaxValue;
        foreach (var d in darks) {
            if (d.Gain != gain) continue;
            var delta = Math.Abs(d.ExposureSec - exposure);
            if (delta < bestDelta) { bestDelta = delta; best = d; }
        }
        return best;
    }

    /// <summary>Exact match on filter + gain. Flats are pickier than
    /// darks because the filter response shapes the result -- a
    /// "close enough" flat from the wrong filter does more harm
    /// than no flat at all.</summary>
    public static FrameRow? FindMatchingFlat(IReadOnlyList<FrameRow> flats, string filter, int gain) {
        if (flats.Count == 0) return null;
        return flats.FirstOrDefault(f =>
            f.Gain == gain &&
            string.Equals(f.Filter ?? "", filter ?? "", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Gain-only match. Bias frames don't depend on
    /// exposure or filter so the match space is much smaller.</summary>
    public static FrameRow? FindMatchingBias(IReadOnlyList<FrameRow> biases, int gain) {
        if (biases.Count == 0) return null;
        return biases.FirstOrDefault(b => b.Gain == gain);
    }
}
