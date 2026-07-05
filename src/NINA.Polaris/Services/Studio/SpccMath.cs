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

namespace NINA.Polaris.Services.Studio;

/// <summary>
/// SpectroPhotometric Color Calibration (SPCC) math — the spectral engine
/// that separates SPCC from the broadband PCC in
/// <see cref="ColorCalibrationMath"/>. Where PCC maps a star's catalog B-V
/// to expected channel flux ratios through a fixed empirical slope, SPCC
/// integrates an actual stellar <see cref="Spectrum"/> through the actual
/// total system response of each channel (filter transmission x sensor
/// quantum efficiency), the way PixInsight SPCC and Siril 1.2+ do.
///
/// Physical model (per colour channel c, for a photon-counting sensor):
///   Exp_c(star) = ∫ Fλ(λ) · Tfilter_c(λ) · QE(λ) · λ dλ
/// The extra λ converts the star's energy flux density Fλ into a photon
/// rate (photon energy = hc/λ), which is what the detector counts. Constant
/// factors (hc, aperture, exposure) cancel because SPCC only ever uses
/// ratios between channels.
///
/// Calibration solve, in two robust steps:
///   1. System throughput. Each measured star gives k_c = obs_c / Exp_c;
///      the per-channel unknown system scale is the MEDIAN over stars
///      (robust to mismatched/saturated stars).
///   2. White-balance gains. Integrate the chosen white-reference spectrum
///      through each channel (wExp_c); the system's response to a neutral
///      object is r_c = k_c · wExp_c. Per-channel gains that make that
///      object come out neutral, anchored at green:
///         g_R = r_G / r_R,   g_G = 1,   g_B = r_G / r_B.
///
/// Kept free of FITS/disk/catalog IO so it is unit-testable with reference
/// spectra and curves; <see cref="SpccService"/> owns star detection,
/// photometry, the catalog match, and building each star's spectrum from
/// the selected source (blackbody / Pickles / Gaia).
/// </summary>
public static class SpccMath {
    // Physical constants (SI, CODATA). Wavelengths are handled in nm below;
    // the nm→m conversion is folded into the Planck evaluation.
    public const double PlanckH = 6.62607015e-34;   // J·s
    public const double SpeedC  = 2.99792458e8;     // m/s
    public const double BoltzK  = 1.380649e-23;     // J/K

    /// <summary>A sampled spectrum: spectral flux density Fλ (relative
    /// units) on a strictly increasing wavelength grid in nanometres.</summary>
    public record Spectrum(double[] WavelengthNm, double[] Flux);

    /// <summary>A sampled response (throughput, 0..1) on an increasing
    /// wavelength grid in nanometres — a filter transmission or a sensor
    /// QE curve, or their product (a channel's total response).</summary>
    public record ResponseCurve(double[] WavelengthNm, double[] Response);

    /// <summary>One matched star for the gain solve: its background-
    /// subtracted observed channel fluxes and the spectrum SPCC believes it
    /// has (from its catalog colour or measured spectrophotometry).</summary>
    public record SpccStar(double ObsR, double ObsG, double ObsB, Spectrum Spectrum);

    // ── Blackbody + colour→temperature ───────────────────────────────────

    /// <summary>Planck spectral radiance Bλ(λ,T) per unit wavelength, λ in
    /// nm, T in kelvin. Units are W·m⁻³·sr⁻¹ but only the SHAPE matters here
    /// (SPCC uses ratios), so any consistent scaling is fine.</summary>
    public static double Planck(double wavelengthNm, double tempK) {
        if (wavelengthNm <= 0 || tempK <= 0) return 0;
        double lam = wavelengthNm * 1e-9;                  // nm → m
        double a = 2.0 * PlanckH * SpeedC * SpeedC / Math.Pow(lam, 5);
        double x = PlanckH * SpeedC / (lam * BoltzK * tempK);
        // Guard the exponential against overflow far out on the blue tail.
        if (x > 700) return 0;
        return a / (Math.Exp(x) - 1.0);
    }

