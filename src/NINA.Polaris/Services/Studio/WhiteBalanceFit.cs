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
/// Builds the "White Balance summary" that PCC and SPCC return for display —
/// the scatter of measured (image) channel ratio against expected (catalog)
/// channel ratio, one panel for B/G and one for R/G, each with a robust
/// straight-line fit, exactly like the summary PixInsight SPCC and Siril show
/// after a photometric calibration.
///
/// For each matched star both calibrators already compute an EXPECTED ratio
/// (PCC from the star's catalog B-V through an empirical slope; SPCC from the
/// star's spectrum integrated through the real channel responses) and a
/// MEASURED ratio (its background-subtracted channel fluxes). Plotting
/// measured vs expected and fitting a line makes the calibration auditable:
/// a good fit clusters tightly on a line through the origin, and the slope /
/// scatter (sigma) quantify the colour agreement.
///
/// Pure math + DTOs, no IO, so it is unit-testable and shared by both the
/// broadband <see cref="ColorCalibrationService"/> (PCC) and the spectral
/// <see cref="SpccService"/> (SPCC).
/// </summary>
public static class WhiteBalanceFit {

    /// <summary>Robust straight-line fit of one channel: y = intercept +
    /// slope·x, with the sigma (RMS residual) and how many points survived
    /// the outlier rejection.</summary>
    public record ChannelFit(double Slope, double Intercept, double Sigma,
        int NStars, int NOutliers);

    /// <summary>One panel of the summary: the (expected, measured) points to
    /// scatter plus the line fit through them.</summary>
    public record WbChannel(double[] CatX, double[] ImgY, ChannelFit Fit);

    /// <summary>The full summary the UI renders: a B/G panel, an R/G panel,
    /// the resulting white-balance gains, and labels for the header.</summary>
    public record Summary(WbChannel Bg, WbChannel Rg,
        double GainR, double GainG, double GainB,
        string Reference, string Method, int Stars);

    /// <summary>Cap on points sent to the browser per panel (the fit itself
    /// uses every point; only the scatter is thinned for payload/render).</summary>
    public const int MaxPoints = 1500;

    /// <summary>
    /// Fit one channel with a two-pass 3σ clip: ordinary least squares, drop
    /// points whose residual exceeds 3× the RMS residual, refit. Returns the
    /// (thinned) points for display and the fit computed on the survivors.
    /// </summary>
    public static WbChannel FitChannel(IReadOnlyList<double> catX, IReadOnlyList<double> imgY) {
        if (catX == null || imgY == null || catX.Count != imgY.Count)
            throw new ArgumentException("White-balance fit needs equal-length x/y.");
        int n = catX.Count;
        var keep = new bool[n];
        for (int i = 0; i < n; i++)
            keep[i] = !(double.IsNaN(catX[i]) || double.IsNaN(imgY[i])
                        || double.IsInfinity(catX[i]) || double.IsInfinity(imgY[i]));

        double slope = 1, intercept = 0, sigma = 0;
        for (int pass = 0; pass < 3; pass++) {
            (slope, intercept, sigma) = Ols(catX, imgY, keep);
            if (sigma <= 0 || pass == 2) break;
            bool changed = false;
            for (int i = 0; i < n; i++) {
                if (!keep[i]) continue;
                double resid = Math.Abs(imgY[i] - (intercept + slope * catX[i]));
                if (resid > 3.0 * sigma) { keep[i] = false; changed = true; }
            }
            if (!changed) break;
        }

        int kept = 0;
        for (int i = 0; i < n; i++) if (keep[i]) kept++;
        int outliers = 0;
        for (int i = 0; i < n; i++)
            if (!keep[i] && !(double.IsNaN(catX[i]) || double.IsNaN(imgY[i]))) outliers++;

        // Thin the DISPLAY points (all valid points, kept or clipped, so the
        // scatter looks full) by an even stride when over the cap.
        var idx = new List<int>(n);
        for (int i = 0; i < n; i++)
            if (!(double.IsNaN(catX[i]) || double.IsNaN(imgY[i]))) idx.Add(i);
        int stride = idx.Count > MaxPoints ? (int)Math.Ceiling(idx.Count / (double)MaxPoints) : 1;
        var xs = new List<double>();
        var ys = new List<double>();
        for (int j = 0; j < idx.Count; j += stride) {
            xs.Add(Math.Round(catX[idx[j]], 4));
            ys.Add(Math.Round(imgY[idx[j]], 4));
        }

        return new WbChannel(xs.ToArray(), ys.ToArray(),
            new ChannelFit(Math.Round(slope, 6), Math.Round(intercept, 6),
                Math.Round(sigma, 6), kept, outliers));
    }

    /// <summary>Assemble both panels + the gains/labels into a Summary.</summary>
    public static Summary Build(
            IReadOnlyList<double> catBg, IReadOnlyList<double> imgBg,
            IReadOnlyList<double> catRg, IReadOnlyList<double> imgRg,
            double gainR, double gainG, double gainB,
            string reference, string method, int stars)
        => new(FitChannel(catBg, imgBg), FitChannel(catRg, imgRg),
               gainR, gainG, gainB, reference, method, stars);

    // Ordinary least squares over the kept points; returns (slope, intercept,
    // rms-residual). Degenerate inputs fall back to a unit slope so the caller
    // still gets a sane line rather than a throw.
    private static (double slope, double intercept, double sigma) Ols(
            IReadOnlyList<double> x, IReadOnlyList<double> y, bool[] keep) {
        double sx = 0, sy = 0, sxx = 0, sxy = 0; int m = 0;
        for (int i = 0; i < x.Count; i++) {
            if (!keep[i]) continue;
            sx += x[i]; sy += y[i]; sxx += x[i] * x[i]; sxy += x[i] * y[i]; m++;
        }
        if (m < 2) return (1, 0, 0);
        double denom = m * sxx - sx * sx;
        double slope = Math.Abs(denom) < 1e-12 ? 1.0 : (m * sxy - sx * sy) / denom;
        double intercept = (sy - slope * sx) / m;
        double ss = 0;
        for (int i = 0; i < x.Count; i++) {
            if (!keep[i]) continue;
            double r = y[i] - (intercept + slope * x[i]);
            ss += r * r;
        }
        return (slope, intercept, Math.Sqrt(ss / m));
    }
}
