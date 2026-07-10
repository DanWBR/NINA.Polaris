// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details. You should have received a copy of the license along with
// this program. If not, see <https://www.gnu.org/licenses/>.

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
    /// <summary>Public surface of the parallel pixel loop. Writes the
    /// calibrated copy into <paramref name="dest"/> when supplied
    /// (MEMOPT: the live stacker reuses one session scratch instead
    /// of allocating ~18 MB per frame), otherwise allocates a fresh
    /// ushort[] same size as <paramref name="light"/>. The input
    /// buffer is never mutated (callers downstream still need the raw
    /// frame for other purposes, e.g. live preview before
    /// calibration); a dest that aliases the light is ignored and a
    /// fresh array is used instead.
    ///
    /// Throws InvalidOperationException if any of dark/bias/flat
    /// don't match the light's pixel count -- caller must validate
    /// dimensions OR catch and fall back to the raw frame.</summary>
    public static ushort[] CalibratePixels(
            ushort[] light,
            ushort[]? dark,
            ushort[]? bias,
            (float[] norm, double mean)? flat,
            ushort[]? dest = null) {
        if (light == null) throw new ArgumentNullException(nameof(light));
        if (dark != null && dark.Length != light.Length)
            throw new InvalidOperationException("Master dark dimensions don't match light.");
        if (bias != null && bias.Length != light.Length)
            throw new InvalidOperationException("Master bias dimensions don't match light.");
        if (flat.HasValue && flat.Value.norm.Length != light.Length)
            throw new InvalidOperationException("Master flat dimensions don't match light.");

        var pixels = (dest != null && dest.Length == light.Length && !ReferenceEquals(dest, light))
            ? dest
            : new ushort[light.Length];
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
    /// calibrator if available, divide by mean. The per-pixel result
    /// is stored as float[] — MEMOPT: it's the largest master buffer
    /// cached for a whole session (69 MB as double[] on a 9 MP
    /// sensor, half that as float[]) and a normalised flat lives in
    /// [~0.5, ~2.0] where float's 24-bit mantissa is ~1e-7 relative
    /// error, far below photon noise. The mean and the subtraction
    /// are still computed in double. Caller caches the result -- it's
    /// expensive to recompute and identical across all lights of the
    /// same (filter, gain).</summary>
    public static (float[] norm, double mean) NormalizeFlat(BaseImageData flat, BaseImageData? cal) {
        var n = flat.Data.Length;
        var corrected = new float[n];
        double sum = 0;
        if (cal != null && cal.Data.Length == n) {
            for (int i = 0; i < n; i++) {
                var v = (double)flat.Data[i] - cal.Data[i];
                if (v < 0) v = 0;
                corrected[i] = (float)v;
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
        for (int i = 0; i < n; i++) corrected[i] = (float)(corrected[i] / mean);
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