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

namespace NINA.Polaris.Services;

/// <summary>
/// Projects sky coordinates (RA/Dec) onto image pixel coordinates given a
/// plate-solve solution (field centre, pixel scale, rotation). Used by the
/// "Annotate" feature to place DSO labels over a solved LIVE/PREVIEW frame.
///
/// Uses the gnomonic (TAN) projection — the standard for the small fields a
/// telescope sees. Pixel convention (rotation 0, no flip): north is up, east
/// is left, matching a normal non-mirrored image with the sky's north at the
/// top. <paramref name="rotationDeg"/> rotates the sky frame into the image;
/// <paramref name="flip"/> mirrors horizontally for setups that produce a
/// mirrored image (e.g. a star diagonal / certain drivers).
/// </summary>
public static class AnnotationProjector {
    private const double ArcsecPerRadian = 206264.806247;

    /// <summary>
    /// Project a sky point to image pixels. Returns null when the point is more
    /// than 90° from the field centre (behind the tangent plane).
    /// </summary>
    /// <param name="extraRotationDeg">Additional rotation (degrees) added to the
    /// solver's <paramref name="rotationDeg"/> before mapping sky axes to image
    /// axes. Used to reconcile solver rotation conventions that differ by a
    /// quadrant (90/180/270) from this projector's north-up/east-left frame, and
    /// as a field-test knob until the right offset is nailed down.</param>
    public static (double x, double y)? Project(
            double centerRaHours, double centerDecDeg,
            double scaleArcsecPerPixel, double rotationDeg,
            int width, int height, bool flip,
            double raHours, double decDeg,
            double extraRotationDeg = 0) {
        if (scaleArcsecPerPixel <= 0) return null;

        double ra0 = centerRaHours * Math.PI / 12.0;
        double dec0 = centerDecDeg * Math.PI / 180.0;
        double ra = raHours * Math.PI / 12.0;
        double dec = decDeg * Math.PI / 180.0;

        double dra = ra - ra0;
        double sinDec0 = Math.Sin(dec0), cosDec0 = Math.Cos(dec0);
        double sinDec = Math.Sin(dec), cosDec = Math.Cos(dec);
        double cosDra = Math.Cos(dra), sinDra = Math.Sin(dra);

        double cosc = sinDec0 * sinDec + cosDec0 * cosDec * cosDra;
        if (cosc <= 0) return null;   // > 90° away

        // Standard coordinates (radians): ξ toward +RA (east), η toward +Dec (north).
        double xi = cosDec * sinDra / cosc;
        double eta = (cosDec0 * sinDec - sinDec0 * cosDec * cosDra) / cosc;

        // To pixels (east / north offsets from centre).
        double xiPx = xi * ArcsecPerRadian / scaleArcsecPerPixel;
        double etaPx = eta * ArcsecPerRadian / scaleArcsecPerPixel;

        // Rotate the sky axes into the image axes by the solve rotation (plus
        // any test/convention offset).
        double th = (rotationDeg + extraRotationDeg) * Math.PI / 180.0;
        double cos = Math.Cos(th), sin = Math.Sin(th);
        double xr = xiPx * cos - etaPx * sin;
        double yr = xiPx * sin + etaPx * cos;

        // North up, east left (mirror flips the east direction); canvas Y grows
        // downward so north maps to a smaller Y.
        double px = width / 2.0 - (flip ? -xr : xr);
        double py = height / 2.0 - yr;
        return (px, py);
    }
}
