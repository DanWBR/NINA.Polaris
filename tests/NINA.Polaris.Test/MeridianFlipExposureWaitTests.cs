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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.INDI.Client;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Alpaca;
using NINA.Polaris.Services.Simulator.Gear;

namespace NINA.Polaris.Test;

/// <summary>
/// A meridian flip must not slew mid-exposure. The mount won't move during a
/// capture anyway, and a slew then would throw the sub away, so the flip waits
/// for the current exposure to finish before it moves. Field, 2026-08-14: the
/// auto-flip fired from its own 20 s timer with no coordination with the 180 s
/// live-stack subs at all.
/// </summary>
[TestFixture]
public class MeridianFlipExposureWaitTests {

    private static MeridianFlipService BuildFlip(CaptureProgressService progress) {
        var cfg = new ConfigurationBuilder().Build();
        var indi = new IndiClient("localhost", 7624);
        var equip = new EquipmentManager(indi, NullLogger<EquipmentManager>.Instance,
            new AlpacaDiscoveryCache(), new SimGearService());
        var relay = new ImageRelayService(NullLogger<ImageRelayService>.Instance);
        var profile = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        var solver = new PlateSolveService(cfg, NullLogger<PlateSolveService>.Instance);
        var stream = new CameraStreamService(equip, relay,
            NullLogger<CameraStreamService>.Instance, new CaptureProgressService());
        var slew = new SlewCenterService(equip, solver, profile, stream,
            NullLogger<SlewCenterService>.Instance);
        var phd2 = new PHD2Client(NullLogger<PHD2Client>.Instance);
        var native = new NativeGuider(equip, profile, NullLogger<NativeGuider>.Instance);
        var guiders = new ActiveGuiderProvider(profile, phd2, native);
        var af = new AutoFocusService(equip, relay, guiders, profile,
            NullLogger<AutoFocusService>.Instance);
        return new MeridianFlipService(equip, guiders, slew, af, profile, progress,
            NullLogger<MeridianFlipService>.Instance);
    }

    [Test]
    public async Task ItReturnsImmediately_WhenNothingIsExposing() {
        var progress = new CaptureProgressService();
        var flip = BuildFlip(progress);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await flip.WaitForExposureIdleAsync(CancellationToken.None);
        sw.Stop();

        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(200),
            "no exposure in flight, so nothing to wait for");
    }

    [Test]
    public async Task ItBlocksWhileExposing_ThenReleasesWhenTheFrameEnds() {
        var progress = new CaptureProgressService();
        var flip = BuildFlip(progress);

        // An exposure is integrating.
        var scope = progress.Begin("live", exposureSeconds: 5);
        Assert.That(progress.Snapshot().Active, Is.True);

        var wait = Task.Run(() => flip.WaitForExposureIdleAsync(CancellationToken.None));

        // It must still be waiting while the frame is open.
        var finishedEarly = await Task.WhenAny(wait, Task.Delay(400)) == wait;
        Assert.That(finishedEarly, Is.False, "the flip must hold while the sub is still open");

        // Frame ends → the wait clears promptly.
        scope.Dispose();
        Assert.That(progress.Snapshot().Active, Is.False);
        var finishedNow = await Task.WhenAny(wait, Task.Delay(2000)) == wait;
        Assert.That(finishedNow, Is.True, "once the sub closes the flip may proceed");
    }

    [Test]
    public async Task ItDoesNotWaitForever_WhenACaptureIsWedged() {
        var progress = new CaptureProgressService();
        var flip = BuildFlip(progress);

        // A capture that never ends (driver wedged). The wait is bounded so the
        // flip can't be parked past the meridian limit; the cap is minutes, so
        // just prove the token cancels the wait promptly here.
        progress.Begin("live", exposureSeconds: 5);
        using var cts = new CancellationTokenSource(300);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await flip.WaitForExposureIdleAsync(cts.Token);
        sw.Stop();

        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(2000),
            "a cancelled token ends the wait without hanging");
    }
}
