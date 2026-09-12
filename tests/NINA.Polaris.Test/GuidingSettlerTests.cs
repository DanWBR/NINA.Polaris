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

using NINA.Guider.Portable;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The settle state machine behind every native dither and every start of
/// guiding: "within tolerance for N seconds, or give up after T".
///
/// The case these were written for: the guide loop only asked the settler on
/// frames that found the star. Lose the star mid-settle (a dither that pushes
/// it past the search window, a cloud) and nothing ever asked again, so
/// IsSettling stayed raised for as long as the star was gone. The operator saw
/// "Settling" on the guider frame with no way out, and the LIVE capture loop,
/// which will not start a sub while the guider is settling, sat parked behind
/// it long past the timeout it had been given. <see cref="GuidingSettler.Tick"/>
/// is the frame-without-a-star path, and the loop now calls it.
/// </summary>
[TestFixture]
public class GuidingSettlerTests {

    private const double Tol = 1.5;     // px
    private const double SettleSec = 10;
    private const double TimeoutSec = 40;
    private const long T0 = 1_000_000;

    private static GuidingSettler New() => new(Tol, SettleSec, TimeoutSec, T0);

    private static long At(double sec) => T0 + (long)(sec * 1000);

    [Test]
    public void WithinToleranceLongEnoughIsDone() {
        var s = New();
        Assert.That(s.Update(0.5, At(1)), Is.EqualTo(GuidingSettler.State.Settling));
        Assert.That(s.Update(0.5, At(5)), Is.EqualTo(GuidingSettler.State.Settling));
        Assert.That(s.Update(0.5, At(11)), Is.EqualTo(GuidingSettler.State.Done),
            "10 s continuously within tolerance, measured from the first in-tolerance frame");
    }

    [Test]
    public void LeavingToleranceRestartsTheClock() {
        var s = New();
        s.Update(0.5, At(1));
        s.Update(3.0, At(6));          // excursion
        Assert.That(s.Update(0.5, At(12)), Is.EqualTo(GuidingSettler.State.Settling),
            "the clock restarts at the first in-tolerance frame after the excursion");
        Assert.That(s.Update(0.5, At(21)), Is.EqualTo(GuidingSettler.State.Settling));
        Assert.That(s.Update(0.5, At(22)), Is.EqualTo(GuidingSettler.State.Done));
    }

    [Test]
    public void NeverSettlingTimesOut() {
        var s = New();
        Assert.That(s.Update(5.0, At(39)), Is.EqualTo(GuidingSettler.State.Settling));
        Assert.That(s.Update(5.0, At(41)), Is.EqualTo(GuidingSettler.State.TimedOut));
    }

    /// <summary>The defect, stated as the old contract: a settler that is never
    /// asked never times out. This is what left "Settling" on screen. It is
    /// kept as a test so the reason for <c>Tick</c> stays visible.</summary>
    [Test]
    public void ASettlerNobodyAsksNeverFinishes_WhichIsWhyTickExists() {
        var s = New();
        s.Update(5.0, At(1));
        // ...star lost, a whole night passes, no Update() calls...
        // The only way to learn it is over is to ask; Update needs a measurement
        // the loop does not have, Tick does not.
        Assert.That(s.Tick(At(3600)), Is.EqualTo(GuidingSettler.State.TimedOut));
    }

    /// <summary>Losing the star before the timeout keeps the settle open (the
    /// star may come back and settle in time), and closes it at the timeout.</summary>
    [Test]
    public void ALostStarRunsTheTimeoutButNotTheSettle() {
        var s = New();
        Assert.That(s.Update(5.0, At(1)), Is.EqualTo(GuidingSettler.State.Settling));
        Assert.That(s.Tick(At(20)), Is.EqualTo(GuidingSettler.State.Settling), "still inside the 40 s");
        Assert.That(s.Tick(At(39.9)), Is.EqualTo(GuidingSettler.State.Settling));
        Assert.That(s.Tick(At(40.1)), Is.EqualTo(GuidingSettler.State.TimedOut));
    }

    /// <summary>A frame with no star is not a frame within tolerance. Being
    /// within tolerance for 8 s, then losing the star for one frame, then being
    /// within tolerance again must NOT count as 10 s continuous.</summary>
    [Test]
    public void ALostStarResetsTheWithinToleranceClock() {
        var s = New();
        s.Update(0.5, At(1));
        s.Update(0.5, At(9));                    // 8 s within tolerance
        Assert.That(s.Tick(At(9.5)), Is.EqualTo(GuidingSettler.State.Settling));
        Assert.That(s.Update(0.5, At(11.5)), Is.EqualTo(GuidingSettler.State.Settling),
            "the 10 s must be continuous, and the lost frame broke it");
        Assert.That(s.BelowSeconds(At(11.5)), Is.EqualTo(0.0).Within(0.01),
            "within-tolerance time restarts at the first in-tolerance frame after the loss");
        Assert.That(s.Update(0.5, At(13.5)), Is.EqualTo(GuidingSettler.State.Settling));
        Assert.That(s.BelowSeconds(At(13.5)), Is.EqualTo(2.0).Within(0.01));
        Assert.That(s.Update(0.5, At(21.5)), Is.EqualTo(GuidingSettler.State.Done));
    }

    /// <summary>The star coming back after a loss settles normally, provided the
    /// timeout has not passed: the loss is not a failure by itself.</summary>
    [Test]
    public void AStarThatComesBackInTimeStillSettles() {
        var s = New();
        s.Update(5.0, At(1));
        s.Tick(At(5)); s.Tick(At(10)); s.Tick(At(15));
        Assert.That(s.Update(0.8, At(20)), Is.EqualTo(GuidingSettler.State.Settling));
        Assert.That(s.Update(0.8, At(30.5)), Is.EqualTo(GuidingSettler.State.Done));
    }

    /// <summary>Progress readout after a lost frame: elapsed keeps counting,
    /// within-tolerance time reads zero. This is what the settle bar shows.</summary>
    [Test]
    public void ProgressAfterALostFrameShowsElapsedAndNoToleranceTime() {
        var s = New();
        s.Update(0.5, At(1));
        s.Tick(At(7));
        Assert.Multiple(() => {
            Assert.That(s.ElapsedSeconds(At(7)), Is.EqualTo(7.0).Within(0.01));
            Assert.That(s.BelowSeconds(At(7)), Is.EqualTo(0.0));
            Assert.That(s.LastErrorPx, Is.EqualTo(0.5), "Tick has no measurement and leaves the last one");
        });
    }
}
