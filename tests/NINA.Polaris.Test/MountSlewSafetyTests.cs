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
using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// Covers the pure mount-slew safety decisions that guard against the AM3
/// near tripod-strike: skip a redundant GoTo when already on target, flag a
/// large / near-meridian slew or a below-floor target for confirmation, and
/// abort a slew whose OTA drops below the altitude floor.
/// </summary>
[TestFixture]
public class MountSlewSafetyTests {

    // A site + time where the math is exercised; the specific values only need
    // to be internally consistent for the relative checks below.
    private const double Lat = -5.79;   // ~Natal, BR
    private const double Lon = -35.2;
    private static readonly DateTime Utc = new(2026, 7, 6, 3, 0, 0, DateTimeKind.Utc);

    // ---- angular separation ----

    [Test]
    public void Separation_SamePoint_IsZero() {
        Assert.That(MountSlewSafety.AngularSeparationDeg(5.0, 20.0, 5.0, 20.0),
            Is.EqualTo(0).Within(1e-9));
    }

    [Test]
    public void Separation_OneHourRa_OnEquator_Is15Deg() {
        // 1h of RA at the equator == 15°.
        Assert.That(MountSlewSafety.AngularSeparationDeg(5.0, 0.0, 6.0, 0.0),
            Is.EqualTo(15.0).Within(1e-6));
    }

    // ---- altitude-floor abort (anti-crash) ----

    [Test]
    public void AltAbort_OnlyWhenSlewingBelowFloor() {
        Assert.IsTrue(MountSlewSafety.ShouldAbortForAltitude(3, 5, isSlewing: true),
            "below floor while slewing → abort");
        Assert.IsFalse(MountSlewSafety.ShouldAbortForAltitude(3, 5, isSlewing: false),
            "not slewing (parked/idle low) → never abort");
        Assert.IsFalse(MountSlewSafety.ShouldAbortForAltitude(30, 5, isSlewing: true),
            "well above floor → no abort");
    }

    [Test]
    public void AltAbort_FloorZeroDisables() {
        Assert.IsFalse(MountSlewSafety.ShouldAbortForAltitude(-10, 0, isSlewing: true));
    }

    [Test]
    public void AltAbort_NaNAltitude_DoesNotAbort() {
        Assert.IsFalse(MountSlewSafety.ShouldAbortForAltitude(double.NaN, 5, isSlewing: true));
    }

    // ---- pre-slew evaluation ----

    [Test]
    public void Evaluate_AlreadyOnTarget_SkipsAndDoesNotWarnOnMeridian() {
        // Mount sitting essentially on the target → AlreadyOnTarget, and the
        // near-meridian flag is suppressed (no fresh GoTo to un-flip).
        double ra = 5.0, dec = 20.0;
        var v = MountSlewSafety.Evaluate(ra, dec, ra + 0.02, dec + 0.02,
            Lat, Lon, Utc, minAltFloorDeg: 0);
        Assert.IsTrue(v.AlreadyOnTarget);
        // A ~0.4° move well above the floor with no size/meridian trigger:
        Assert.IsFalse(v.Warn);
    }

    [Test]
    public void Evaluate_LargeMove_Warns() {
        // ~90° move → flagged.
        var v = MountSlewSafety.Evaluate(0.0, 0.0, 6.0, 0.0,
            Lat, Lon, Utc, minAltFloorDeg: 0);
        Assert.IsFalse(v.AlreadyOnTarget);
        Assert.IsTrue(v.Warn);
        Assert.That(v.MoveDeg, Is.GreaterThan(MountSlewSafety.LargeMoveDeg));
    }

    [Test]
    public void Evaluate_NoMountPosition_SkipsMoveTests() {
        // NaN mount position → move-size + already-on-target tests skipped; a
        // small nearby target with the floor off should not warn purely on size.
        var v = MountSlewSafety.Evaluate(double.NaN, double.NaN, 5.0, 80.0,
            Lat, Lon, Utc, minAltFloorDeg: 0);
        Assert.IsFalse(v.AlreadyOnTarget);
        Assert.That(v.MoveDeg, Is.NaN);
    }

    [Test]
    public void Evaluate_BelowAltFloor_Warns() {
        // A target below the horizon (dec far south from a southern site can
        // still be up; force it below by choosing a target near the anti-zenith).
        // Simpler: pick a floor high enough that any target trips it, and assert
        // the flag mechanics.
        var v = MountSlewSafety.Evaluate(5.0, 20.0, 5.0, 20.0,
            Lat, Lon, Utc, minAltFloorDeg: 90);   // nothing is above 90°
        Assert.IsTrue(v.BelowAltFloor);
        Assert.IsTrue(v.Warn);
    }

    [Test]
    public void TargetHaMinutes_Wraps() {
        // LST just past the target RA → small positive HA (west), in minutes.
        double ha = MountSlewSafety.TargetHaMinutes(targetRaHours: 5.0, lstHours: 5.1);
        Assert.That(ha, Is.EqualTo(6.0).Within(1e-6));   // 0.1h = 6 min
        // Wrap across 24h.
        double wrapped = MountSlewSafety.TargetHaMinutes(targetRaHours: 23.9, lstHours: 0.1);
        Assert.That(wrapped, Is.EqualTo(12.0).Within(1e-6));   // 0.2h = 12 min
    }
}
