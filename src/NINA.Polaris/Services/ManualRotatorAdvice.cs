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

/// <summary>What to tell someone holding a manual rotator: how far to turn
/// the camera, and which way, to reach the framing angle they set on the SKY
/// map.
///
/// A motorised rotator gets driven by <see cref="SlewCenterService"/>: solve,
/// move by the difference, solve again. A manual one (a camera rotator ring,
/// a CAA turned by hand) cannot be driven, so the loop is the operator: we
/// measure, they turn, we measure again. That makes the direction word the
/// whole product, and the direction is not derivable from the solve alone:
/// the sky angle grows counter-clockwise as seen from behind the camera, but
/// each reflection in the optical train flips that, and the operator's own
/// point of view is not something a solve can know. So the parity from the
/// solve's CD matrix sets the default, and the caller can reverse it: the
/// motorised path has to learn its sign the same way.
/// </summary>
public static class ManualRotatorAdvice {

    public const double DefaultToleranceDeg = 1.0;

    /// <summary>Turn instruction for one measurement.</summary>
    /// <param name="SolvedPa">Camera position angle the solve reported.</param>
    /// <param name="TargetPa">Framing angle the operator asked for.</param>
    /// <param name="DeltaDeg">Signed change in sky angle still needed,
    /// folded into (-90, 90].</param>
    /// <param name="TurnDeg">How far to turn, always positive.</param>
    /// <param name="Direction"><c>cw</c>, <c>ccw</c>, or <c>none</c> when it
    /// is already close enough to leave alone.</param>
    /// <param name="Mirrored">The optical train flips the field (a star
    /// diagonal, an odd number of reflections), taken from the CD matrix.
    /// This is what decides the default direction.</param>
    /// <param name="Reversed">The per-rig override was in force.</param>
    public record Advice(
        double SolvedPa,
        double TargetPa,
        double DeltaDeg,
        double TurnDeg,
        string Direction,
        bool WithinTolerance,
        bool Mirrored,
        bool Reversed);

    /// <summary>
    /// Fold target minus solved into (-90, 90]. Same rule as
    /// <see cref="SlewCenterService.RotationErrorDeg"/>: a rectangular sensor
    /// turned by 180 degrees frames the same field, so the shorter way round
    /// is always the answer. It matters more by hand than by motor, because
    /// nobody wants to be told to turn a camera 170 degrees when 10 the other
    /// way gives the same picture.
    /// </summary>
    public static double SkyDeltaDeg(double targetPa, double solvedPa)
        => SlewCenterService.RotationErrorDeg(targetPa, solvedPa);

    /// <summary>True when the field is mirrored, i.e. the determinant of the
    /// CD matrix is positive. A normal astronomical frame has north up and
    /// east left, which makes the determinant negative; one reflection flips
    /// it. Unknown (a solver that reports no CD matrix) counts as not
    /// mirrored, and the reverse override exists for exactly that case.
    /// </summary>
    public static bool IsMirrored(double? cd11, double? cd12, double? cd21, double? cd22) {
        if (cd11 is not double a || cd12 is not double b
                || cd21 is not double c || cd22 is not double d) return false;
        var det = a * d - b * c;
        return det > 0;
    }

    public static Advice Compute(double targetPa, double solvedPa,
            double? cd11 = null, double? cd12 = null, double? cd21 = null, double? cd22 = null,
            bool reverse = false, double toleranceDeg = DefaultToleranceDeg) {
        var delta = SkyDeltaDeg(targetPa, solvedPa);
        var turn = Math.Abs(delta);
        var mirrored = IsMirrored(cd11, cd12, cd21, cd22);
        var tol = toleranceDeg > 0 ? toleranceDeg : DefaultToleranceDeg;

        // Looking at the back of the camera, towards the sky: north is up and
        // east is to the left, so position angle (north towards east) grows
        // anti-clockwise. A mirrored train puts east on the right and the
        // sense flips with it.
        var ccw = delta > 0;
        if (mirrored) ccw = !ccw;
        if (reverse) ccw = !ccw;

        var within = turn <= tol;
        return new Advice(
            SolvedPa: solvedPa,
            TargetPa: targetPa,
            DeltaDeg: delta,
            TurnDeg: turn,
            Direction: within ? "none" : (ccw ? "ccw" : "cw"),
            WithinTolerance: within,
            Mirrored: mirrored,
            Reversed: reverse);
    }
}
