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

using System;
using System.Threading.Tasks;

namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Generalized Hyperbolic Stretch (GHS) + asinh stretch. These are the
/// non-linear "stretch" transforms astrophotographers use to pull faint
/// nebulosity out of a linear stack while protecting star cores from
/// blowing out -- a more controllable alternative to a plain MTF/histogram
/// stretch.
///
/// The forward transform + its coefficient setup are re-implemented in C#
/// from Siril's <c>src/filters/ght.c</c> (the <c>GHTsetup</c> /
/// <c>GHT</c> functions, GPLv3; no code copied), covering the
/// <c>STRETCH_PAYNE_NORMAL</c> (GHS) and <c>STRETCH_ASINH</c> forward
/// branches. The generalized-hyperbolic family is from D. Payne's published
/// GHS equations.
///
/// Parameters (all in normalised [0,1] intensity):
///   D  : stretch amount / intensity (0 = identity, higher = stronger lift).
///   B  : local stretch intensity / character (GHS only; 0 = logarithmic,
///        &gt;0 harder, &lt;0 softer, -1 = pure log special case).
///   SP : symmetry / stretch-focus point (where the transform pivots).
///   LP : shadow-protection point (below it the response is linear).
///   HP : highlight-protection point (above it the response is linear).
///   BP : black point (linear pre-offset, subtracted then rescaled).
///
/// Usage: <see cref="Setup"/> once to get the coefficient block, then
/// <see cref="Evaluate"/> per sample, or build a LUT via
/// <see cref="ApplyToUshort"/> which does both and applies the same
/// (linked) curve to every channel so colour balance is preserved.
/// </summary>
public static class HyperbolicStretch {
    public enum StretchType {
        /// <summary>Generalized hyperbolic (Payne normal).</summary>
        Ghs,
        /// <summary>Arc-sinh stretch.</summary>
        Asinh,
    }

    public static StretchType ParseType(string? s) => (s ?? "").Trim().ToLowerInvariant() switch {
        "asinh" => StretchType.Asinh,
        _ => StretchType.Ghs,
    };

    /// <summary>Coefficient block produced by <see cref="Setup"/>; opaque to callers.</summary>
    public sealed class Coefficients {
        public double qlp, q0, qwp, q1, q, b1, a1, a2, b2, c2, d2, e2, a3, b3, c3, d3, e3, a4, b4;
    }

