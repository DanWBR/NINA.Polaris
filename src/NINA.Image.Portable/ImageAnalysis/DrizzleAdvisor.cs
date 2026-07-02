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

using System.Globalization;

namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Recommends a drizzle scale from the data's sampling. Drizzle at scale &gt; 1
/// only recovers real resolution when the data is <b>undersampled</b> (stars
/// span fewer than ~2 px FWHM); on well/over-sampled data it just amplifies
/// noise and enlarges the file. The decision is driven by the median star FWHM
/// in pixels (a self-contained, focal-length-independent sampling measure),
/// with a secondary check on the number of subs (drizzle &gt; 1 needs many
/// well-dithered subs to fill the finer grid without coverage holes).
///
/// Pure decision logic (no image IO) so it is unit-tested directly; the caller
/// supplies the measured median FWHM + sub count.
/// </summary>
public static class DrizzleAdvisor {
    public record Advice(double FwhmPx, int SubCount, int RecommendedScale, string Reason);

    /// <summary>
    /// FWHM (px) from the half-flux radius. For a Gaussian PSF, ~2×HFR is the
    /// standard practical proxy used for sampling decisions.
    /// </summary>
    public static double FwhmFromHfr(double hfr) => 2.0 * hfr;

    /// <summary>Undersampling threshold (px FWHM). Below this, Nyquist is not
    /// satisfied and drizzle 2× recovers detail.</summary>
    public const double UndersampledFwhm = 2.0;

    /// <summary>Above this the data is comfortably sampled and drizzle &gt; 1
    /// adds no real resolution.</summary>
    public const double WellSampledFwhm = 2.6;

    public static Advice Recommend(double medianFwhmPx, int subCount) {
        string f = medianFwhmPx.ToString("0.0", CultureInfo.InvariantCulture);
        if (medianFwhmPx <= 0) {
            return new Advice(medianFwhmPx, subCount, 1,
                "Could not measure star FWHM; defaulting to 1x (no drizzle).");
        }
        if (medianFwhmPx < UndersampledFwhm) {
            if (subCount < 20) {
                return new Advice(medianFwhmPx, subCount, 2,
                    $"Undersampled (FWHM {f} px) so drizzle 2x recovers real detail, but only " +
                    $"{subCount} subs may leave coverage holes/noise - 30+ well-dithered subs " +
                    $"give the best result.");
            }
            return new Advice(medianFwhmPx, subCount, 2,
                $"Undersampled (FWHM {f} px, {subCount} subs): drizzle 2x recommended - it recovers " +
                $"detail lost to undersampling.");
        }
        if (medianFwhmPx < WellSampledFwhm) {
            return new Advice(medianFwhmPx, subCount, 1,
                $"Borderline sampling (FWHM {f} px): 1x is the safe choice; 2x only helps with many " +
                $"well-dithered subs and mostly enlarges the file.");
        }
        return new Advice(medianFwhmPx, subCount, 1,
            $"Well sampled (FWHM {f} px): drizzle above 1x won't add real resolution, only noise and " +
            $"file size - use 1x.");
    }
}
