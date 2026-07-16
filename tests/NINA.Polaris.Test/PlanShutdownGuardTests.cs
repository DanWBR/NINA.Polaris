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
using NINA.Polaris.Services.Plan;

namespace NINA.Polaris.Test;

/// <summary>
/// FIELD7-3 (critical): a plan's "shut down the host at end of session" action
/// must fire ONLY when the run completed normally. The bug powered off the SBC
/// after a recoverable capture failure — reported as "completed" — while the INDI
/// watchdog was mid-restart with a healthy camera about to come back.
///
/// The whole decision is <see cref="PlanRunnerService.ShouldShutdownHost"/>. The
/// runner itself needs the engine + power + compiler graph to construct, so this
/// pins the extracted decision directly, which is where the regression lived.
/// </summary>
[TestFixture]
public class PlanShutdownGuardTests {
    /// <summary>Normal completion is the ONLY case that powers off.</summary>
    [Test]
    public void Shutdown_OnlyOnNormalCompletion() {
        Assert.That(PlanRunnerService.ShouldShutdownHost(
            endShutdownHost: true, userAborted: false, mainRunFailed: false), Is.True);
    }

    /// <summary>THE regression: a failed run must NOT shut down the host, no matter
    /// that EndShutdownHost is set. This is the line between "recoverable glitch"
    /// and "SBC powered off in a field, alone, at 2am".</summary>
    [Test]
    public void Shutdown_SuppressedWhenRunFailed() {
        Assert.That(PlanRunnerService.ShouldShutdownHost(
            endShutdownHost: true, userAborted: false, mainRunFailed: true), Is.False);
    }

    /// <summary>User stop already suppressed shutdown before this fix; it still
    /// must.</summary>
    [Test]
    public void Shutdown_SuppressedWhenUserAborted() {
        Assert.That(PlanRunnerService.ShouldShutdownHost(
            endShutdownHost: true, userAborted: true, mainRunFailed: false), Is.False);
    }

    /// <summary>Both at once (a failure the user then stopped): still no shutdown.</summary>
    [Test]
    public void Shutdown_SuppressedWhenAbortedAndFailed() {
        Assert.That(PlanRunnerService.ShouldShutdownHost(
            endShutdownHost: true, userAborted: true, mainRunFailed: true), Is.False);
    }

    /// <summary>The opt-out is honoured: no shutdown requested, no shutdown, ever.</summary>
    [Test]
    public void Shutdown_NeverWhenNotRequested() {
        Assert.That(PlanRunnerService.ShouldShutdownHost(false, false, false), Is.False);
        Assert.That(PlanRunnerService.ShouldShutdownHost(false, false, true), Is.False);
        Assert.That(PlanRunnerService.ShouldShutdownHost(false, true, false), Is.False);
    }
}
