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

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;
using NINA.INDI.Client;

namespace NINA.Polaris.Test;

/// <summary>
/// Regression coverage for the AUTORUN item-advance logic. Field report
/// 2026-07-11: a two-item schedule (Bias x10, Dark x10) "ended" right after
/// the first item. Root cause: the per-item start-frame check compared the
/// loop index against the LIVE <c>CurrentItemIndex</c> (rewritten to the
/// loop index at the top of every iteration, so always equal) instead of the
/// resume snapshot, which made every item after the first inherit the
/// previous item's finished frame counter — its frame for-loop started at
/// Count and captured nothing. Runs the REAL RunAsync loop end-to-end
/// against the simulator camera (zero-second calibration frames, no INDI).
/// </summary>
[TestFixture]
public class SequenceEngineResumeTests {

    private (SequenceEngine engine, EquipmentManager equip) MakeEngine() {
        var indi = new IndiClient("localhost", 7624);
        var equip = new EquipmentManager(indi, NullLogger<EquipmentManager>.Instance,
            new NINA.Polaris.Services.Alpaca.AlpacaDiscoveryCache(),
            new NINA.Polaris.Services.Simulator.Gear.SimGearService());
        var relay = new ImageRelayService(NullLogger<ImageRelayService>.Instance);
        var liveStack = new LiveStackingService(relay, NullLogger<LiveStackingService>.Instance);
        var phd2 = new PHD2Client(NullLogger<PHD2Client>.Instance);
        var emptyConfig = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var plateSolve = new PlateSolveService(emptyConfig, NullLogger<PlateSolveService>.Instance);
        var profile = new ProfileService(emptyConfig, NullLogger<ProfileService>.Instance);
        // No output dir → ImageWriterService.SaveImage no-ops, keeping the
        // test off the filesystem entirely.
        profile.Active.ImageOutputDir = "";
        var stream = new CameraStreamService(equip, relay, NullLogger<CameraStreamService>.Instance,
            new CaptureProgressService());
        var slewCenter = new SlewCenterService(equip, plateSolve, profile, stream, NullLogger<SlewCenterService>.Instance);
        var native = new NativeGuider(equip, profile, NullLogger<NativeGuider>.Instance);
        var guiders = new ActiveGuiderProvider(profile, phd2, native);
        var autoFocus = new AutoFocusService(equip, relay, guiders, profile, NullLogger<AutoFocusService>.Instance);
        var meridianFlip = new MeridianFlipService(equip, guiders, slewCenter, autoFocus, profile,
            new CaptureProgressService(), NullLogger<MeridianFlipService>.Instance);
        var imageWriter = new ImageWriterService(equip, profile, NullLogger<ImageWriterService>.Instance);
        var graXpert = new NINA.Polaris.Services.External.GraXpertService(emptyConfig, profile,
            NullLogger<NINA.Polaris.Services.External.GraXpertService>.Instance);
        var flatWizard = new FlatWizardService(equip, imageWriter, profile,
            NullLogger<FlatWizardService>.Instance, emptyConfig);
        var aux = new AuxCaptureService(equip, imageWriter, profile, guiders, autoFocus, meridianFlip,
            NullLogger<AuxCaptureService>.Instance);
        var engine = new SequenceEngine(equip, relay, liveStack, phd2, guiders, meridianFlip, imageWriter,
            graXpert, flatWizard, profile, new CaptureProgressService(), aux,
            new NINA.Polaris.Services.CameraReadyGate(() => equip.Camera, Microsoft.Extensions.Logging.Abstractions.NullLogger<NINA.Polaris.Services.CameraReadyGate>.Instance),
            NullLogger<SequenceEngine>.Instance);
        return (engine, equip);
    }

    private static async Task WaitForIdleAsync(SequenceEngine engine, int timeoutMs = 30000) {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (engine.State != SequenceState.Idle && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.That(engine.State, Is.EqualTo(SequenceState.Idle),
            "sequence did not finish within the timeout");
    }

    [Test]
    public async Task TwoItemSequence_CapturesEveryFrameOfBothItems() {
        var (engine, equip) = MakeEngine();
        var cam = equip.SelectCamera("sim", "Simulator");
        await cam.ConnectAsync();

        engine.LoadSequence(new List<SequenceItem> {
            // Calibration types: no slew, no meridian flip, no dither — the
            // loop reduces to pure capture, which is exactly what the
            // item-advance regression needs.
            new() { Name = "Bias set", ImageType = "BIAS", Exposure = 0, Count = 2 },
            new() { Name = "Dark set", ImageType = "DARK", Exposure = 0, Count = 2 }
        });
        engine.Start();
        await WaitForIdleAsync(engine);

        Assert.That(engine.LastError, Is.Null.Or.Empty);
        // The bug made item 2 start its frame loop at item 1's finished
        // counter (2 >= Count) and skip every frame: total came out 2.
        Assert.That(engine.TotalFramesCompleted, Is.EqualTo(4),
            "both items must contribute all their frames");
        Assert.That(engine.CurrentItemIndex, Is.EqualTo(1),
            "the run must have advanced to (and finished) the second item");
    }

    [Test]
    public async Task StopThenStart_ResumesFromRetainedProgress_NotFromZero() {
        var (engine, equip) = MakeEngine();
        var cam = equip.SelectCamera("sim", "Simulator");
        await cam.ConnectAsync();

        engine.LoadSequence(new List<SequenceItem> {
            new() { Name = "Bias set", ImageType = "BIAS", Exposure = 0, Count = 3 }
        });
        engine.Start();
        await WaitForIdleAsync(engine);
        Assert.That(engine.TotalFramesCompleted, Is.EqualTo(3));

        // Simulate the "continue" flow: a second Start() with retained
        // progress must resume at the retained frame (past the end here,
        // so it captures nothing extra) instead of re-shooting the item.
        engine.Start();
        await WaitForIdleAsync(engine);
        Assert.That(engine.TotalFramesCompleted, Is.EqualTo(3),
            "a completed item must not re-capture on resume");

        // And after ResetProgress a fresh run shoots everything again.
        engine.ResetProgress();
        engine.Start();
        await WaitForIdleAsync(engine);
        Assert.That(engine.TotalFramesCompleted, Is.EqualTo(3),
            "restart after ResetProgress must capture the full item");
    }
}