    /// <summary>
    /// Effective temperature (K) from Johnson B-V, using the Ballesteros
    /// (2012) two-blackbody relation:
    ///   T = 4600 · (1/(0.92·BV + 1.7) + 1/(0.92·BV + 0.62)).
    /// Reference: F.J. Ballesteros, "New insights into black bodies",
    /// EPL 97 (2012) 34008, arXiv:1201.1809. Clamped to a sane stellar
    /// range so a bad catalog colour can't produce a degenerate spectrum.
    /// </summary>
    public static double TeffFromBv(double bv) {
        double t = 4600.0 * (1.0 / (0.92 * bv + 1.7) + 1.0 / (0.92 * bv + 0.62));
        return Math.Clamp(t, 1500.0, 40000.0);
    }

    /// <summary>Sample a blackbody of temperature <paramref name="tempK"/>
    /// onto <paramref name="gridNm"/> as a <see cref="Spectrum"/>.</summary>
    public static Spectrum BlackbodySpectrum(double tempK, double[] gridNm) {
        var flux = new double[gridNm.Length];
        for (int i = 0; i < gridNm.Length; i++) flux[i] = Planck(gridNm[i], tempK);
        return new Spectrum((double[])gridNm.Clone(), flux);
    }

    /// <summary>Blackbody spectrum for a star of a given B-V on the grid.</summary>
    public static Spectrum BlackbodyFromBv(double bv, double[] gridNm)
        => BlackbodySpectrum(TeffFromBv(bv), gridNm);

    // ── Curves + integration ─────────────────────────────────────────────

    /// <summary>Linear interpolation of (xs, ys) at x; 0 outside the grid.
    /// Assumes xs strictly increasing.</summary>
    public static double Interp(double[] xs, double[] ys, double x) {
        int n = xs.Length;
        if (n == 0 || x < xs[0] || x > xs[n - 1]) return 0;
        // Binary search for the bracketing interval.
        int lo = 0, hi = n - 1;
        while (hi - lo > 1) {
            int mid = (lo + hi) >> 1;
            if (xs[mid] <= x) lo = mid; else hi = mid;
        }
        double dx = xs[hi] - xs[lo];
        if (dx <= 0) return ys[lo];
        double t = (x - xs[lo]) / dx;
        return ys[lo] + t * (ys[hi] - ys[lo]);
    }

    /// <summary>Channel total response = filter transmission × sensor QE,
    /// evaluated on the filter's wavelength grid (the filter defines the
    /// bandpass; QE is interpolated onto it).</summary>
    public static ResponseCurve CombineResponse(ResponseCurve filter, ResponseCurve qe) {
        var grid = filter.WavelengthNm;
        var r = new double[grid.Length];
        for (int i = 0; i < grid.Length; i++)
            r[i] = filter.Response[i] * Interp(qe.WavelengthNm, qe.Response, grid[i]);
        return new ResponseCurve((double[])grid.Clone(), r);
    }

    /// <summary>
    /// Photon-weighted band integral ∫ Fλ(λ)·R(λ)·λ dλ (trapezoidal) over
    /// the channel response grid, with the star spectrum interpolated onto
    /// it. The λ weight makes this a photon count for a photon-counting
    /// detector. Returns 0 for a non-overlapping / empty band.
    /// </summary>
    public static double IntegrateChannel(Spectrum spec, ResponseCurve response) {
        var g = response.WavelengthNm;
        var r = response.Response;
        int n = g.Length;
        if (n < 2) return 0;
        double sum = 0;
        double prevLam = g[0];
        double prevVal = SafeFlux(spec, prevLam) * r[0] * prevLam;
        for (int i = 1; i < n; i++) {
            double lam = g[i];
            double val = SafeFlux(spec, lam) * r[i] * lam;
            sum += 0.5 * (prevVal + val) * (lam - prevLam);
            prevLam = lam;
            prevVal = val;
        }
        return sum;
    }

    private static double SafeFlux(Spectrum spec, double lam)
        => Interp(spec.WavelengthNm, spec.Flux, lam);

    // ── Gain solve ───────────────────────────────────────────────────────

