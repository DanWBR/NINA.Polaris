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
/// Highlight recovery: a soft-knee compression of the highlights above a knee
/// point, leaving everything below the knee untouched. Pulls blown star cores
/// / bright cores down so their structure reads again, without darkening the
/// midtones or shadows. Monotonic, endpoints fixed (knee→knee, 1→1).
///
/// Operates on luminance and re-applies the change as a per-pixel gain to each
/// RGB channel, so colour is preserved. Inspired by the HDR highlight
/// compression in SASpro/PixInsight; implemented from scratch.
/// </summary>
public static class HighlightRecovery {
    /// <summary>
    /// Apply in place. <paramref name="knee"/> 0..1 = where compression starts;
    /// <paramref name="strength"/> 0..1 = how hard the highlights are pulled down.
    /// </summary>
    public static void Apply(ushort[] data, int width, int height, int channels,
                             double knee = 0.6, double strength = 0.5) {
        int ch = channels == 3 ? 3 : 1;
        long plane = (long)width * height;
        double kn = Math.Clamp(knee, 0.0, 0.999);
        double s = Math.Clamp(strength, 0.0, 1.0);
        if (s <= 0.0) return; // identity

        const double inv = 1.0 / 65535.0;
        double span = 1.0 - kn;
        double g = 1.0 + 2.0 * s; // >1 → compress the [knee,1] range downward

        Parallel.For(0, height, y => {
            long baseIdx = (long)y * width;
            for (int x = 0; x < width; x++) {
                long i = baseIdx + x;
                if (ch == 3) {
                    double r = data[i] * inv, gg = data[plane + i] * inv, b = data[2 * plane + i] * inv;
                    double l0 = ColorSpace.Luminance(r, gg, b);
                    double l1 = Compress(l0, kn, span, g);
                    double gain = l0 > 1e-5 ? l1 / l0 : 1.0;
                    data[i]             = Scale(data[i] * gain);
                    data[plane + i]     = Scale(data[plane + i] * gain);
                    data[2 * plane + i] = Scale(data[2 * plane + i] * gain);
                } else {
                    double v = data[i] * inv;
                    data[i] = (ushort)Math.Clamp(Math.Round(Compress(v, kn, span, g) * 65535.0), 0, 65535);
                }
            }
        });
    }

    // Identity below the knee; above it, compress t=(v-knee)/span via t^g (g>1),
    // which is monotonic and keeps the two endpoints fixed.
    private static double Compress(double v, double knee, double span, double g) {
        if (v <= knee || span <= 0) return v;
        double t = (v - knee) / span;
        return knee + span * Math.Pow(t, g);
    }

    private static ushort Scale(double v) => (ushort)Math.Clamp(Math.Round(v), 0, 65535);
}
