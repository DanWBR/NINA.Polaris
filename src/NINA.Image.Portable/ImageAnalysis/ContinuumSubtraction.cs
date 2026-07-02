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
using System.Collections.Generic;

namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Continuum subtraction: isolate the emission-line signal in a narrowband
/// master by removing a scaled broadband (continuum) master —
/// <c>NB' = max(0, NB - k·Continuum)</c>. Stars (pure continuum) largely
/// cancel while the emission nebulosity remains. The scale <c>k</c> can be
/// given, or estimated automatically from the bright (star-dominated) pixels
/// as the median ratio NB/Continuum there.
///
/// Pure math; the combine service supplies the two aligned mono planes.
/// </summary>
public static class ContinuumSubtraction {
    /// <summary>
    /// Return the continuum-subtracted narrowband (mono). When
    /// <paramref name="autoScale"/> is set, <paramref name="scale"/> is ignored
    /// and estimated from the brightest continuum pixels.
    /// </summary>
    public static ushort[] Subtract(ushort[] nb, ushort[] continuum, int width, int height,
                                    double scale = 1.0, bool autoScale = true) {
        if (nb.Length != continuum.Length)
            throw new InvalidOperationException("NB and continuum planes must match in size.");
        double k = autoScale ? EstimateScale(nb, continuum) : Math.Clamp(scale, 0.0, 4.0);

        var outp = new ushort[nb.Length];
        for (int i = 0; i < nb.Length; i++) {
            double v = nb[i] - k * continuum[i];
            outp[i] = (ushort)Math.Clamp(Math.Round(v), 0, 65535);
        }
        return outp;
    }

    // Estimate k as the median of NB/Continuum over the brightest continuum
    // pixels (stars), where the emission contribution is negligible, so
    // subtracting k·Continuum removes the stellar continuum cleanly.
    private static double EstimateScale(ushort[] nb, ushort[] continuum) {
        // Threshold = 99th percentile of the continuum (the star pixels).
        var hist = new long[65536];
        foreach (var v in continuum) hist[v]++;
        long total = continuum.Length, want = (long)(total * 0.99), acc = 0;
        int thr = 65535;
        for (int v = 0; v < 65536; v++) { acc += hist[v]; if (acc >= want) { thr = v; break; } }
        thr = Math.Max(thr, 1);

        var ratios = new List<double>();
        for (int i = 0; i < continuum.Length; i++) {
            if (continuum[i] >= thr && continuum[i] > 0) ratios.Add((double)nb[i] / continuum[i]);
        }
        if (ratios.Count == 0) return 1.0;
        ratios.Sort();
        double med = ratios[ratios.Count / 2];
        return Math.Clamp(med, 0.0, 4.0);
    }
}
