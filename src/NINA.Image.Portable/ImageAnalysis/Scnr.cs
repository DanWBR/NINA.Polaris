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
/// SCNR — Subtractive Chromatic Noise Reduction. Removes the residual green
/// colour cast that light pollution + OSC debayering leave on broadband RGB
/// stacks (real astro scenes have almost no pure green, so a green-dominant
/// pixel is noise). Ported from Siril's <c>src/filters/scnr.c</c> (GPLv3;
/// re-implemented here in C#, no code copied) — the four classic modes:
///
///   AverageNeutral : g = min(g, (r+b)/2)
///   MaximumNeutral : g = min(g, max(r,b))
///   MaximumMask    : g = g·(1-amount)·(1-m) + m·g,  m = max(r,b)
///   AdditiveMask   : g = g·(1-amount)·(1-m) + m·g,  m = min(1, r+b)
///
/// Values are worked in normalised [0,1]. Optional <c>preserveLightness</c>
/// keeps the pixel's Rec.709 luminance (rescales r/g/b after the green
/// subtraction) so the operation only shifts hue, not overall brightness — a
/// self-contained approximation of Siril's CIE-L preserve.
/// Mono images are a no-op (SCNR needs three colour planes).
/// </summary>
public static class Scnr {
    public enum ScnrMode {
        AverageNeutral,
        MaximumNeutral,
        MaximumMask,
        AdditiveMask,
    }

    public static ScnrMode ParseMode(string? s) => (s ?? "").Trim().ToLowerInvariant() switch {
        "maximumneutral" or "maximum-neutral" or "maxneutral" => ScnrMode.MaximumNeutral,
        "maximummask" or "maximum-mask" or "maxmask" => ScnrMode.MaximumMask,
        "additivemask" or "additive-mask" or "addmask" => ScnrMode.AdditiveMask,
        _ => ScnrMode.AverageNeutral,
    };

    /// <summary>
    /// Apply SCNR in place to a plane-sequential ushort buffer. Returns the
    /// number of pixels whose green was reduced (0 for a mono no-op).
    /// </summary>
    public static long Apply(ushort[] data, int width, int height, int channels,
                             ScnrMode mode, double amount = 1.0, bool preserveLightness = false) {
        if (channels != 3) return 0;   // needs R,G,B planes
        long plane = (long)width * height;
        double a = Math.Clamp(amount, 0.0, 1.0);
        const double norm = 65535.0, invnorm = 1.0 / 65535.0;
        long changed = 0;

        Parallel.For(0, height, y => {
            long baseIdx = (long)y * width;
            for (int x = 0; x < width; x++) {
                long i = baseIdx + x;
                double r = data[i] * invnorm;
                double g = data[plane + i] * invnorm;
                double b = data[2 * plane + i] * invnorm;
                double g0 = g;

                double lum0 = preserveLightness ? Luma(r, g, b) : 0.0;

                double m;
                switch (mode) {
                    case ScnrMode.MaximumNeutral:
                        m = Math.Max(r, b);
                        g = Math.Min(g, m);
                        break;
                    case ScnrMode.MaximumMask:
                        m = Math.Max(r, b);
                        g = (g * (1.0 - a) * (1.0 - m)) + (m * g);
                        break;
                    case ScnrMode.AdditiveMask:
                        m = Math.Min(1.0, r + b);
                        g = (g * (1.0 - a) * (1.0 - m)) + (m * g);
                        break;
                    default: // AverageNeutral
                        m = 0.5 * (r + b);
                        g = Math.Min(g, m);
                        break;
                }

                if (preserveLightness) {
                    // Rescale the whole pixel so its luminance is unchanged,
                    // i.e. the op only re-hues (approximation of Siril's LAB
                    // lightness preserve). Skip when the new luma is ~0.
                    double lum1 = Luma(r, g, b);
                    if (lum1 > 1e-6) {
                        double k = lum0 / lum1;
                        r = Math.Clamp(r * k, 0.0, 1.0);
                        g = Math.Clamp(g * k, 0.0, 1.0);
                        b = Math.Clamp(b * k, 0.0, 1.0);
                    }
                }

                if (g < g0 - 1e-9) System.Threading.Interlocked.Increment(ref changed);

                data[i]             = (ushort)Math.Clamp(Math.Round(r * norm), 0, 65535);
                data[plane + i]     = (ushort)Math.Clamp(Math.Round(g * norm), 0, 65535);
                data[2 * plane + i] = (ushort)Math.Clamp(Math.Round(b * norm), 0, 65535);
            }
        });
        return changed;
    }

    private static double Luma(double r, double g, double b)
        => 0.2126 * r + 0.7152 * g + 0.0722 * b;
}
