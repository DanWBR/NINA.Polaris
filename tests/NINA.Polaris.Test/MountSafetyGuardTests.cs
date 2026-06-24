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

[TestFixture]
public class MountSafetyGuardTests {

    // limit = 60 min past meridian throughout.
    private const double Limit = 60;

    // ---- cable-wrap (past-meridian) guard ----

    [Test]
    public void Meridian_EastOfMeridian_NeverTrips() {
        // HA negative = target still rising in the east; no winding risk.
        Assert.That(MountSafetyGuardService.ShouldTripMeridian(-2.0, true, false, Limit), Is.False);
        Assert.That(MountSafetyGuardService.ShouldTripMeridian(-0.5, false, false, Limit), Is.False);
    }

    [Test]
    public void Meridian_WithinLimit_DoesNotTrip() {
        // 0.5 h = 30 min past meridian, under the 60 min limit.
        Assert.That(MountSafetyGuardService.ShouldTripMeridian(0.5, false, false, Limit), Is.False);
        Assert.That(MountSafetyGuardService.ShouldTripMeridian(0.5, true, false, Limit), Is.False);
    }

    [Test]
    public void Meridian_StrainWavePastLimit_Trips() {
        // No pier-side support (AM3/AM5): a flip-less mount tracking 90 min
        // past the meridian is the exact incident scenario → trip.
        Assert.That(MountSafetyGuardService.ShouldTripMeridian(1.5, false, false, Limit), Is.True);
    }

    [Test]
    public void Meridian_GemPastLimitWithoutFlip_Trips() {
        // GEM, past the limit, pier side unchanged since crossing → didn't flip.
        Assert.That(MountSafetyGuardService.ShouldTripMeridian(1.5, true, flippedSinceCrossing: false, Limit), Is.True);
    }

    [Test]
    public void Meridian_GemPastLimitAfterFlip_NeverTrips() {
        // A healthy flipped GEM is safe regardless of how far west it tracks.
        Assert.That(MountSafetyGuardService.ShouldTripMeridian(1.5, true, flippedSinceCrossing: true, Limit), Is.False);
        Assert.That(MountSafetyGuardService.ShouldTripMeridian(5.0, true, flippedSinceCrossing: true, Limit), Is.False);
    }

    [Test]
    public void Meridian_LimitZero_DisablesGuard() {
        Assert.That(MountSafetyGuardService.ShouldTripMeridian(5.0, false, false, 0), Is.False);
    }

    // ---- guiding circuit breaker ----

    [Test]
    public void Breaker_BelowThreshold_DoesNotTrip() {
        Assert.That(MountSafetyGuardService.ShouldTripBreaker(19, 20), Is.False);
    }

    [Test]
    public void Breaker_AtOrAboveThreshold_Trips() {
        Assert.That(MountSafetyGuardService.ShouldTripBreaker(20, 20), Is.True);
        Assert.That(MountSafetyGuardService.ShouldTripBreaker(25, 20), Is.True);
    }

    [Test]
    public void Breaker_ThresholdZero_Disabled() {
        Assert.That(MountSafetyGuardService.ShouldTripBreaker(999, 0), Is.False);
    }
}
