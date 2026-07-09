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
/// Crossing-observation gate for the live-stacking auto meridian flip. The field
/// near-crash: a fresh SKY GoTo to a western target (Coma Berenices in the NW,
/// after homing from the South Pole) had HA already past the flip point, so the
/// auto-flip fired and tried to un-flip the mount toward the tripod. The gate
/// requires the target to actually cross the meridian WHILE stacking before a
/// flip may fire.
/// </summary>
[TestFixture]
public class MeridianFlipAutoLiveTests {

    // ---- crossing detection ----

    [Test]
    public void CrossedMeridianWest_FirstSample_False() {
        // No previous HA sample yet → no transition can be claimed.
        Assert.That(MeridianFlipAutoLiveService.CrossedMeridianWest(null, 0.1), Is.False);
    }

    [Test]
    public void CrossedMeridianWest_EastToWest_True() {
        // HA went from just east (−) to at/just west (≥0): a real crossing.
        Assert.That(MeridianFlipAutoLiveService.CrossedMeridianWest(-0.02, 0.0), Is.True);
        Assert.That(MeridianFlipAutoLiveService.CrossedMeridianWest(-0.5, 0.1), Is.True);
    }

    [Test]
    public void CrossedMeridianWest_StayingWest_False() {
        // Both samples west (target acquired already west, or well past): NOT a
        // fresh crossing — this is exactly the fresh-GoTo-to-NW-target case.
        Assert.That(MeridianFlipAutoLiveService.CrossedMeridianWest(0.2, 0.3), Is.False);
        Assert.That(MeridianFlipAutoLiveService.CrossedMeridianWest(1.0, 2.0), Is.False);
    }

    [Test]
    public void CrossedMeridianWest_StayingEast_False() {
        Assert.That(MeridianFlipAutoLiveService.CrossedMeridianWest(-2.0, -1.0), Is.False);
    }

    // ---- flip-due decision ----

    [Test]
    public void AutoFlipDue_NoCrossing_NeverFlips() {
        // The core fix: without an observed crossing, a target already past the
        // flip point (hoursUntilFlip <= 0) must NOT auto-flip.
        Assert.That(MeridianFlipAutoLiveService.AutoFlipDue(false, -0.1), Is.False);
        Assert.That(MeridianFlipAutoLiveService.AutoFlipDue(false, -1.0), Is.False);
        Assert.That(MeridianFlipAutoLiveService.AutoFlipDue(false, 0.0), Is.False);
    }

    [Test]
    public void AutoFlipDue_CrossedButNotYetDue_DoesNotFlip() {
        // Crossed the meridian but still short of the flip point (flip in the
        // future → positive hoursUntilFlip): wait.
        Assert.That(MeridianFlipAutoLiveService.AutoFlipDue(true, 0.05), Is.False);
    }

    [Test]
    public void AutoFlipDue_CrossedAndDue_Flips() {
        // Watched the crossing and now at/past the flip point: fire.
        Assert.That(MeridianFlipAutoLiveService.AutoFlipDue(true, 0.0), Is.True);
        Assert.That(MeridianFlipAutoLiveService.AutoFlipDue(true, -0.5), Is.True);
    }

    [Test]
    public void AutoFlipDue_CrossedButAbsurdlyFarWest_DoesNotFlip() {
        // Sanity bound: > ~6 h past the flip point is a stale / bogus state, not
        // a live flip window.
        Assert.That(MeridianFlipAutoLiveService.AutoFlipDue(true, -6.5), Is.False);
    }
}
