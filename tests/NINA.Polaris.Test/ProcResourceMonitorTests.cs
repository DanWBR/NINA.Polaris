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

namespace NINA.Polaris.Test;

/// <summary>
/// Host CPU read from /proc/stat, which replaced the resource-monitoring
/// package on Linux. The package's cgroup v2 parser threw from its own
/// constructor when `memory.current - inactive_file` came out negative, an
/// idle desktop does that routinely, and the throw killed the server before
/// it ever listened: systemd restarted it every five seconds and the operator
/// saw a browser that reconnected and dropped for ever. Reported from a fresh
/// Lubuntu 26.04 as a network problem, reproduced here on Ubuntu x64.
/// </summary>
[TestFixture]
public class ProcResourceMonitorTests {

    [Test]
    public void ParsesTheAggregateLineOfProcStat() {
        // Real line from an idle machine.
        const string line = "cpu  123456 789 45678 9876543 2345 0 1234 0 0 0";
        var (total, idle) = ProcResourceMonitor.ParseCpuLine(line);
        // total is every field summed; idle is idle + iowait
        Assert.That(total, Is.EqualTo(123456UL + 789 + 45678 + 9876543 + 2345 + 0 + 1234));
        Assert.That(idle, Is.EqualTo(9876543UL + 2345));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("intr 12345 0 0")]
    [TestCase("cpu")]
    public void RefusesAnythingThatIsNotTheCpuLine(string? line) {
        var (total, idle) = ProcResourceMonitor.ParseCpuLine(line);
        Assert.That(total, Is.Zero);
        Assert.That(idle, Is.Zero);
    }

    [Test]
    public void TheFirstReadingReportsZero() {
        // Nothing to compare against yet: one tick at 0 is correct, and much
        // better than inventing a number.
        Assert.That(ProcResourceMonitor.BusyPercent(0, 0, 1000, 900), Is.Zero);
    }

    [Test]
    public void HalfIdleIsFiftyPercent() {
        // 200 ticks passed, 100 of them idle.
        Assert.That(ProcResourceMonitor.BusyPercent(1000, 500, 1200, 600), Is.EqualTo(50).Within(1e-9));
    }

    [Test]
    public void FullyIdleIsZeroAndFullyBusyIsHundred() {
        Assert.That(ProcResourceMonitor.BusyPercent(1000, 500, 1100, 600), Is.EqualTo(0).Within(1e-9));
        Assert.That(ProcResourceMonitor.BusyPercent(1000, 500, 1100, 500), Is.EqualTo(100).Within(1e-9));
    }

    [Test]
    public void CountersGoingBackwardsDoNotProduceNonsense() {
        // A counter reset (or a wrap) must not surface as a negative or a
        // wild percentage on the activity bar.
        Assert.That(ProcResourceMonitor.BusyPercent(5000, 4000, 1000, 500), Is.Zero);
        Assert.That(ProcResourceMonitor.BusyPercent(1000, 900, 1100, 800),
                    Is.InRange(0, 100), "idle going backwards is clamped, not negative");
    }

    [Test]
    public void IoWaitCountsAsIdle() {
        // A board blocked on an SD card write is not busy. Counting iowait as
        // work showed a Pi doing nothing at 100%.
        var (_, idleNoWait) = ProcResourceMonitor.ParseCpuLine("cpu 100 0 100 800 0 0 0");
        var (_, idleWithWait) = ProcResourceMonitor.ParseCpuLine("cpu 100 0 100 800 200 0 0");
        Assert.That(idleWithWait, Is.EqualTo(idleNoWait + 200));
    }
}
