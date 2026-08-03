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

namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Narrowband palette mapping (SHO / HSO / HOS / HOO ...): pack mono
/// Ha / OIII / SII masters into an RGB image per the chosen palette, with an
/// optional per-channel normalization that matches the three output channels'
/// backgrounds so no single filter dominates the colour. Pure math; the
/// combine service supplies the aligned planes.
///
/// Inspired by the narrowband integration / normalization tools in SASpro /
/// PixInsight but implemented from scratch (palette lookup + robust median
/// background match).
/// </summary>
public static class NarrowbandCombine {
    /// <summary>
    /// Compose an RGB (plane-sequential) image. Pass whichever masters the
    /// palette needs (unused ones may be null). Palettes:
    ///   SHO : R=SII, G=Ha,  B=OIII
    ///   HSO : R=Ha,  G=SII, B=OIII
    ///   HOS : R=Ha,  G=OIII,B=SII
    ///   HOO : R=Ha,  G=OIII,B=OIII (bicolor)
    /// </summary>
    public static ushort[] Compose(ushort[]? ha, ushort[]? oiii, ushort[]? sii,
                                   int width, int height, string palette, bool normalize = true) {
        long plane = (long)width * height;
        ushort[] r, g, b;
        switch ((palette ?? "sho").Trim().ToLowerInvariant()) {
            case "hso": r = Req(ha, "Ha"); g = Req(sii, "SII"); b = Req(oiii, "OIII"); break;
            case "hos": r = Req(ha, "Ha"); g = Req(oiii, "OIII"); b = Req(sii, "SII"); break;
            case "hoo": r = Req(ha, "Ha"); g = Req(oiii, "OIII"); b = Req(oiii, "OIII"); break;
            default:    r = Req(sii, "SII"); g = Req(ha, "Ha"); b = Req(oiii, "OIII"); break; // SHO
        }

        // Copy so the palette (which may alias the same plane, e.g. HOO uses
        // OIII twice) can be normalized independently without mutating inputs.
        var rc = (ushort[])r.Clone();
        var gc = (ushort[])g.Clone();
        var bc = (ushort[])b.Clone();

        if (normalize) {
            double mr = Median(rc), mg = Median(gc), mb = Median(bc);
            double target = Math.Max(mr, Math.Max(mg, mb));
            ScaleToMedian(rc, mr, target);
            ScaleToMedian(gc, mg, target);
            ScaleToMedian(bc, mb, target);
        }

        var packed = new ushort[plane * 3];
        Buffer.BlockCopy(rc, 0, packed, 0, (int)plane * sizeof(ushort));
        Buffer.BlockCopy(gc, 0, packed, (int)plane * sizeof(ushort), (int)plane * sizeof(ushort));
        Buffer.BlockCopy(bc, 0, packed, (int)plane * 2 * sizeof(ushort), (int)plane * sizeof(ushort));
        return packed;
    }

    private static ushort[] Req(ushort[]? ch, string name)
        => ch ?? throw new InvalidOperationException($"Narrowband palette needs the '{name}' channel.");

    private static void ScaleToMedian(ushort[] data, double med, double target) {
        if (med <= 1e-6) return;
        double k = target / med;
        if (Math.Abs(k - 1.0) < 1e-6) return;
        for (int i = 0; i < data.Length; i++)
            data[i] = (ushort)Math.Clamp(Math.Round(data[i] * k), 0, 65535);
    }

    private static double Median(ushort[] data) {
        if (data.Length == 0) return 0;
        var hist = new long[65536];
        foreach (var v in data) hist[v]++;
        long half = data.Length / 2, acc = 0;
        for (int v = 0; v < 65536; v++) { acc += hist[v]; if (acc >= half) return v; }
        return 0;
    }
}
