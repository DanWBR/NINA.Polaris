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

using System.Collections.Generic;
using NUnit.Framework;
using NINA.Image.ImageAnalysis;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// GUIDEREC: recovering the guide star after a cloud-out.
///
/// The field bug: while the star is missing the mount keeps tracking with no
/// corrections, so the star drifts. The per-frame search was a fixed 15 px window
/// around the lock, so once the drift exceeded that the star was never found
/// again — it could be sitting 30 px away, blazing, in a clear sky, and the loop
/// stayed in LostLock all night. The only cure was the user doing stop → loop →
/// start by hand, which re-detects on the full frame. "Isso é crítico para
/// processos de captura longa."
///
/// Two layers now: widen the window as frames are lost (recovers a drifted star,
/// keeps the original lock), then one full-frame re-acquisition (recovers a star
/// that left even the widened window).
///
/// These pin the maths, which is where the dangerous mistakes live: widen too far
/// and Find() locks a brighter NEIGHBOUR, and the resulting dx/dy jump is applied
/// as a correction that walks the target out of frame.
/// </summary>
[TestFixture]
public class NativeGuiderRecoveryTests {
    private const int Base = 15;
    private const int Max = 60;

    /// <summary>Guiding normally (and the first lost frame — could be a satellite
    /// or a gust) keeps the tight window. Widening on every blip would invite
    /// neighbour-locks for no reason.</summary>
    [Test]
    public void SearchRegion_WhileTracking_StaysTight() {
        Assert.That(NativeGuider.RecoverySearchRegionFor(0, Base, Max), Is.EqualTo(Base));
        Assert.That(NativeGuider.RecoverySearchRegionFor(1, Base, Max), Is.EqualTo(Base));
    }

    /// <summary>Sustained loss widens the window — the actual fix for a drifting
    /// star. One base-width every 2 lost frames.</summary>
    [Test]
    public void SearchRegion_WidensAsFramesAreLost() {
        Assert.That(NativeGuider.RecoverySearchRegionFor(2, Base, Max), Is.EqualTo(30));
        Assert.That(NativeGuider.RecoverySearchRegionFor(4, Base, Max), Is.EqualTo(45));
        Assert.That(NativeGuider.RecoverySearchRegionFor(6, Base, Max), Is.EqualTo(60));
    }

    /// <summary>THE safety property. The window must never grow without bound: a
    /// huge search hands Find() a whole field of stars and it returns the
    /// BRIGHTEST, not ours. Locking a neighbour mid-session is worse than staying
    /// lost — the jump gets applied as a correction.</summary>
    [Test]
    public void SearchRegion_IsCapped_NoMatterHowLongTheCloudLasts() {
        foreach (var lost in new[] { 8, 20, 100, 10_000 }) {
            Assert.That(NativeGuider.RecoverySearchRegionFor(lost, Base, Max), Is.EqualTo(Max),
                $"search must stay capped after {lost} lost frames");
        }
    }

    private static DetectedStar Star(double x, double y, double flux = 100, double peak = 1000)
        => new DetectedStar { X = x, Y = y, Flux = flux, Peak = peak, HFR = 2.0 };

    /// <summary>THE re-acquisition rule: NEAREST the old lock, not brightest.
    /// After a cloud the original star is the one that drifted a little. Picking
    /// the brightest (what AutoSelectStarAsync does when starting fresh) could
    /// grab a different star and silently re-frame the target mid-session.</summary>
    [Test]
    public void Reacquire_PicksNearestToOldLock_NotBrightest() {
        var stars = new List<DetectedStar> {
            Star(520, 500, flux: 9999),   // much brighter, 20px away
            Star(505, 500, flux: 10),     // our faint star, 5px away
        };
        var pick = NativeGuider.PickReacquireStar(stars, 500, 500, 150, 1000, 1000, 20, 65535);
        Assert.That(pick, Is.Not.Null);
        Assert.That(pick!.Value.X, Is.EqualTo(505).Within(0.01),
            "must re-lock the NEAREST star; brightest would re-frame the target");
    }

    /// <summary>Bounds how far one recovery may shift the pointing. A star beyond
    /// the radius is not our star.</summary>
    [Test]
    public void Reacquire_IgnoresStarsBeyondTheRadius() {
        var stars = new List<DetectedStar> { Star(800, 500) };   // 300px away
        var pick = NativeGuider.PickReacquireStar(stars, 500, 500, 150, 1000, 1000, 20, 65535);
        Assert.That(pick, Is.Null, "a star 300px away is not the one we lost");
    }

    /// <summary>Saturated stars are useless for centroiding — the same guard
    /// AutoSelectStarAsync applies. Must survive here or recovery would lock onto
    /// exactly the star that can't be measured.</summary>
    [Test]
    public void Reacquire_SkipsSaturatedStars() {
        var stars = new List<DetectedStar> {
            Star(502, 500, peak: 65000),          // nearest but saturated
            Star(515, 500, peak: 1000),           // usable
        };
        var pick = NativeGuider.PickReacquireStar(stars, 500, 500, 150, 1000, 1000, 20, 65535);
        Assert.That(pick, Is.Not.Null);
        Assert.That(pick!.Value.X, Is.EqualTo(515).Within(0.01));
    }

    /// <summary>A star hard against the frame edge can't hold a search window, so
    /// it can't be guided on even if it's nearest.</summary>
    [Test]
    public void Reacquire_SkipsStarsTooCloseToTheEdge() {
        var stars = new List<DetectedStar> {
            Star(5, 500),      // nearest to a lock at (10,500) but inside the margin
            Star(60, 500),
        };
        var pick = NativeGuider.PickReacquireStar(stars, 10, 500, 150, 1000, 1000, 20, 65535);
        Assert.That(pick, Is.Not.Null);
        Assert.That(pick!.Value.X, Is.EqualTo(60).Within(0.01));
    }

    /// <summary>Still clouded: nothing detected. Must return null (and the caller
    /// keeps looping) rather than invent a lock.</summary>
    [Test]
    public void Reacquire_NoStars_ReturnsNull() {
        var pick = NativeGuider.PickReacquireStar(
            new List<DetectedStar>(), 500, 500, 150, 1000, 1000, 20, 65535);
        Assert.That(pick, Is.Null);
    }
}