    /// <summary>
    /// Compute the transform coefficients for the given parameters. Mirrors
    /// Siril's GHTsetup for the forward GHS (Payne normal) + asinh branches.
    /// </summary>
    public static Coefficients Setup(StretchType type, double B, double D,
                                     double LP, double SP, double HP) {
        var c = new Coefficients();
        if (D == 0.0) return c; // identity; all coeffs zero, Evaluate short-circuits.

        if (type == StretchType.Ghs) {
            if (B == -1.0) {
                c.qlp = -1.0 * Log1p(D * (SP - LP));
                c.q0 = c.qlp - D * LP / (1.0 + D * (SP - LP));
                c.qwp = Log1p(D * (HP - SP));
                c.q1 = c.qwp + D * (1.0 - HP) / (1.0 + D * (HP - SP));
                c.q = 1.0 / (c.q1 - c.q0);
                c.b1 = (1.0 + D * (SP - LP)) / (D * c.q);
                c.a2 = (-c.q0) * c.q;
                c.b2 = -c.q;
                c.c2 = 1.0 + D * SP;
                c.d2 = -D;
                c.a3 = (-c.q0) * c.q;
                c.b3 = c.q;
                c.c3 = 1.0 - D * SP;
                c.d3 = D;
                c.a4 = (c.qwp - c.q0 - D * HP / (1.0 + D * (HP - SP))) * c.q;
                c.b4 = c.q * D / (1.0 + D * (HP - SP));
            } else if (B < 0.0) {
                B = -B;
                c.qlp = (1.0 - Math.Pow(1.0 + D * B * (SP - LP), (B - 1.0) / B)) / (B - 1.0);
                c.q0 = c.qlp - D * LP * Math.Pow(1.0 + D * B * (SP - LP), -1.0 / B);
                c.qwp = (Math.Pow(1.0 + D * B * (HP - SP), (B - 1.0) / B) - 1.0) / (B - 1.0);
                c.q1 = c.qwp + D * (1.0 - HP) * Math.Pow(1.0 + D * B * (HP - SP), -1.0 / B);
                c.q = 1.0 / (c.q1 - c.q0);
                c.b1 = D * Math.Pow(1.0 + D * B * (SP - LP), -1.0 / B) * c.q;
                c.a2 = (1.0 / (B - 1.0) - c.q0) * c.q;
                c.b2 = -c.q / (B - 1.0);
                c.c2 = 1.0 + D * B * SP;
                c.d2 = -D * B;
                c.e2 = (B - 1.0) / B;
                c.a3 = (-1.0 / (B - 1.0) - c.q0) * c.q;
                c.b3 = c.q / (B - 1.0);
                c.c3 = 1.0 - D * B * SP;
                c.d3 = D * B;
                c.e3 = (B - 1.0) / B;
                c.a4 = (c.qwp - c.q0 - D * HP * Math.Pow(1.0 + D * B * (HP - SP), -1.0 / B)) * c.q;
                c.b4 = D * Math.Pow(1.0 + D * B * (HP - SP), -1.0 / B) * c.q;
            } else if (B == 0.0) {
                c.qlp = Math.Exp(-D * (SP - LP));
                c.q0 = c.qlp - D * LP * Math.Exp(-D * (SP - LP));
                c.qwp = 2.0 - Math.Exp(-D * (HP - SP));
                c.q1 = c.qwp + D * (1.0 - HP) * Math.Exp(-D * (HP - SP));
                c.q = 1.0 / (c.q1 - c.q0);
                c.a1 = 0.0;
                c.b1 = D * Math.Exp(-D * (SP - LP)) * c.q;
                c.a2 = -c.q0 * c.q;
                c.b2 = c.q;
                c.c2 = -D * SP;
                c.d2 = D;
                c.a3 = (2.0 - c.q0) * c.q;
                c.b3 = -c.q;
                c.c3 = D * SP;
                c.d3 = -D;
                c.a4 = (c.qwp - c.q0 - D * HP * Math.Exp(-D * (HP - SP))) * c.q;
                c.b4 = D * Math.Exp(-D * (HP - SP)) * c.q;
            } else { // B > 0
                c.qlp = Math.Pow(1.0 + D * B * (SP - LP), -1.0 / B);
                c.q0 = c.qlp - D * LP * Math.Pow(1.0 + D * B * (SP - LP), -(1.0 + B) / B);
                c.qwp = 2.0 - Math.Pow(1.0 + D * B * (HP - SP), -1.0 / B);
                c.q1 = c.qwp + D * (1.0 - HP) * Math.Pow(1.0 + D * B * (HP - SP), -(1.0 + B) / B);
                c.q = 1.0 / (c.q1 - c.q0);
                c.b1 = D * Math.Pow(1.0 + D * B * (SP - LP), -(1.0 + B) / B) * c.q;
                c.a2 = -c.q0 * c.q;
                c.b2 = c.q;
                c.c2 = 1.0 + D * B * SP;
                c.d2 = -D * B;
                c.e2 = -1.0 / B;
                c.a3 = (2.0 - c.q0) * c.q;
                c.b3 = -c.q;
                c.c3 = 1.0 - D * B * SP;
                c.d3 = D * B;
                c.e3 = -1.0 / B;
                c.a4 = (c.qwp - c.q0 - D * HP * Math.Pow(1.0 + D * B * (HP - SP), -(B + 1.0) / B)) * c.q;
                c.b4 = D * Math.Pow(1.0 + D * B * (HP - SP), -(B + 1.0) / B) * c.q;
            }
        } else { // Asinh
            c.qlp = -Math.Log(D * (SP - LP) + Math.Sqrt(D * D * (SP - LP) * (SP - LP) + 1.0));
            c.q0 = c.qlp - LP * D * Math.Pow(D * D * (SP - LP) * (SP - LP) + 1.0, -0.5);
            c.qwp = Math.Log(D * (HP - SP) + Math.Sqrt(D * D * (HP - SP) * (HP - SP) + 1.0));
            c.q1 = c.qwp + (1.0 - HP) * D * Math.Pow(D * D * (HP - SP) * (HP - SP) + 1.0, -0.5);
            c.q = 1.0 / (c.q1 - c.q0);
            c.a1 = 0.0;
            c.b1 = D * Math.Pow(D * D * (SP - LP) * (SP - LP) + 1.0, -0.5) * c.q;
            c.a2 = -c.q0 * c.q;
            c.b2 = -c.q;
            c.c2 = -D;
            c.d2 = D * D;
            c.e2 = SP;
            c.a3 = -c.q0 * c.q;
            c.b3 = c.q;
            c.c3 = D;
            c.d3 = D * D;
            c.e3 = SP;
            c.a4 = (c.qwp - HP * D * Math.Pow(D * D * (HP - SP) * (HP - SP) + 1.0, -0.5) - c.q0) * c.q;
            c.b4 = D * Math.Pow(D * D * (HP - SP) * (HP - SP) + 1.0, -0.5) * c.q;
        }
        return c;
    }

