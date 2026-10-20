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
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>An AM5 synced at 19:29 to Dec -25.29 reported -26.41 from about
/// 19:55 with nothing sent to it, while the live stack kept aligning frames a
/// few dither steps apart: it had dropped its sync.</summary>
[TestFixture]
public class PointingWatchdogTests {
    private const double Scale = 0.61; // "/px, SV550 + AE676C

    [Test]
    public void TheAm5DroppedSyncIsAJump() {
        double move = PointingWatchdogService.SeparationArcsec(0.79470, -25.2931, 0.78841, -26.4109);
        Assert.That(move, Is.EqualTo(4040).Within(40), "about 67 arcmin");
        Assert.That(PointingWatchdogService.IsCoordinateJump(move, fieldShiftPx: 24, Scale), Is.True);
    }

    [Test]
    public void ADitherIsNotAJump() {
        Assert.That(PointingWatchdogService.IsCoordinateJump(30, fieldShiftPx: 40, Scale), Is.False);
    }

    [Test]
    public void AMountThatReallyMovedIsNotAJump() {
        // 67' of mount travel with the field following it (a slew, a
        // recentre): the field shift says the telescope moved.
        Assert.That(PointingWatchdogService.IsCoordinateJump(4040, fieldShiftPx: 6600, Scale), Is.False);
    }

    [Test]
    public void WithoutAPixelScaleOnlyALargeJumpOverAStillFieldCounts() {
        Assert.Multiple(() => {
            Assert.That(PointingWatchdogService.IsCoordinateJump(4040, fieldShiftPx: 20, null), Is.True);
            Assert.That(PointingWatchdogService.IsCoordinateJump(700, fieldShiftPx: 20, null), Is.False);
            Assert.That(PointingWatchdogService.IsCoordinateJump(4040, fieldShiftPx: 400, null), Is.False);
        });
    }

    [Test]
    public void SeparationHandlesTheRaWrap() {
        Assert.That(PointingWatchdogService.SeparationArcsec(23.999, 0, 0.001, 0), Is.EqualTo(108).Within(1));
    }

    [Test]
    public void IndiLogFoldersOlderThanAWeekExpire() {
        var today = new DateTime(2026, 10, 19);
        Assert.Multiple(() => {
            Assert.That(MountDriverLogService.IsExpired("2026-10-11", today, 7), Is.True);
            Assert.That(MountDriverLogService.IsExpired("2026-10-12", today, 7), Is.False);
            Assert.That(MountDriverLogService.IsExpired("2026-10-19", today, 7), Is.False);
            Assert.That(MountDriverLogService.IsExpired("indi_lx200am5", today, 7), Is.False, "not a day folder");
        });
    }
}
