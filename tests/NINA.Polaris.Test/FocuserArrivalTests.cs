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
using W = NINA.Polaris.Services.AutoFocusService.FocuserWait;

namespace NINA.Polaris.Test;

/// <summary>An autofocus night with a Gemini EAF: it stopped 3 or 4 steps off
/// each target (6675 → 6678, 6625 → 6629) and the run sat 60 s on every
/// point; later, its serial link timing out, it was asked for 6645, 6595,
/// 6545… and never left 6445, again 60 s per point before failing.</summary>
[TestFixture]
public class FocuserArrivalTests {
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(0.3);
    private static readonly TimeSpan Stable = TimeSpan.FromSeconds(2);

    [Test]
    public void AtTheTargetAndStoppedIsArrived() {
        Assert.That(AutoFocusService.Decide(6525, 6675, 6676, moving: false, Short, Short), Is.EqualTo(W.Arrived));
    }

    [Test]
    public void StillTravellingIsWaitedFor() {
        Assert.That(AutoFocusService.Decide(6525, 6675, 6600, moving: true, Short, Short), Is.EqualTo(W.Wait));
        Assert.That(AutoFocusService.Decide(6525, 6675, 6675, moving: true, Short, Short), Is.EqualTo(W.Wait));
    }

    [Test]
    public void StoppedAFewStepsOffIsMeasuredWhereItIs() {
        // 6675 asked, 6678 reached and stable: carry on from 6678.
        Assert.That(AutoFocusService.Decide(6525, 6675, 6678, moving: false, Stable, Stable), Is.EqualTo(W.StoppedElsewhere));
    }

    [Test]
    public void AStopIsOnlyBelievedOnceThePositionHoldsStill() {
        // A driver that reports Idle mid-travel: the position is still changing.
        Assert.That(AutoFocusService.Decide(6525, 6675, 6640, moving: false, Stable, Short), Is.EqualTo(W.Wait));
    }

    [Test]
    public void AFocuserThatNeverLeftItsStartIsReportedAfterTheGrace() {
        Assert.That(AutoFocusService.Decide(6445, 6645, 6445, moving: false, TimeSpan.FromSeconds(3), Stable), Is.EqualTo(W.Wait),
            "a slow start gets its grace");
        Assert.That(AutoFocusService.Decide(6445, 6645, 6445, moving: false, TimeSpan.FromSeconds(9), Stable), Is.EqualTo(W.NeverMoved));
    }
}