    /// <summary>
    /// Forward transform of a single sample in [0,1]. Mirrors Siril's GHT for
    /// the GHS + asinh forward branches. Output is clamped to [0,1].
    /// </summary>
    public static double Evaluate(double v, StretchType type, double B, double D,
                                  double LP, double SP, double HP, double BP, Coefficients c) {
        double @in = Math.Max(0.0, (v - BP) / (1.0 - BP));
        if (D == 0.0) return Clamp01(@in);

        double res1, res2, @out;
        if (type == StretchType.Ghs) {
            if (B == -1.0) {
                res1 = c.a2 + c.b2 * Math.Log(c.c2 + c.d2 * @in);
                res2 = c.a3 + c.b3 * Math.Log(c.c3 + c.d3 * @in);
            } else if (B < 0.0 || B > 0.0) {
                res1 = c.a2 + c.b2 * Math.Pow(c.c2 + c.d2 * @in, c.e2);
                res2 = c.a3 + c.b3 * Math.Pow(c.c3 + c.d3 * @in, c.e3);
            } else {
                res1 = c.a2 + c.b2 * Math.Exp(c.c2 + c.d2 * @in);
                res2 = c.a3 + c.b3 * Math.Exp(c.c3 + c.d3 * @in);
            }
            @out = (@in < LP) ? c.b1 * @in
                 : (@in < SP) ? res1
                 : (@in < HP) ? res2
                 : c.a4 + c.b4 * @in;
        } else { // Asinh
            double val = c.c2 * (@in - c.e2) + Math.Sqrt(c.d2 * (@in - c.e2) * (@in - c.e2) + 1.0);
            res1 = c.a2 + c.b2 * Math.Log(val);
            val = c.c3 * (@in - c.e3) + Math.Sqrt(c.d3 * (@in - c.e3) * (@in - c.e3) + 1.0);
            res2 = c.a3 + c.b3 * Math.Log(val);
            @out = (@in < LP) ? c.a1 + c.b1 * @in
                 : (@in < SP) ? res1
                 : (@in < HP) ? res2
                 : c.a4 + c.b4 * @in;
        }
        return Clamp01(@out);
    }

    /// <summary>
    /// Build a <paramref name="size"/>-entry LUT (input index /(size-1) → output)
    /// for the given parameters. Reused by both the FITS (65536) and editor
    /// (256) paths so both produce the identical curve.
    /// </summary>
    public static double[] BuildLut(int size, StretchType type, double B, double D,
                                    double LP, double SP, double HP, double BP) {
        var c = Setup(type, B, D, LP, SP, HP);
        var lut = new double[size];
        double denom = size - 1;
        for (int i = 0; i < size; i++)
            lut[i] = Evaluate(i / denom, type, B, D, LP, SP, HP, BP, c);
        return lut;
    }

    /// <summary>
    /// Apply the stretch in place to a plane-sequential ushort buffer via a
    /// 65536-entry LUT. The SAME curve is applied to every channel (linked),
    /// so a colour image keeps its balance -- only tonal distribution changes.
    /// </summary>
    public static void ApplyToUshort(ushort[] data, StretchType type, double B, double D,
                                     double LP, double SP, double HP, double BP) {
        if (D == 0.0) return; // identity
        var lut = BuildLut(65536, type, B, D, LP, SP, HP, BP);
        var byteLut = new ushort[65536];
        for (int i = 0; i < 65536; i++)
            byteLut[i] = (ushort)Math.Clamp(Math.Round(lut[i] * 65535.0), 0, 65535);
        Parallel.For(0, data.Length, i => { data[i] = byteLut[data[i]]; });
    }

    /// <summary>
    /// Estimate D so a source of median intensity <paramref name="median01"/>
    /// (0..1) maps to <paramref name="target"/> after the transform, with the
    /// other params at their defaults. Bisection on D (monotonic in D for a
    /// fixed input). Used by the "Auto" stretch option so the user gets a
    /// reasonable lift without dialing D by hand.
    /// </summary>
    public static double EstimateD(double median01, double target,
                                   StretchType type, double B, double LP, double SP, double HP,
                                   double bp = 0.0, double dMin = 0.0, double dMax = 30.0) {
        median01 = Clamp01(median01);
        // If the median already exceeds the target, no stretch needed.
        if (median01 >= target) return 0.0;
        double lo = dMin, hi = dMax;
        for (int it = 0; it < 40; it++) {
            double mid = 0.5 * (lo + hi);
            var c = Setup(type, B, mid, LP, SP, HP);
            double y = Evaluate(median01, type, B, mid, LP, SP, HP, bp, c);
            if (y < target) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    private static double Clamp01(double x) => x < 0.0 ? 0.0 : x > 1.0 ? 1.0 : x;

    // log(1+x) with the numerical stability C's log1p gives near x≈0.
    private static double Log1p(double x) => Math.Log(1.0 + x);
}
