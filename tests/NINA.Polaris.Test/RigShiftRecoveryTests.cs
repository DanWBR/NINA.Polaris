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
using System.Collections.Generic;
using System.Linq;
using NINA.Core.Enum;
using NINA.Image.ImageAnalysis;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// An SV550 rig on an AM5 shifted by about 270 px over two minutes, no slew,
/// no flip. The guide star was lost and never re-acquired, a dither went out
/// with no star, and the live stack, unable to match the shifted frames,
/// accepted two of them as "meridian flipped" and stacked them upside down.
/// </summary>
[TestFixture]
public class RigShiftRecoveryTests {
    private const int Size = 3536;

    private static List<DetectedStar> Field(int seed, int n = 60) {
        var rng = new Random(seed);
        return Enumerable.Range(0, n).Select(i => new DetectedStar {
            X = 300 + rng.NextDouble() * (Size - 600),
            Y = 300 + rng.NextDouble() * (Size - 600),
            Flux = 10000 - i * 100, Peak = 5000, HFR = 2.0,
        }).ToList();
    }

    private static List<DetectedStar> Shift(List<DetectedStar> s, double dx, double dy)
        => s.Select(a => new DetectedStar { X = a.X + dx, Y = a.Y + dy, Flux = a.Flux, Peak = a.Peak, HFR = a.HFR }).ToList();

    private static List<DetectedStar> Rotate180(List<DetectedStar> s)
        => s.Select(a => new DetectedStar { X = Size - 1 - a.X, Y = Size - 1 - a.Y, Flux = a.Flux, Peak = a.Peak, HFR = a.HFR }).ToList();

    [Test]
    public void AFieldShiftedPastTheOldWideWindowStillAligns() {
        var reference = Field(1);
        var shifted = Shift(reference, 310, -40);
        Assert.That(StarMatcher.Match(reference, shifted, maxSearchRadius: 250.0, minVotes: 8), Is.Null,
            "beyond 250 px, which is where every frame used to be dropped");
        var t = StarMatcher.Match(reference, shifted, maxSearchRadius: 800.0, minVotes: 8);
        Assert.That(t, Is.Not.Null);
        Assert.That(t!.Tx, Is.EqualTo(-310).Within(2));
        Assert.That(t.Ty, Is.EqualTo(40).Within(2));
    }

    [Test]
    public void AShiftedFrameDoesNotMatchTheReferenceWhenRotated() {
        var reference = Field(2);
        var shifted = Shift(reference, 230, -20);
        foreach (var seed in Enumerable.Range(0, 20)) {
            var rotated = Rotate180(Shift(Field(2), 230 + seed, -20));
            Assert.That(StarMatcher.Match(reference, rotated, maxSearchRadius: 250.0, minVotes: 8), Is.Null,
                "a rotated copy of a shifted field is not a meridian flip");
        }
        Assert.That(StarMatcher.Match(reference, shifted, maxSearchRadius: 250.0, minVotes: 8), Is.Not.Null);
    }

    [Test]
    public void TheRotatedOrientationIsOnlyTriedWhenThePierSideAllowsIt() {
        Assert.Multiple(() => {
            Assert.That(LiveStackingService.MayProbeFlipped(PierSide.pierWest, PierSide.pierWest, false), Is.False,
                "same side, not yet flipped: a rotated match is a coincidence");
            Assert.That(LiveStackingService.MayProbeFlipped(PierSide.pierWest, PierSide.pierEast, false), Is.True);
            Assert.That(LiveStackingService.MayProbeFlipped(PierSide.pierWest, PierSide.pierWest, true), Is.True,
                "already stacking rotated frames: the mount may flip back");
            Assert.That(LiveStackingService.MayProbeFlipped(PierSide.pierUnknown, PierSide.pierWest, false), Is.True,
                "a mount that does not report its side leaves it to the stars");
            Assert.That(LiveStackingService.MayProbeFlipped(PierSide.pierEast, PierSide.pierUnknown, false), Is.True);
        });
    }

    [Test]
    public void ReacquisitionRetriesWhileTheStarStaysLost() {
        Assert.Multiple(() => {
            Assert.That(NativeGuider.ShouldReacquire(9, long.MaxValue), Is.False, "not lost long enough");
            Assert.That(NativeGuider.ShouldReacquire(10, long.MaxValue), Is.True, "first attempt");
            Assert.That(NativeGuider.ShouldReacquire(25, 5_000), Is.False, "too soon after the last attempt");
            Assert.That(NativeGuider.ShouldReacquire(25, 15_000), Is.True, "and again while still lost");
        });
    }
}
