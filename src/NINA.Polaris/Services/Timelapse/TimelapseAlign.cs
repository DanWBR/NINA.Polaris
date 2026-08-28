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

using NINA.Polaris.Services.Planetary;

namespace NINA.Polaris.Services.Timelapse;

/// <summary>
/// Frame centering for the time-lapse builder: work out how far to shift a
/// frame so a bright Sun/Moon disk sits at the image center. Pure and Skia-free
/// (takes a luminance buffer, returns an integer offset) so it's unit-testable;
/// the pixel shift itself is done by the caller.
///
/// Uses the same <see cref="CentroidAligner"/> the planetary stacker uses for a
/// bounded disc on sky (a partial-eclipse crescent included). There is no
/// circle/limb fit, so a deep crescent's centroid leans toward the bright side;
/// for the partial phases and a full disk it centers cleanly.
/// </summary>
public static class TimelapseAlign {
    /// <summary>Offset (dx, dy) that moves the bright subject's centroid to the
    /// image center. Returns (0,0) when there's nothing sensible to center:
    /// an empty/ambiguous frame (Find falls back to frame-center) or a
    /// frame-filling surface (no bounded disc to move).</summary>
    public static (int dx, int dy) CenterOffset(ushort[] lum, int width, int height) {
        if (lum == null || width <= 0 || height <= 0 || lum.Length < (long)width * height)
            return (0, 0);
        // A subject that fills the frame has no meaningful center to move to.
        if (CentroidAligner.FillFraction(lum, width, height) >= 0.85) return (0, 0);

        var c = CentroidAligner.Find(lum, width, height);
        int dx = (int)Math.Round(width / 2.0 - c.X);
        int dy = (int)Math.Round(height / 2.0 - c.Y);
        // Clamp so a spurious detection can't shove the whole frame off-canvas.
        dx = Math.Clamp(dx, -width, width);
        dy = Math.Clamp(dy, -height, height);
        return (dx, dy);
    }
}
