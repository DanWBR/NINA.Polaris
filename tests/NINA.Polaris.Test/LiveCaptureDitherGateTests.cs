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

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.INDI.Client;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Does the LIVE capture loop actually wait for a dither before the next sub?
///
/// Asked from the field after a session where the header read "dithering"
/// while the LIVE panel looked busy. The answer from the code is yes, twice
/// over: the dither runs inside the frame hook the loop awaits, and the loop
/// will not start a sub while the guider reports settling. What had broken was
/// the second half of that: with the star lost mid-settle the guider reported
/// settling with nothing to ever lower it, so the loop was not out of step, it
/// was parked, and the parking had no end.
///
/// These pin the gate against the native guider's real settle state, driven
/// the way the guide loop drives it.
/// </summary>
[TestFixture]
public class LiveCaptureDitherGateTests {

    private LiveCaptureService _loop = null!;
    private NativeGuider _guider = null!;

    [SetUp]
    public void SetUp() {
        var cfg = new ConfigurationBuilder().Build();
        var indi = new IndiClient("localhost", 7624);
        var equip = new EquipmentManager(indi, NullLogger<EquipmentManager>.Instance,
            new NINA.Polaris.Services.Alpaca.AlpacaDiscoveryCache(),
            new NINA.Polaris.Services.Simulator.Gear.SimGearService());
        var relay = new ImageRelayService(NullLogger<ImageRelayService>.Instance);
        var liveStack = new LiveStackingService(relay, NullLogger<LiveStackingService>.Instance);
        var profile = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        profile.ActiveEquipmentProfile.GuiderDriver = "native";
        var phd2 = new PHD2Client(NullLogger<PHD2Client>.Instance);
        var plateSolve = new PlateSolveService(cfg, NullLogger<PlateSolveService>.Instance);
        var progress = new CaptureProgressService();
        var stream = new CameraStreamService(equip, relay, NullLogger<CameraStreamService>.Instance, progress);
        var slewCenter = new SlewCenterService(equip, plateSolve, profile, stream,
            NullLogger<SlewCenterService>.Instance);
        _guider = new NativeGuider(equip, profile, NullLogger<NativeGuider>.Instance);
        var guiders = new ActiveGuiderProvider(profile, phd2, _guider);
        var autoFocus = new AutoFocusService(equip, relay, guiders, profile,
            NullLogger<AutoFocusService>.Instance);
        var meridian = new MeridianFlipService(equip, guiders, slewCenter, autoFocus, profile,
            progress, NullLogger<MeridianFlipService>.Instance);
        var barrier = new DitherBarrier(guiders, profile, NullLogger<DitherBarrier>.Instance);
        var writer = new ImageWriterService(equip, profile, NullLogger<ImageWriterService>.Instance);
        var multiImager = new MultiImagerCaptureService(equip, writer, profile, guiders, autoFocus,
            meridian, barrier, liveStack, relay, NullLogger<MultiImagerCaptureService>.Instance);
        var aux = new AuxCaptureService(equip, writer, profile, guiders, autoFocus, meridian, barrier,
            multiImager, NullLogger<AuxCaptureService>.Instance);
        var ready = new CameraReadyGate(() => equip.Camera, NullLogger<CameraReadyGate>.Instance);
        _loop = new LiveCaptureService(equip, liveStack, relay, progress, guiders, autoFocus,
            aux, ready, meridian, barrier, NullLogger<LiveCaptureService>.Instance);
    }

    [Test]
    public void IdleGuider_DoesNotHoldTheLoop() {
        Assert.That(_loop.ShouldPause(), Is.False);
    }

    /// <summary>The gate itself: a settle in progress, dither or not, holds the
    /// next sub.</summary>
    [TestCase(true)]
    [TestCase(false)]
    public void ASettleInProgress_HoldsTheNextSub(bool dithering) {
        _guider.InstallSettleForTest(settlePixels: 1.5, settleSec: 10, timeoutSec: 40, dithering);
        Assert.That(_loop.ShouldPause(), Is.True);
    }

    /// <summary>The settle completing releases the loop.</summary>
    [Test]
    public async Task ASettleThatCompletes_ReleasesTheLoop() {
        _guider.InstallSettleForTest(settlePixels: 1.5, settleSec: 0.05, timeoutSec: 40, dithering: true);
        _guider.AdvanceSettle(0.3);
        Assert.That(_loop.ShouldPause(), Is.True, "still inside the settle time");
        await Task.Delay(80);
        _guider.AdvanceSettle(0.3);
        Assert.That(_loop.ShouldPause(), Is.False);
    }

    /// <summary>THE REGRESSION. The star is lost right after the dither and
    /// never found again inside the timeout. The loop must be released when the
    /// settle times out, not when the star happens to come back, which may be
    /// never.</summary>
    [Test]
    public async Task ASettleWhoseStarIsLost_ReleasesTheLoopAtTheTimeout() {
        _guider.InstallSettleForTest(settlePixels: 1.5, settleSec: 10, timeoutSec: 0.1, dithering: true);
        _guider.AdvanceSettle(null);
        Assert.That(_loop.ShouldPause(), Is.True, "inside the timeout, the loop rightly waits");
        await Task.Delay(150);
        _guider.AdvanceSettle(null);
        Assert.That(_loop.ShouldPause(), Is.False,
            "past the timeout the guider must have let go, or the loop parks for the whole loss");
    }
}
