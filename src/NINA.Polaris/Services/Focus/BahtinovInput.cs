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

using NINA.Core.Enum;

namespace NINA.Polaris.Services.Focus;

/// <summary>
/// Preparing a frame for <see cref="BahtinovAnalyzer"/>, and putting its
/// answer back into frame coordinates.
///
/// Two things sit between a live video frame and the analyser:
///
/// <para>The ROI. The analyser crops a box around the star and sweeps
/// lines across it, so the box has to contain the whole spike pattern. At
/// 2000 mm with small pixels, or with the focuser far out, the spikes run
/// well past the 100 px default and the measurement quietly degrades. So
/// the size is a parameter the operator can raise, clamped here.</para>
///
/// <para>The colour filter array. A one-shot-colour frame is a mosaic, so
/// a line integrated across it alternates between red, green and blue
/// sensitivity every pixel: a 2 px ripple riding on the spike profile,
/// which is noise as far as peak finding is concerned. Averaging each 2x2
/// quad into one pixel gives one sample per colour site, which is the same
/// trick the live stacker uses to judge a mosaic. It halves the
/// resolution, so everything the analyser reports is scaled back up here
/// and the client never has to know.</para>
/// </summary>
public static class BahtinovInput {
    /// <summary>Smallest ROI half-width worth analysing: below this the
    /// sweep has too few pixels per line to find three peaks.</summary>
    public const int MinRoiHalf = 32;

    /// <summary>Largest ROI half-width. The sweep is O(angles x roiHalf),
    /// so this caps the per-frame cost on an SBC.</summary>
    public const int MaxRoiHalf = 400;

    public const int DefaultRoiHalf = 100;

    /// <summary>ROI half-width in FRAME pixels, clamped. A null or absent
    /// request keeps the default.</summary>
    public static int ClampRoiHalf(int? requested) {
        var v = requested ?? DefaultRoiHalf;
        if (v < MinRoiHalf) return MinRoiHalf;
        if (v > MaxRoiHalf) return MaxRoiHalf;
        return v;
    }

    /// <summary>True when the frame is a colour mosaic and should be
    /// reduced to pseudo-luminance before the sweep. Auto and None both
    /// mean "nothing known about a CFA here", so they are left alone.</summary>
    public static bool IsMosaic(BayerPatternEnum pattern) =>
        pattern != BayerPatternEnum.None && pattern != BayerPatternEnum.Auto;

    /// <summary>
    /// Average every 2x2 quad into one pixel. On a Bayer frame each quad
    /// holds one R, one B and two G sites, so the result is a
    /// green-weighted luminance at half the width and height, with the 2 px
    /// mosaic ripple gone. An odd last row or column is dropped rather
    /// than half-sampled.
    /// </summary>
    public static (ushort[] Pixels, int Width, int Height) PseudoLuminance2x2(
            ushort[] pixels, int width, int height) {
        if (pixels == null || width <= 1 || height <= 1 || pixels.Length != width * height) {
            return (Array.Empty<ushort>(), 0, 0);
        }
        var ow = width / 2;
        var oh = height / 2;
        var outPx = new ushort[ow * oh];
        for (int oy = 0; oy < oh; oy++) {
            int r0 = (oy * 2) * width;
            int r1 = r0 + width;
            int dst = oy * ow;
            for (int ox = 0; ox < ow; ox++) {
                int x0 = ox * 2;
                int sum = pixels[r0 + x0] + pixels[r0 + x0 + 1]
                        + pixels[r1 + x0] + pixels[r1 + x0 + 1];
                outPx[dst + ox] = (ushort)(sum >> 2);
            }
        }
        return (outPx, ow, oh);
    }

    /// <summary>
    /// Scale a result measured on a reduced grid back to frame pixels.
    /// Positions, the perpendicular offsets and the ROI all multiply by
    /// <paramref name="scale"/>; so does the in-focus threshold, because
    /// half a pixel on a 2x2-reduced grid is a whole pixel of the same
    /// defocus in the frame the operator is looking at. A scale of 1
    /// returns the result untouched.
    /// </summary>
    public static BahtinovResult ToFrameScale(BahtinovResult r, int scale) {
        if (r == null || !r.Ok || scale <= 1) return r!;
        return r with {
            StarX = r.StarX * scale,
            StarY = r.StarY * scale,
            RoiHalf = r.RoiHalf * scale,
            Spike1Rho = r.Spike1Rho * scale,
            Spike2Rho = r.Spike2Rho * scale,
            Spike3Rho = r.Spike3Rho * scale,
            OffsetPx = r.OffsetPx * scale,
            InFocusThresholdPx = r.InFocusThresholdPx * scale,
            IntersectionX = r.IntersectionX * scale,
            IntersectionY = r.IntersectionY * scale
        };
    }
}
