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
/// Extract mono emission-line planes from an OSC (one-shot-colour)
/// dual-band master. Dual-band filters pass two narrow lines that land
/// on different Bayer channels of a colour sensor:
///
///   Ha + OIII filter:  Ha  -> Red channel, OIII -> Green + Blue channels
///   SII + OIII filter: SII -> Red channel, OIII -> Green + Blue channels
///
/// So the red plane of a debayered dual-band master is the (nearly pure)
/// Ha or SII line, and the green/blue planes carry OIII. We take the
/// red plane as the "red line" and the mean of green + blue as OIII
/// (averaging the two OIII-bearing channels roughly doubles the OIII
/// signal-to-noise versus using one channel).
///
/// Pure math; the combine service supplies the plane-sequential RGB data
/// (R plane, then G plane, then B plane). Reimplemented from the
/// published dual-band OSC extraction technique (not copied from any
/// third-party tool).
/// </summary>
public static class NarrowbandExtract {
    /// <summary>
    /// Split a plane-sequential RGB master into its red-line plane
    /// (Ha or SII) and its OIII plane (mean of green + blue).
    /// </summary>
    /// <param name="rgb">Plane-sequential RGB, length = width*height*3.</param>
    /// <returns>(redLine, oiii) mono planes, each length width*height.</returns>
    public static (ushort[] redLine, ushort[] oiii) Extract(ushort[] rgb, int width, int height) {
        long plane = (long)width * height;
        if (rgb.Length < plane * 3) {
            throw new InvalidOperationException(
                $"OSC extract expects a 3-channel RGB master ({plane * 3} samples), got {rgb.Length}.");
        }
        var redLine = new ushort[plane];
        var oiii = new ushort[plane];
        int gOff = (int)plane;
        int bOff = (int)plane * 2;
        for (int i = 0; i < plane; i++) {
            redLine[i] = rgb[i];
            // Mean of G + B, rounded. Both channels carry OIII on a
            // dual-band filter, so averaging cancels per-channel noise.
            oiii[i] = (ushort)((rgb[gOff + i] + rgb[bOff + i] + 1) / 2);
        }
        return (redLine, oiii);
    }
}
