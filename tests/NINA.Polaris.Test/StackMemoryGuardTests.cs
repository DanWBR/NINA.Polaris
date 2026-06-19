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
using NINA.Polaris.Services.Studio;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the STUDIO pre-flight RAM guard. The tiled integrators bound peak
/// memory, but the output buffer + a frame's debayer/resample transient
/// still scale with sensor size; on a small SBC a big sensor can still
/// exhaust RAM. The guard must refuse those up front instead of letting the
/// OOM killer take the whole process — while never blocking a job when
/// memory is plentiful or unknown.
/// </summary>
[TestFixture]
public class StackMemoryGuardTests {

    private const long Mb = 1024 * 1024;

    [Test]
    public void Check_AllowsWhenAvailableUnknown() {
        // Metrics not sampled yet → fail-open, never block.
        var (ok, msg) = StackMemoryGuard.Check(8L * 1024 * Mb, 0, "do a huge job");
        Assert.That(ok, Is.True);
        Assert.That(msg, Is.Null);
    }

    [Test]
    public void Check_AllowsWhenItFitsWithinReserve() {
        // Needs 500 MB, 4 GB free, 256 MB reserved → fits.
        var (ok, msg) = StackMemoryGuard.Check(500 * Mb, 4096 * Mb, "integrate lights");
        Assert.That(ok, Is.True);
        Assert.That(msg, Is.Null);
    }

    [Test]
    public void Check_RefusesWhenItExceedsAvailableMinusReserve() {
        // 1.9 GB free, 256 MB reserved → ~1.64 GB budget; a 1.8 GB job
        // must be refused with an actionable message.
        var (ok, msg) = StackMemoryGuard.Check(1800 * Mb, 1900 * Mb, "integrate lights");
        Assert.That(ok, Is.False);
        Assert.That(msg, Does.Contain("Not enough memory"));
        Assert.That(msg, Does.Contain("integrate lights"));
    }

    [Test]
    public void Check_AccountsForTheReserve() {
        // Job needs exactly all free memory: must be refused because the
        // OS/process reserve is not available to it.
        var (ok, _) = StackMemoryGuard.Check(2048 * Mb, 2048 * Mb, "build the dark master");
        Assert.That(ok, Is.False);
    }

    [Test]
    public void EstimateLight_ColorPeaksOnAlignTransient() {
        // OSC: align holds ~7 planes (raw + 3 debayered + 3 resampled),
        // which dominates the integrate phase (3 planes output + budget).
        const int w = 6000, h = 4000;   // 24 MP
        long plane = (long)w * h * 2;
        long est = StackMemoryGuard.EstimateLightBytes(w, h, planes: 3, stripBudgetBytes: 96 * Mb);
        Assert.That(est, Is.EqualTo(plane * 7));
    }

    [Test]
    public void EstimateLight_MonoUsesIntegratePhaseWhenLarger() {
        // Mono align transient is ~2 planes; integrate is 1 plane + budget.
        // With a big budget the integrate phase wins.
        const int w = 1000, h = 1000;   // 1 MP → plane = 2 MB
        long plane = (long)w * h * 2;
        long budget = 96 * Mb;
        long est = StackMemoryGuard.EstimateLightBytes(w, h, planes: 1, stripBudgetBytes: budget);
        Assert.That(est, Is.EqualTo(plane + budget));     // integrate phase dominates
        Assert.That(est, Is.GreaterThan(plane * 2));      // ... and beats the align transient
    }

    [Test]
    public void EstimateMaster_IsOutputPlusBudget() {
        const int w = 6000, h = 4000;
        long est = StackMemoryGuard.EstimateMasterBytes(w, h, stripBudgetBytes: 96 * Mb);
        Assert.That(est, Is.EqualTo((long)w * h * 2 + 96 * Mb));
    }
}
