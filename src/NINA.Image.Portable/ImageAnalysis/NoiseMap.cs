// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

using System;
using System.Collections.Generic;

namespace NINA.Image.ImageAnalysis;

/// <summary>
/// A measured, per-pixel noise model — the differentiator over tools that
/// assume a single flat background σ. Astronomical noise follows a
/// photon-transfer law: variance grows linearly with signal,
///     σ²(S) = a·S + b   (a = 1/gain shot term, b = read-noise floor).
/// Both coefficients are estimated from the frame itself (no header / gain
/// guess needed) by measuring the high-frequency noise across many tiles at
/// different signal levels — a single-frame photon-transfer curve. The σ map
/// then drives noise-aware deconvolution: amplify where SNR is high, hold back
/// where it's noise-dominated, per pixel, accounting for shot noise on bright
/// nebulosity and for vignetting / gradients — not just a flat threshold.
/// </summary>
public static class NoiseMap {
    public readonly record struct Model(double A, double B) {
        /// <summary>Per-pixel σ for a signal value (clamped non-negative).</summary>
        public double Sigma(double signal) => Math.Sqrt(Math.Max(B, A * signal + B));
    }

    /// <summary>
    /// Fit σ²(S) = a·S + b from the frame's own statistics. Splits into tiles,
    /// estimates each tile's robust noise from horizontal first differences
    /// (immune to smooth gradients) at its median signal, then does an
    /// iteratively-clipped linear regression (dropping star/structure tiles
    /// whose variance sits well above the line).
    /// </summary>
    public static Model EstimateModel(ushort[] data, int width, int height, int tile = 32) {
        if (data == null) throw new ArgumentNullException(nameof(data));
        tile = Math.Max(8, tile);
        var sig = new List<double>();
        var var = new List<double>();

        for (int ty = 0; ty + tile <= height; ty += tile) {
            for (int tx = 0; tx + tile <= width; tx += tile) {
                // robust noise from |Δx| across the tile (first differences):
                // sd(diff) = √2·σ, so σ = MAD(diff)·1.4826/√2.
                var diffs = new List<double>(tile * tile);
                var vals = new List<double>(tile * tile);
                for (int y = 0; y < tile; y++) {
                    int row = (ty + y) * width + tx;
                    for (int x = 0; x < tile; x++) {
                        vals.Add(data[row + x]);
                        if (x > 0) diffs.Add(Math.Abs(data[row + x] - data[row + x - 1]));
                    }
                }
                double s = Median(vals);
                double madDiff = Median(diffs);
                double sigma = madDiff * 1.4826 / Math.Sqrt(2.0);
                sig.Add(s);
                var.Add(sigma * sigma);
            }
        }
        if (sig.Count < 4) return new Model(0, EstimateFloorVar(data));

        // If the tiles don't span a meaningful signal range (flat field / very
        // low dynamic range), the photon-transfer slope is unidentifiable and
        // the regression is degenerate — report a flat noise floor instead.
        double minS = double.MaxValue, maxS = double.MinValue;
        foreach (var s in sig) { if (s < minS) minS = s; if (s > maxS) maxS = s; }
        if (maxS - minS < 0.05 * Math.Max(1.0, Median(sig)))
            return new Model(0, Math.Max(1.0, Median(var)));

        // iteratively-clipped least squares of var = a·sig + b
        double a = 0, b = var.Count > 0 ? Median(var) : 1;
        var keep = new bool[sig.Count];
        for (int i = 0; i < keep.Length; i++) keep[i] = true;
        for (int iter = 0; iter < 4; iter++) {
            double sx = 0, sy = 0, sxx = 0, sxy = 0; int n = 0;
            for (int i = 0; i < sig.Count; i++) {
                if (!keep[i]) continue;
                sx += sig[i]; sy += var[i]; sxx += sig[i] * sig[i]; sxy += sig[i] * var[i]; n++;
            }
            if (n < 3) break;
            double denom = n * sxx - sx * sx;
            if (Math.Abs(denom) < 1e-9) { a = 0; b = sy / n; break; }
            a = (n * sxy - sx * sy) / denom;
            b = (sy - a * sx) / n;
            if (a < 0) a = 0;                          // variance can't fall with signal
            // clip tiles whose variance is well above the fit (stars/structure)
            double mad = 0; var resid = new List<double>(n);
            for (int i = 0; i < sig.Count; i++) if (keep[i]) resid.Add(var[i] - (a * sig[i] + b));
            mad = Median(AbsList(resid)) * 1.4826 + 1e-6;
            int dropped = 0;
            for (int i = 0; i < sig.Count; i++) {
                bool ok = (var[i] - (a * sig[i] + b)) <= 3 * mad;
                if (keep[i] && !ok) dropped++;
                keep[i] = ok || var[i] < a * sig[i] + b;  // keep below-line tiles
            }
            if (dropped == 0) break;
        }
        if (b <= 0) b = EstimateFloorVar(data);
        return new Model(a, b);
    }

    /// <summary>Per-pixel σ map (ADU) from a fitted model.</summary>
    public static float[] BuildSigmaMap(ushort[] data, Model m) {
        var sigma = new float[data.Length];
        for (int i = 0; i < data.Length; i++) sigma[i] = (float)m.Sigma(data[i]);
        return sigma;
    }

    /// <summary>Convenience: fit + build in one call.</summary>
    public static float[] Estimate(ushort[] data, int width, int height, out Model model, int tile = 32) {
        model = EstimateModel(data, width, height, tile);
        return BuildSigmaMap(data, model);
    }

    // Fallback read-floor variance: MAD of global first differences.
    private static double EstimateFloorVar(ushort[] data) {
        int step = Math.Max(1, data.Length / 100_000);
        var d = new List<double>();
        for (int i = step; i < data.Length; i += step) d.Add(Math.Abs(data[i] - data[i - step]));
        double sigma = Median(d) * 1.4826 / Math.Sqrt(2.0);
        return Math.Max(1.0, sigma * sigma);
    }

    private static double Median(List<double> v) {
        if (v == null || v.Count == 0) return 0;
        var a = v.ToArray(); Array.Sort(a);
        int n = a.Length; return n % 2 == 1 ? a[n / 2] : 0.5 * (a[n / 2 - 1] + a[n / 2]);
    }

    private static List<double> AbsList(List<double> v) {
        var o = new List<double>(v.Count);
        foreach (var x in v) o.Add(Math.Abs(x));
        return o;
    }
}