    /// <summary>
    /// Per-channel system throughput k = {kR, kG, kB}: the MEDIAN over
    /// matched stars of obs_c / Exp_c, where Exp_c integrates the star's
    /// spectrum through channel c. Stars with a non-positive observed flux
    /// or expected integral in any channel are skipped.
    /// </summary>
    public static double[] ComputeThroughput(
            IReadOnlyList<SpccStar> stars,
            ResponseCurve respR, ResponseCurve respG, ResponseCurve respB) {
        if (stars == null || stars.Count == 0)
            throw new ArgumentException("SPCC needs at least 1 matched star.");
        var kR = new List<double>(stars.Count);
        var kG = new List<double>(stars.Count);
        var kB = new List<double>(stars.Count);
        foreach (var s in stars) {
            if (s.ObsR <= 0 || s.ObsG <= 0 || s.ObsB <= 0) continue;
            double eR = IntegrateChannel(s.Spectrum, respR);
            double eG = IntegrateChannel(s.Spectrum, respG);
            double eB = IntegrateChannel(s.Spectrum, respB);
            if (eR <= 0 || eG <= 0 || eB <= 0) continue;
            kR.Add(s.ObsR / eR);
            kG.Add(s.ObsG / eG);
            kB.Add(s.ObsB / eB);
        }
        if (kR.Count == 0)
            throw new InvalidOperationException(
                "SPCC: all matched stars rejected (zero flux or empty band overlap).");
        return new[] { Median(kR), Median(kG), Median(kB) };
    }

    /// <summary>
    /// White-balance gains {gR, gG, gB} (gG = 1) that make an object of the
    /// white-reference spectrum come out neutral, given the per-channel
    /// system throughput and the channel responses. See the class summary
    /// for the derivation.
    /// </summary>
    public static double[] ComputeGains(
            double[] throughput, Spectrum whiteRef,
            ResponseCurve respR, ResponseCurve respG, ResponseCurve respB) {
        double wR = IntegrateChannel(whiteRef, respR);
        double wG = IntegrateChannel(whiteRef, respG);
        double wB = IntegrateChannel(whiteRef, respB);
        double rR = throughput[0] * wR;
        double rG = throughput[1] * wG;
        double rB = throughput[2] * wB;
        if (rR <= 0 || rG <= 0 || rB <= 0)
            throw new InvalidOperationException(
                "SPCC: white reference has no overlap with a channel response.");
        return new[] { rG / rR, 1.0, rG / rB };
    }

    /// <summary>Convenience: throughput solve + white-balance gains in one
    /// call. Returns {gR, gG(=1), gB}.</summary>
    public static double[] Solve(
            IReadOnlyList<SpccStar> stars, Spectrum whiteRef,
            ResponseCurve respR, ResponseCurve respG, ResponseCurve respB) {
        var k = ComputeThroughput(stars, respR, respG, respB);
        return ComputeGains(k, whiteRef, respR, respG, respB);
    }

    /// <summary>
    /// Per-star channel ratios for the white-balance summary plot: the MEASURED
    /// ratio (obs_c / obs_G) and the EXPECTED ratio (Exp_c / Exp_G, from the
    /// star's spectrum integrated through channel c). Same star-rejection rule
    /// as <see cref="ComputeThroughput"/>. Returns four parallel lists
    /// (catalog B/G, image B/G, catalog R/G, image R/G).
    /// </summary>
    public static (List<double> CatBg, List<double> ImgBg, List<double> CatRg, List<double> ImgRg)
            ChannelRatios(IReadOnlyList<SpccStar> stars,
                ResponseCurve respR, ResponseCurve respG, ResponseCurve respB) {
        var catBg = new List<double>(); var imgBg = new List<double>();
        var catRg = new List<double>(); var imgRg = new List<double>();
        if (stars == null) return (catBg, imgBg, catRg, imgRg);
        foreach (var s in stars) {
            if (s.ObsR <= 0 || s.ObsG <= 0 || s.ObsB <= 0) continue;
            double eR = IntegrateChannel(s.Spectrum, respR);
            double eG = IntegrateChannel(s.Spectrum, respG);
            double eB = IntegrateChannel(s.Spectrum, respB);
            if (eR <= 0 || eG <= 0 || eB <= 0) continue;
            catBg.Add(eB / eG); imgBg.Add(s.ObsB / s.ObsG);
            catRg.Add(eR / eG); imgRg.Add(s.ObsR / s.ObsG);
        }
        return (catBg, imgBg, catRg, imgRg);
    }

    internal static double Median(List<double> values) {
        if (values.Count == 0) return 0;
        values.Sort();
        int mid = values.Count / 2;
        return values.Count % 2 == 1
            ? values[mid]
            : 0.5 * (values[mid - 1] + values[mid]);
    }
}
