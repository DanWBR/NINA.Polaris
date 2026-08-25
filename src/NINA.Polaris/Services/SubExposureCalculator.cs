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

namespace NINA.Polaris.Services;

/// <summary>
/// Recommend a sub-exposure length for the current sky, using the
/// "swamp the read noise" criterion popularised by Robin Glover
/// (SharpCap smart-histogram; "The Ideal Exposure", 2018).
///
/// <para>A single sub's noise variance is
/// <c>σ² = readNoise² + skyRate·t</c> (dark current ignored — negligible
/// for cooled sensors on typical LIVE subs). The sky shot-noise term grows
/// with exposure; once it dominates the fixed read-noise term, making subs
/// longer stops improving the stacked SNR and only risks saturation, star
/// bloat and lost frames. "Optimal" is the exposure at which the read noise
/// adds no more than a chosen small fraction <c>p</c> to the total noise:
/// <code>
///   (readNoise² + skyRate·t) / (skyRate·t) = (1 + p)²
///   ⇒ t = readNoise² / (skyRate · ((1+p)² − 1))
/// </code>
/// The default p = 5% gives the familiar "sky ≈ 10× read-noise variance"
/// rule of thumb (1/((1.05)²−1) ≈ 9.76).</para>
///
/// <para>Requires the electron-domain sensor constants (read noise in e-,
/// and the sky-background rate in e-/px/s, itself derived from the measured
/// background ADU and the conversion gain e-/ADU). Those come from a
/// <see cref="SensorAnalysisService"/> PTC run; without it the caller must
/// fall back to a flagged estimate. Pure functional helper — no state,
/// fully testable.</para>
/// </summary>
public static class SubExposureCalculator {
    /// <summary>Default allowed fractional noise increase from read noise (5%).</summary>
    public const double DefaultNoiseIncrease = 0.05;

    /// <summary>Sanity bounds on the recommendation (seconds). Below/above
    /// these the number is more misleading than helpful, so we clamp.</summary>
    public const double MinRecommendedSec = 0.5;
    public const double MaxRecommendedSec = 900.0;

    public sealed record SubExposureResult(
        /// <summary>Final recommendation (seconds): the optimal exposure,
        /// clamped to [Min,Max] and to the saturation cap when known.</summary>
        double RecommendedSeconds,
        /// <summary>Unclamped optimal from the swamp criterion (seconds).</summary>
        double OptimalSeconds,
        /// <summary>Exposure at which the brightest measured pixel reaches
        /// full well, when a peak rate and full well were supplied; null
        /// otherwise. The recommendation never exceeds this.</summary>
        double? SaturationCapSeconds,
        /// <summary>True when the recommendation was pulled down by the
        /// saturation cap rather than the swamp criterion.</summary>
        bool SaturationLimited);

    /// <summary>
    /// Compute the recommended sub length.
    /// </summary>
    /// <param name="readNoiseE">Read noise, electrons (from Sensor Analysis).</param>
    /// <param name="skyRateEPerSec">Sky background rate, e-/px/s (measured
    /// background ADU above bias × e-/ADU ÷ current exposure).</param>
    /// <param name="allowedNoiseIncrease">Fractional extra noise from read
    /// noise the operator tolerates (default 5%).</param>
    /// <param name="fullWellE">Sensor full-well capacity, electrons; pass with
    /// <paramref name="peakRateEPerSec"/> to cap against saturation. Optional.</param>
    /// <param name="peakRateEPerSec">Brightest measured pixel's rate, e-/px/s.
    /// Optional.</param>
    /// <returns>Null when the inputs are non-physical (non-positive read noise
    /// or sky rate): the caller renders "—" / falls back to an estimate.</returns>
    public static SubExposureResult? Recommend(
            double readNoiseE,
            double skyRateEPerSec,
            double allowedNoiseIncrease = DefaultNoiseIncrease,
            double? fullWellE = null,
            double? peakRateEPerSec = null) {
        if (!(readNoiseE > 0) || !double.IsFinite(readNoiseE)) return null;
        if (!(skyRateEPerSec > 0) || !double.IsFinite(skyRateEPerSec)) return null;
        double p = allowedNoiseIncrease > 0 && double.IsFinite(allowedNoiseIncrease)
            ? allowedNoiseIncrease : DefaultNoiseIncrease;

        // t = readNoise² / (skyRate · ((1+p)² − 1))
        double denom = skyRateEPerSec * (Math.Pow(1.0 + p, 2) - 1.0);
        if (!(denom > 0)) return null;
        double optimal = (readNoiseE * readNoiseE) / denom;

        // Saturation cap: the brightest pixel must stay below full well.
        double? satCap = null;
        if (fullWellE is > 0 && peakRateEPerSec is > 0) {
            satCap = fullWellE.Value / peakRateEPerSec.Value;
        }

        double rec = optimal;
        bool satLimited = false;
        if (satCap is > 0 && satCap.Value < rec) {
            rec = satCap.Value;
            satLimited = true;
        }
        rec = Math.Clamp(rec, MinRecommendedSec, MaxRecommendedSec);

        return new SubExposureResult(rec, optimal, satCap, satLimited);
    }

    /// <summary>
    /// Convert a measured background level to a sky rate in e-/px/s.
    /// <c>skyRate = max(0, backgroundAdu − biasAdu) · electronsPerAdu / exposureSec</c>.
    /// Returns 0 when the exposure is non-positive.
    /// </summary>
    public static double SkyRateEPerSec(double backgroundAdu, double biasAdu,
                                        double electronsPerAdu, double exposureSec) {
        if (!(exposureSec > 0)) return 0;
        double aboveBias = Math.Max(0, backgroundAdu - biasAdu);
        return aboveBias * electronsPerAdu / exposureSec;
    }
}
