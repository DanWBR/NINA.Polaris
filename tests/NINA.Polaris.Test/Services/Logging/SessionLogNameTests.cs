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

using NINA.Polaris.Services.Logging;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>On a Raspberry Pi 4 (no RTC) Polaris starts before NTP sets the
/// clock, and a 16 Oct session was filed as polaris_2026-10-01_133842.jsonl.
/// When the clock steps, the session file takes the start time the corrected
/// clock implies.</summary>
[TestFixture]
public class SessionLogNameTests {
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(2);

    [Test]
    public void ANtpStepForwardGivesTheRealStartTime() {
        var stale = new DateTime(2026, 10, 1, 13, 38, 42);      // clock at boot, before NTP
        var elapsed = TimeSpan.FromMinutes(3);
        var now = new DateTime(2026, 10, 16, 21, 3, 10);        // after the sync
        var start = LogRotatorService.CorrectedStart(stale, elapsed, now, Threshold);
        Assert.That(start, Is.EqualTo(new DateTime(2026, 10, 16, 21, 0, 10)));
        Assert.That(LogRotatorService.SessionFileName(start!.Value), Is.EqualTo("polaris_2026-10-16_210010.jsonl"));
    }

    [Test]
    public void AClockThatKeepsTimeIsLeftAlone() {
        var start = new DateTime(2026, 10, 16, 21, 0, 0);
        var elapsed = TimeSpan.FromHours(5);
        // NTP slews a few seconds over hours: not a step.
        Assert.That(LogRotatorService.CorrectedStart(start, elapsed, start + elapsed + TimeSpan.FromSeconds(4), Threshold),
            Is.Null);
    }

    [Test]
    public void AStepBackwardIsFollowedToo() {
        var start = new DateTime(2026, 10, 17, 9, 0, 0);
        var elapsed = TimeSpan.FromMinutes(10);
        var now = new DateTime(2026, 10, 17, 8, 10, 0);         // a clock that was ahead
        Assert.That(LogRotatorService.CorrectedStart(start, elapsed, now, Threshold),
            Is.EqualTo(new DateTime(2026, 10, 17, 8, 0, 0)));
    }
}
