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

namespace NINA.Polaris.Services.Planetary;

/// <summary>
/// Brightest-region centroid finder with sub-pixel parabolic refinement.
/// Used to align planetary frames before stacking. Works well for bright
/// targets: the Moon, Jupiter, Mars, Saturn's body.
///
/// Algorithm: threshold the frame between its background and its peak, then
/// take the intensity-weighted centroid of everything above that line.
///
/// It used to track the BRIGHTEST PIXEL instead, which works for a small planet
/// on a dark sky and fails completely on an extended one. Measured on a 652
/// frame lunar SER from the field: the peak pixel alternated between two spots
/// on the lunar surface 670 px apart, frame to frame, because thousands of
/// pixels sit within noise of the maximum and which one wins is chance. Every
/// frame was then shifted by that, and the stack came out as two Moons with a
/// hard seam where the coverage count changed. The disc's own centroid over the
/// same frames moved 35 px in total, smoothly.
///
/// The threshold is relative to the frame's own range, so it needs no tuning
/// per target: a planet on black sky and a full Moon both end up with the
/// illuminated area selected. For a gibbous phase the centroid of the LIT area
/// is not the disc centre, which does not matter: alignment only needs the same
/// measure on every frame.
///
/// Returns (cx, cy) in pixel coordinates, sub-pixel by construction.
/// </summary>
public static class CentroidAligner {

    public record Centroid(double X, double Y);

    /// <summary>Fraction of the (peak - background) range a pixel must clear to
    /// count as target. Low enough to keep a dim planet's whole disc, high
    /// enough to exclude sky glow and the Moon's earthshine halo.</summary>
    private const double ThresholdFraction = 0.25;

    public static Centroid Find(ushort[] pixels, int width, int height) {
        if (pixels == null || pixels.Length != width * height || width < 3 || height < 3)
            return new Centroid(width / 2.0, height / 2.0);

        // Peak and background from one pass. The background is the frame's
        // minimum over a stride sample: on a planetary capture most of the
        // frame IS background, so a sample finds it without sorting 3.7 M
        // pixels per frame.
        ushort peak = 0;
        ushort background = ushort.MaxValue;
        for (int i = 0; i < pixels.Length; i++) {
            var v = pixels[i];
            if (v > peak) peak = v;
        }
        for (int i = 0; i < pixels.Length; i += 97) {   // prime stride, ~40 k samples
            var v = pixels[i];
            if (v < background) background = v;
        }
        if (peak <= background) return new Centroid(width / 2.0, height / 2.0);

        double threshold = background + ThresholdFraction * (peak - background);

        // Intensity-weighted first moments over everything above the line.
        // Weighting by (value - threshold) rather than by value keeps the
        // background's residual from dragging the centroid toward the frame
        // centre when the target is small.
        double sumW = 0, sumX = 0, sumY = 0;
        long above = 0;
        for (int y = 0; y < height; y++) {
            int row = y * width;
            for (int x = 0; x < width; x++) {
                double v = pixels[row + x];
                if (v <= threshold) continue;
                double w = v - threshold;
                sumW += w;
                sumX += w * x;
                sumY += w * y;
                above++;
            }
        }

        // Nothing cleared the line (a blank or all-saturated frame): the frame
        // centre is a stable answer, and a stable wrong answer costs one frame
        // of alignment error rather than throwing the stack off by half a
        // field the way a random peak pixel does.
        if (above == 0 || sumW <= 0) return new Centroid(width / 2.0, height / 2.0);

        return new Centroid(sumX / sumW, sumY / sumW);
    }
}
