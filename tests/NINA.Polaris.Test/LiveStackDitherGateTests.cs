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
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.INDI.Client;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// A skipped dither is not a dither.
///
/// From the field on 2026-08-10, to the millisecond:
///
///     23:43:33.545  live stack starts processing frame 9
///     23:43:37.886  native guider: guide capture exceeded its 9000 ms budget
///     23:43:37.887  native guider alert: guide star lost
///     23:43:37.927  frame 9 added, and the dither check runs
///
/// The check read IsGuiding forty milliseconds after a dropped BLOB knocked the
/// guider off its star, took the "not guiding" branch, and set the gate to
/// frame 9 as though a dither had happened. With "every 3 frames" the session
/// dithered at 3, 6 and then not again until 12: three minutes of 60-second
/// subs on the same pixels, over a 40 ms window.
///
/// These drive the real handler chain rather than a copy of the rule, because
/// the bug was never in the arithmetic. It was in what the failure path wrote.
/// </summary>
[TestFixture]
public class LiveStackDitherGateTests {

    private LiveStackingService _stack = null!;
    private LiveStackTriggersService _triggers = null!;
    private NativeGuider _guider = null!;

    [SetUp]
    public void SetUp() {
        var cfg = new ConfigurationBuilder().Build();
        var relay = new ImageRelayService(NullLogger<ImageRelayService>.Instance);
        _stack = new LiveStackingService(relay, NullLogger<LiveStackingService>.Instance);
        var profiles = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        profiles.ActiveEquipmentProfile.GuiderDriver = "native";
        var indi = new IndiClient("localhost", 7624);
        var equip = new EquipmentManager(indi, NullLogger<EquipmentManager>.Instance,
            new NINA.Polaris.Services.Alpaca.AlpacaDiscoveryCache(),
            new NINA.Polaris.Services.Simulator.Gear.SimGearService());
        var solver = new PlateSolveService(cfg, NullLogger<PlateSolveService>.Instance);
        var stream = new CameraStreamService(equip, relay,
            NullLogger<CameraStreamService>.Instance, new CaptureProgressService());
        var slew = new SlewCenterService(equip, solver, profiles, stream,
            NullLogger<SlewCenterService>.Instance);
        var phd2 = new PHD2Client(NullLogger<PHD2Client>.Instance);
        _guider = new NativeGuider(equip, profiles, NullLogger<NativeGuider>.Instance);
        var guiders = new ActiveGuiderProvider(profiles, phd2, _guider);
        var af = new AutoFocusService(equip, relay, guiders, profiles,
            NullLogger<AutoFocusService>.Instance);
        _triggers = new LiveStackTriggersService(_stack, profiles, equip, af, slew, solver,
            guiders, NullLogger<LiveStackTriggersService>.Instance);

        _triggers.Settings.DitherEnabled = true;
        _triggers.Settings.DitherEveryNFrames = 3;
        _triggers.Settings.RefocusEnabled = false;
        _triggers.Settings.RecenterEnabled = false;
    }

    /// <summary>
    /// Scenarios start at frame 2, never frame 1.
    ///
    /// Frame 1 is the bootstrap frame, and all it does is ResetTriggerState()
    /// plus a fire-and-forget reference solve. The reset is redundant here: a
    /// freshly constructed service is already in exactly that state, field for
    /// field. The solve is actively harmful: it runs on a Task.Run nobody
    /// awaits, over this fixture's null frame, and its catch writes _lastError
    /// at an unpredictable moment. Every LastError assertion below was racing
    /// it, and roughly one run in five lost, reading "Reference solve crashed"
    /// instead of the message the frame path had just written.
    /// </summary>
    private async Task FrameAsync(int n) =>
        await _stack.RaiseFrameIntegratedAsync(new LiveStackFrameInfo(
            n, null!, MedianHfr: 1.5, StarCount: 80, At: DateTime.UtcNow,
            FrameSnr: 40, CumulativeSnr: 40));

    /// <summary>
    /// THE REGRESSION. No guider is connected in this fixture, so every frame
    /// takes the skip path. The gate must not move: whatever frame guiding
    /// comes back on, that frame dithers.
    /// </summary>
    [Test]
    public async Task ASkippedDither_DoesNotConsumeTheSlot() {
        for (int n = 2; n <= 12; n++) await FrameAsync(n);

        var st = _triggers.CurrentStatus;
        Assert.That(st.LastError, Does.StartWith("Dither skipped"),
            "the operator has to be told why nothing dithered");
        Assert.That(st.LastDitherFrame, Is.Null,
            "nothing dithered, so the gate must still be where it started; "
            + "advancing it turns a momentary guider hiccup into N frames without a dither");
    }

    /// <summary>The notice names the actual reason rather than one catch-all
    /// string: "not connected" and "connected but not guiding" send the
    /// operator to different places.</summary>
    [Test]
    public async Task TheSkipNotice_SaysWhichProblemItIs() {
        await FrameAsync(4);

        Assert.That(_triggers.CurrentStatus.LastError,
            Is.EqualTo("Dither skipped: guider not connected"));
    }

    /// <summary>
    /// With the gate no longer advancing on a skip, the dither branch is taken
    /// on every frame. It used to return unconditionally, which would have left
    /// an unguided session unable to ever reach the recenter check below it.
    /// </summary>
    [Test]
    public async Task ASkippedDither_DoesNotBlockTheRestOfTheFrame() {
        _triggers.Settings.RecenterEnabled = true;
        _triggers.Settings.RecenterEveryNFrames = 2;

        await FrameAsync(4);

        // No reference solve is possible here, so a recenter that is REACHED
        // reports exactly that. Reaching it is the whole point: the old code
        // returned after the dither branch and this stayed null.
        Assert.That(_triggers.CurrentStatus.LastError,
            Is.EqualTo("Recenter skipped, reference RA/Dec not solved"),
            "the frame must carry on past a skipped dither");
    }

    [Test]
    public async Task WithDitherDisabled_NothingIsReported() {
        _triggers.Settings.DitherEnabled = false;

        for (int n = 2; n <= 8; n++) await FrameAsync(n);

        Assert.That(_triggers.CurrentStatus.LastError, Is.Null);
        Assert.That(_triggers.CurrentStatus.LastDitherFrame, Is.Null);
    }
}
