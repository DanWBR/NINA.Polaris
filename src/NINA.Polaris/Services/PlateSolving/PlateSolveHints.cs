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

using NINA.Image.Interfaces;

namespace NINA.Polaris.Services.PlateSolving;

/// <summary>
/// Field geometry for a solve: how big the frame is on the sky, and how much
/// sky one pixel covers.
///
/// WHY THIS IS SHARED. A solver with no scale hint has to search every plate
/// scale it knows, which is the single biggest cause of slow and failed
/// solves. Measured on an Orange Pi 5 Pro with an ASI585MC at 1268mm
/// (2026-08-10), on a thin 5s frame:
///
///     ASTAP, no -fov      no solution, 18s spent sweeping 1d to 30d
///     ASTAP, -fov 0.26    solved in 1s
///
/// The FOV/scale was computed correctly in SlewCenterService and nowhere else,
/// so every solve started from the PREVIEW, AUX or ANNOTATE endpoint went out
/// blind. This type is the one place that arithmetic lives.
/// </summary>
public static class PlateSolveHints {

    /// <param name="FovDeg">Field HEIGHT in degrees. ASTAP's -fov is the
    /// vertical extent; using the width over-states it on any non-square
    /// sensor and the hinted solve then fails at the wrong scale.</param>
    /// <param name="ScaleArcsecPerPixel">Dimension-independent, and the
    /// tightest hint available. Astrometry.net and PlateSolve3 prefer it.</param>
    /// <param name="EffectivePixelUm">Pixel pitch after any binning
    /// correction, for logging.</param>
    /// <param name="NativeBinning">The reduction factor detected, or 1.</param>
    public readonly record struct Geometry(
        double FovDeg, double ScaleArcsecPerPixel, double EffectivePixelUm, int NativeBinning);

    /// <summary>Work out the geometry, correcting for a reduced frame.
    ///
    /// A binned or reduced frame keeps the sensor's NATIVE pixel pitch in its
    /// metadata, so pairing that pitch with the reduced height puts both hints
    /// out by the reduction factor. That is not hypothetical: a live stack at
    /// 1:2 broke solving for an hour in the field, and the same sky solved
    /// instantly at 1:1.</summary>
    public static Geometry From(double focalLengthMm, double pixelSizeUm,
                                int imageHeightPx, long sensorHeightPx) {
        if (focalLengthMm <= 0 || pixelSizeUm <= 0 || imageHeightPx <= 0)
            return new Geometry(0, 0, pixelSizeUm, 1);

        var pix = pixelSizeUm;
        var binning = 1;
        if (sensorHeightPx > imageHeightPx) {
            var factor = (double)sensorHeightPx / imageHeightPx;
            var nearest = (int)System.Math.Round(factor);
            // Only a clean integer reduction; anything else is a crop or a
            // subframe, where the pitch in the metadata is already right.
            if (nearest >= 2 && System.Math.Abs(factor - nearest) < 0.02) {
                pix *= nearest;
                binning = nearest;
            }
        }

        var scale = 206.2648 * pix / focalLengthMm;
        var sensorMm = pix * imageHeightPx / 1000.0;
        var fov = 2.0 * System.Math.Atan(sensorMm / (2.0 * focalLengthMm)) * (180.0 / System.Math.PI);
        return new Geometry(fov, scale, pix, binning);
    }

    /// <summary>Put the geometry on the options, leaving anything already set
    /// alone so an explicit caller still wins.</summary>
    public static void Apply(PlateSolveOptions options, Geometry g) {
        if (options == null) return;
        if (options.FovDeg <= 0 && g.FovDeg > 0) options.FovDeg = g.FovDeg;
        if (options.ScaleArcsecPerPixel <= 0 && g.ScaleArcsecPerPixel > 0)
            options.ScaleArcsecPerPixel = g.ScaleArcsecPerPixel;
    }

    /// <summary>Make sure the focal length reaches the FITS the solver reads.
    ///
    /// Frames saved to disk go through ImageWriterService, which stamps this;
    /// the temporary FITS written for a solve does not, so it reached ASTAP
    /// with 27 header cards and no FOCALLEN while the saved science frame next
    /// to it had 53 and the right value. With the keyword present a solver can
    /// derive the scale itself even when no hint is passed.</summary>
    public static void StampFocalLength(IImageData? image, double focalLengthMm) {
        if (image?.MetaData?.Telescope == null || focalLengthMm <= 0) return;
        if (image.MetaData.Telescope.FocalLength > 0) return;   // already known
        image.MetaData.Telescope.FocalLength = focalLengthMm;
    }
}
