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

using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// POLARUI2: displacement-to-target refinement math. The invariants:
///
///   1. Anchoring is self-consistent — decomposing the rotation from
///      the reference pointing to its own target must recover exactly
///      the axis error the target was built from.
///   2. The target RA/Dec is constant in time — solving the SAME
///      pointing RA/Dec minutes later must still report the full
///      original error (the whole point of storing the anchor as
///      RA/Dec instead of alt/az).
///   3. Partial knob corrections show proportional remaining error.
///   4. Zero error → target == reference.
///
/// Hemisphere cases use the operator's real site (lat −5.18, Mossoró)
/// plus a mirrored northern site, because the alt-knob sign flips with
/// the pole azimuth (rotation about the east axis raises north points
/// and LOWERS south points).
/// </summary>
[TestFixture]
public class PolarRefineTargetTests {

    static readonly DateTime T0 = new(2026, 7, 10, 22, 0, 0, DateTimeKind.Utc);

    // The operator's field-test pointing: RA 15.47h, Dec +24.7°.
    const double RefRa = 15.47, RefDec = 24.7;

    [TestCase(-5.18, -37.36, TestName = "RoundTrip_South")]
    [TestCase(30.0, -37.36, TestName = "RoundTrip_North")]
    public void Target_ThenError_RecoversInput(double lat, double lng) {
        // 10.7' az / -4.1' alt — same order as a rough tripod drop.
        const double azErr = 644.0, altErr = -246.0;

        var (tRa, tDec) = PolarAlignmentMath.ComputeRefineTarget(
            RefRa, RefDec, T0, azErr, altErr, lat, lng);
        var rem = PolarAlignmentMath.ComputeRefineError(
            tRa, tDec, RefRa, RefDec, T0, lat, lng);

        Assert.That(rem, Is.Not.Null);
        Assert.That(rem!.Value.azErrSec, Is.EqualTo(azErr).Within(1.0),
            "unadjusted pointing must report the full azimuth error");
        Assert.That(rem.Value.altErrSec, Is.EqualTo(altErr).Within(1.0),
            "unadjusted pointing must report the full altitude error");
    }

    [Test]
    public void Target_IsStable_MinutesLater() {
        // Under tracking the pointing keeps (approximately) the same
        // RA/Dec; the anchored target is a fixed RA/Dec too, so a
        // solve 10 minutes later must still report ≈ the full error.
        const double lat = -5.18, lng = -37.36;
        const double azErr = 644.0, altErr = -246.0;

        var (tRa, tDec) = PolarAlignmentMath.ComputeRefineTarget(
            RefRa, RefDec, T0, azErr, altErr, lat, lng);
        var rem = PolarAlignmentMath.ComputeRefineError(
            tRa, tDec, RefRa, RefDec, T0.AddMinutes(10), lat, lng);

        Assert.That(rem, Is.Not.Null);
        // The test models perfect TRUE-pole tracking (RA/Dec constant);
        // a real mount tracks about its OWN axis, where the anchor is
        // exact. The modelling gap ≈ totalError × sky rotation:
        // 690″ × sin(2.5°) ≈ 30″ here (~4% of the error), and it
        // shrinks quadratically as the user walks the error to zero —
        // irrelevant for knob guidance. Assert within that bound.
        Assert.That(rem!.Value.azErrSec, Is.EqualTo(azErr).Within(40.0));
        Assert.That(rem.Value.altErrSec, Is.EqualTo(altErr).Within(40.0));
    }

    [TestCase(-5.18, TestName = "HalfCorrection_South")]
    [TestCase(30.0, TestName = "HalfCorrection_North")]
    public void HalfCorrection_ReportsHalfError(double lat) {
        const double lng = -37.36;
        const double azErr = 600.0, altErr = -300.0;

        var (tRa, tDec) = PolarAlignmentMath.ComputeRefineTarget(
            RefRa, RefDec, T0, azErr, altErr, lat, lng);
        // Applying HALF the knob correction moves the pointing to the
        // target computed from half the error (same rotation, half
        // the angles).
        var (hRa, hDec) = PolarAlignmentMath.ComputeRefineTarget(
            RefRa, RefDec, T0, azErr / 2, altErr / 2, lat, lng);

        var rem = PolarAlignmentMath.ComputeRefineError(
            tRa, tDec, hRa, hDec, T0, lat, lng);

        Assert.That(rem, Is.Not.Null);
        // Az/alt rotations don't commute exactly: at these ~10' scales
        // the cross-term is ~1% of the error (≈3″), shrinking
        // quadratically as the error converges.
        Assert.That(rem!.Value.azErrSec, Is.EqualTo(azErr / 2).Within(6.0));
        Assert.That(rem.Value.altErrSec, Is.EqualTo(altErr / 2).Within(6.0));
    }

    [Test]
    public void FullCorrection_ReportsZero() {
        const double lat = -5.18, lng = -37.36;
        var (tRa, tDec) = PolarAlignmentMath.ComputeRefineTarget(
            RefRa, RefDec, T0, 644.0, -246.0, lat, lng);

        // Pointing arrived at the target ⇒ dot at the bullseye centre.
        var rem = PolarAlignmentMath.ComputeRefineError(
            tRa, tDec, tRa, tDec, T0, lat, lng);

        Assert.That(rem, Is.Not.Null);
        Assert.That(rem!.Value.azErrSec, Is.EqualTo(0).Within(0.01));
        Assert.That(rem.Value.altErrSec, Is.EqualTo(0).Within(0.01));
    }

    [Test]
    public void ZeroError_TargetEqualsReference() {
        const double lat = -5.18, lng = -37.36;
        var (tRa, tDec) = PolarAlignmentMath.ComputeRefineTarget(
            RefRa, RefDec, T0, 0, 0, lat, lng);
        Assert.That(tRa, Is.EqualTo(RefRa).Within(1e-6));
        Assert.That(tDec, Is.EqualTo(RefDec).Within(1e-6));
    }
}
