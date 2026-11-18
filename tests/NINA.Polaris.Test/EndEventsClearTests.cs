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

using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;
using NINA.INDI.Client;

namespace NINA.Polaris.Test;

/// <summary>
/// The LIVE and AUTORUN end events belong to one session: they turn off once
/// they run, and on Reset (and Clear), so the next session does not inherit a
/// park or a power-off. The run options that live beside them (per-item
/// auto-focus, per-frame GraXpert, LIVE Save stack) stay.
/// </summary>
[TestFixture]
public class EndEventsClearTests {

    private string _dir = "";

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "polaris-endevents-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ProfileService NewProfile() {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Profiles:Directory"] = _dir })
            .Build();
        return new ProfileService(cfg, NullLogger<ProfileService>.Instance);
    }

    // Build a SequenceEngine over a specific ProfileService (so two engines can
    // share one on-disk profile, the "restart" the test exercises).
    private SequenceEngine MakeEngine(ProfileService profile) {
        var cfg = new ConfigurationBuilder().Build();
        var indi = new IndiClient("localhost", 7624);
        var equip = new EquipmentManager(indi, NullLogger<EquipmentManager>.Instance,
            new NINA.Polaris.Services.Alpaca.AlpacaDiscoveryCache(),
            new NINA.Polaris.Services.Simulator.Gear.SimGearService());
        var relay = new ImageRelayService(NullLogger<ImageRelayService>.Instance);
        var liveStack = new LiveStackingService(relay, NullLogger<LiveStackingService>.Instance);
        var phd2 = new PHD2Client(NullLogger<PHD2Client>.Instance);
        var plateSolve = new PlateSolveService(cfg, NullLogger<PlateSolveService>.Instance);
        var stream = new CameraStreamService(equip, relay, NullLogger<CameraStreamService>.Instance,
            new CaptureProgressService());
        var slewCenter = new SlewCenterService(equip, plateSolve, profile, stream, NullLogger<SlewCenterService>.Instance);
        var native = new NativeGuider(equip, profile, NullLogger<NativeGuider>.Instance);
        var guiders = new ActiveGuiderProvider(profile, phd2, native);
        var autoFocus = new AutoFocusService(equip, relay, guiders, profile, NullLogger<AutoFocusService>.Instance);
        var meridianFlip = new MeridianFlipService(equip, guiders, slewCenter, autoFocus, profile,
            new CaptureProgressService(), NullLogger<MeridianFlipService>.Instance);
        var imageWriter = new ImageWriterService(equip, profile, NullLogger<ImageWriterService>.Instance);
        var graXpert = new NINA.Polaris.Services.External.GraXpertService(cfg, profile,
            NullLogger<NINA.Polaris.Services.External.GraXpertService>.Instance);
        var flatWizard = new FlatWizardService(equip, imageWriter, profile,
            NullLogger<FlatWizardService>.Instance, cfg);
        var barrier = new DitherBarrier(guiders, profile, NullLogger<DitherBarrier>.Instance);
        var multiImager = new MultiImagerCaptureService(equip, imageWriter, profile, guiders, autoFocus, meridianFlip, barrier, liveStack, relay, NullLogger<MultiImagerCaptureService>.Instance);
        var aux = new AuxCaptureService(equip, imageWriter, profile, guiders, autoFocus, meridianFlip,
            barrier, multiImager, NullLogger<AuxCaptureService>.Instance);
        return new SequenceEngine(equip, relay, liveStack, phd2, guiders, meridianFlip, imageWriter,
            graXpert, flatWizard, profile, new CaptureProgressService(), aux,
            new CameraReadyGate(() => equip.Camera, NullLogger<CameraReadyGate>.Instance),
            barrier, NullLogger<SequenceEngine>.Instance);
    }

    private static SequenceEndActions AllOn() => new() {
        ParkMount = true, HomeMount = true, StopTracking = true, WarmCamera = true,
        DisconnectGuider = true, ShutdownHost = true, RunOnStop = true,
        AutoFocusEachItem = true, AutoGraXpert = true,
    };

    private static void AssertEndEventsOff(SequenceEndActions ea) {
        Assert.That(ea.ParkMount || ea.HomeMount || ea.StopTracking || ea.WarmCamera
                    || ea.DisconnectGuider || ea.ShutdownHost || ea.RunOnStop, Is.False);
        Assert.That(ea.AutoFocusEachItem, Is.True, "per-item auto-focus is a run option");
        Assert.That(ea.AutoGraXpert, Is.True, "per-frame GraXpert is a run option");
    }

    [Test]
    public void SequenceEndActions_WithoutEndEvents_KeepsTheRunOptions() {
        var ea = AllOn();
        Assert.That(ea.HasEndEvents, Is.True);
        var off = ea.WithoutEndEvents();
        AssertEndEventsOff(off);
        Assert.That(off.HasEndEvents, Is.False);
    }

    [Test]
    public void Autorun_ClearEndEvents_IsSavedOnTheRig() {
        var profile = NewProfile();
        var engine = MakeEngine(profile);
        engine.EndActions = AllOn();
        engine.SaveSchedule();

        engine.ClearEndEvents();
        AssertEndEventsOff(engine.EndActions);

        // A restart reads the cleared set back.
        var again = MakeEngine(NewProfile());
        AssertEndEventsOff(again.EndActions);
    }

    [Test]
    public void Autorun_RunningTheEndEvents_TurnsThemOff() {
        var profile = NewProfile();
        var engine = MakeEngine(profile);
        // No power-off here: a stopped run never powers off, and nothing is
        // connected, so every action is a logged no-op.
        engine.EndActions = AllOn();
        engine.SaveSchedule();
        var run = typeof(SequenceEngine).GetMethod("RunEndActionsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((Task)run.Invoke(engine, new object[] { true })!).GetAwaiter().GetResult();
        AssertEndEventsOff(engine.EndActions);
        AssertEndEventsOff(MakeEngine(NewProfile()).EndActions);
    }

    [Test]
    public void Live_WithoutEndEvents_KeepsSaveStack() {
        var a = new LiveStackEndActions {
            SaveStack = true, StopCapture = true, StopGuiding = true, ParkMount = true,
            HomeMount = true, StopTracking = true, WarmCamera = true, ShutdownHost = true,
        };
        var off = a.WithoutEndEvents();
        Assert.That(off.SaveStack, Is.True);
        Assert.That(off.ToTeardown().Any, Is.False);
        Assert.That(new LiveStackEndActions { SaveStack = false }.WithoutEndEvents().SaveStack, Is.False);
    }

    [Test]
    public void Live_ClearEndEvents_IsSavedOnTheRig() {
        var profile = NewProfile();
        var rig = profile.ActiveEquipmentProfile;
        profile.UpdateEquipmentProfile(rig.Id, r => r.LiveStackEndActions = new LiveStackEndActions {
            SaveStack = true, ParkMount = true, WarmCamera = true, ShutdownHost = true,
        });

        LiveStackEndService.ClearEndEvents(profile);

        var back = NewProfile().ActiveEquipmentProfile.LiveStackEndActions;
        Assert.That(back.SaveStack, Is.True);
        Assert.That(back.ToTeardown().Any, Is.False);
    }
}
