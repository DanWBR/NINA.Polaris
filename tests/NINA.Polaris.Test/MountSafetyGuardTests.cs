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

    // ---- meridian-crossing detection (slew-gated) ----

    // The guard polls every 15 seconds, so a sample gap is a quarter minute and
    // the hour angle moves about 0.004 h in it.
    private const double Gap15s = 15.0 / 3600.0;

    [Test]
    public void Crossing_TrackingAcrossMeridian_Detected() {
        // HA drifts − → ≥0 while tracking: a real crossing.
        Assert.That(MountSafetyGuardService.DetectMeridianCrossing(-0.002, 0.002,
            slewing: false, hoursSincePrev: Gap15s), Is.True);
        Assert.That(MountSafetyGuardService.DetectMeridianCrossing(-0.001, 0.0,
            slewing: false, hoursSincePrev: Gap15s), Is.True);
    }

    [Test]
    public void Crossing_SlewAcrossMeridian_NotDetected() {
        // A commanded slew that carries HA across 0 (out of home to a west
        // target) must NOT register as a cable-wrap crossing.
        Assert.That(MountSafetyGuardService.DetectMeridianCrossing(-0.1, 1.0,
            slewing: true, hoursSincePrev: Gap15s), Is.False);
    }

    /// <summary>
    /// The slewing flag is not enough on its own. The guard polls every fifteen
    /// seconds while the mount is idle, so a GoTo that starts and finishes
    /// between two polls is never seen slewing: the previous sample is east,
    /// the next is west, and that read as a crossing. Starting a session on a
    /// target already past the meridian then tripped the cable-wrap guard
    /// within a tick of the first frame, with the mount on the correct side and
    /// nothing wound (field report).
    ///
    /// What separates them is the rate. The sky turns an hour of hour angle per
    /// hour; a mount being driven does not.
    /// </summary>
    [Test]
    public void Crossing_SlewBetweenTwoPolls_NotDetected() {
        Assert.That(MountSafetyGuardService.DetectMeridianCrossing(-3.0, 1.2,
            slewing: false, hoursSincePrev: Gap15s), Is.False,
            "four hours of hour angle in fifteen seconds is a GoTo, not the sky");
        Assert.That(MountSafetyGuardService.DetectMeridianCrossing(-0.2, 1.0,
            slewing: false, hoursSincePrev: Gap15s), Is.False);
    }

    /// <summary>A guard that missed several ticks must still see a real
    /// crossing when it comes back: the allowance scales with the gap.</summary>
    [Test]
    public void Crossing_AfterALongGapWhileTracking_StillDetected() {
        Assert.That(MountSafetyGuardService.DetectMeridianCrossing(-0.15, 0.02,
            slewing: false, hoursSincePrev: 10.0 / 60.0), Is.True,
            "ten minutes of real time and ten minutes of hour angle is tracking");
    }

    [Test]
    public void Crossing_AlreadyWest_NotDetected() {
        // Both samples west of the meridian: no − → ≥0 transition.
        Assert.That(MountSafetyGuardService.DetectMeridianCrossing(0.5, 1.0,
            slewing: false, hoursSincePrev: Gap15s), Is.False);
    }

    [Test]
    public void Crossing_StillEast_NotDetected() {
        Assert.That(MountSafetyGuardService.DetectMeridianCrossing(-2.0, -1.0,
            slewing: false, hoursSincePrev: Gap15s), Is.False);
    }

    [Test]
    public void Crossing_NoPriorSample_NotDetected() {
        Assert.That(MountSafetyGuardService.DetectMeridianCrossing(null, 0.1,
            slewing: false, hoursSincePrev: Gap15s), Is.False);
    }
}
