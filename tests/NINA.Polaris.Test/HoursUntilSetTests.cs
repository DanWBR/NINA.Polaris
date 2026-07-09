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
/// AltitudeService.HoursUntilSet — the "time until the target sets below the
/// horizon" countdown shown in the LIVE bar for targets already past the
/// meridian. Uses the standard rise/set hour-angle formula.
/// </summary>
[TestFixture]
public class HoursUntilSetTests {

    // A fixed instant so LST is deterministic.
    private static readonly DateTime Utc = new(2026, 3, 21, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    public void CircumpolarTarget_NeverSets_ReturnsNull() {
        // Observer at +50°, target at +80° dec: min altitude = 90-50-... stays
        // above the horizon all day (dec > 90 - lat = 40 ⇒ circumpolar).
        var t = AltitudeService.HoursUntilSet(6.0, 80.0, Utc, 50.0, 0.0);
        Assert.That(t, Is.Null);
    }

    [Test]
    public void NeverRises_ReturnsNull() {
        // Observer at +50°, target at −80° dec is always below the horizon
        // (dec < lat - 90 = -40) — no setting event.
        var t = AltitudeService.HoursUntilSet(6.0, -80.0, Utc, 50.0, 0.0);
        Assert.That(t, Is.Null);
    }

    [Test]
    public void Setting_IsPositiveAndWithinADay() {
        // An equatorial target from a mid-northern site does rise and set.
        var t = AltitudeService.HoursUntilSet(6.0, 0.0, Utc, 40.0, 0.0);
        Assert.That(t, Is.Not.Null);
        Assert.That(t!.Value, Is.GreaterThanOrEqualTo(0.0));
        Assert.That(t.Value, Is.LessThan(24.0));
    }

    [Test]
    public void JustPastMeridian_SetsInAboutHalfTheUpTime() {
        // Target on the meridian (HA≈0) at dec 0 from lat 0: it sets 6 sidereal
        // hours later (H_set = 90° = 6h). Put RA = LST so HA = 0.
        double lst = MeridianFlipService.ComputeLstHours(Utc, 0.0);
        var t = AltitudeService.HoursUntilSet(lst, 0.0, Utc, 0.0, 0.0);
        Assert.That(t, Is.Not.Null);
        // 6 sidereal h → ~5.98 solar h. Allow a small tolerance.
        Assert.That(t!.Value, Is.EqualTo(6.0 * 0.9972695663).Within(0.05));
    }

    [Test]
    public void DegenerateGeometry_ReturnsNull() {
        // Dec at the celestial pole ⇒ cos(dec)=0 ⇒ degenerate, guarded to null.
        var t = AltitudeService.HoursUntilSet(6.0, 90.0, Utc, 40.0, 0.0);
        Assert.That(t, Is.Null);
    }
}
