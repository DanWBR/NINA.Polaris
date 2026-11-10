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

using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>A ZWO AM5 with "After Meridian" = STOP and a 0° limit stopped at
/// the meridian, and the flip at 5 min past it was refused (2026-11-05 and
/// 2026-11-07).</summary>
[TestFixture]
public class MeridianFlipDriverLimitTests {
    [Test]
    public void StopAtTheMeridianIsWarned() {
        var w = MeridianFlipService.DriverStopsBeforeFlip(stopsAtLimit: true, limitDeg: 0, minutesAfterMeridian: 5);
        Assert.That(w, Does.Contain("After Meridian").And.Contain("TRACK"));
    }

    [Test]
    public void TrackIsNeverWarned() {
        Assert.That(MeridianFlipService.DriverStopsBeforeFlip(false, 0, 5), Is.Null);
        Assert.That(MeridianFlipService.DriverStopsBeforeFlip(false, -15, 5), Is.Null);
    }

    [Test]
    public void StopFarEnoughPastTheFlipIsFine() {
        // 15° is an hour past the meridian: room for the flip and its retries.
        Assert.That(MeridianFlipService.DriverStopsBeforeFlip(true, 15, 5), Is.Null);
        // 5° (20 min) covers a flip at 5 min plus the 15 min of retries, just.
        Assert.That(MeridianFlipService.DriverStopsBeforeFlip(true, 5, 5), Is.Null);
    }

    [Test]
    public void StopBeforeTheRetriesEndIsWarned() {
        Assert.That(MeridianFlipService.DriverStopsBeforeFlip(true, 4, 5), Is.Not.Null);
        Assert.That(MeridianFlipService.DriverStopsBeforeFlip(true, -5, 5), Is.Not.Null);
        // A later flip point needs a later limit.
        Assert.That(MeridianFlipService.DriverStopsBeforeFlip(true, 5, 10), Is.Not.Null);
    }
}
