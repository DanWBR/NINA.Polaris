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

using NINA.Core.Enum;
using NINA.Polaris.Services;
using NUnit.Framework;
using O = NINA.Polaris.Services.MeridianFlipService.FlipOutcome;

namespace NINA.Polaris.Test;

/// <summary>A Star Adventurer GTi on EQMod, five minutes past the meridian:
/// the flip's re-slew moved 87 steps, the mount stayed on its side of the
/// pier, and "Meridian flip complete" was logged anyway.</summary>
[TestFixture]
public class MeridianFlipPierSideTests {
    [Test]
    public void TheSameSideAfterTheReSlewIsNotAFlip() {
        Assert.That(MeridianFlipService.Judge(PierSide.pierWest, PierSide.pierWest), Is.EqualTo(O.StayedOnSide));
        Assert.That(MeridianFlipService.Judge(PierSide.pierEast, PierSide.pierEast), Is.EqualTo(O.StayedOnSide));
    }

    [Test]
    public void AChangedSideIsAFlip() {
        Assert.That(MeridianFlipService.Judge(PierSide.pierWest, PierSide.pierEast), Is.EqualTo(O.Flipped));
        Assert.That(MeridianFlipService.Judge(PierSide.pierEast, PierSide.pierWest), Is.EqualTo(O.Flipped));
    }

    [Test]
    public void AMountThatDoesNotReportItsSideIsTakenAtItsWord() {
        Assert.That(MeridianFlipService.Judge(PierSide.pierUnknown, PierSide.pierUnknown), Is.EqualTo(O.Unverified));
        Assert.That(MeridianFlipService.Judge(PierSide.pierWest, PierSide.pierUnknown), Is.EqualTo(O.Unverified));
        Assert.That(MeridianFlipService.Judge(PierSide.pierUnknown, PierSide.pierEast), Is.EqualTo(O.Unverified));
    }
}
