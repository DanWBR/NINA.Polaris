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
using NUnit.Framework;
using NINA.Polaris.Services;
using NINA.INDI.Client;

namespace NINA.Polaris.Test;

/// <summary>
/// AUTORUN-PERSIST: the autorun schedule (items + dither + end-actions) is
/// persisted to the profile so it survives a host restart. Here a restart is
/// simulated by building a second ProfileService + SequenceEngine over the same
/// on-disk profile directory.
/// </summary>
[TestFixture]
public class AutorunPersistenceTests {

    private string _dir = "";

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "polaris-autorun-test-" + Guid.NewGuid().ToString("N"));
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
        var aux = new AuxCaptureService(equip, imageWriter, profile, guiders, autoFocus, meridianFlip,
            NullLogger<AuxCaptureService>.Instance);
        return new SequenceEngine(equip, relay, liveStack, phd2, guiders, meridianFlip, imageWriter,
            graXpert, flatWizard, profile, new CaptureProgressService(), aux,
            new CameraReadyGate(() => equip.Camera, NullLogger<CameraReadyGate>.Instance),
            NullLogger<SequenceEngine>.Instance);
    }

    [Test]
    public void Schedule_SurvivesHostRestart() {
        var p1 = NewProfile();
        var engine1 = MakeEngine(p1);
        engine1.LoadSequence(new List<SequenceItem> {
            new() { Name = "M31", Exposure = 120, Gain = 100, Count = 20, Filter = "Ha", ImageType = "LIGHT" },
            new() { Name = "M31", Exposure = 120, Gain = 100, Count = 20, Filter = "OIII" },
        });
        engine1.Dither = new DitherSettings { Enabled = true, EveryNFrames = 2 };
        engine1.SaveSchedule();

        // "Restart": a fresh ProfileService reads active.json from disk, and a
        // fresh engine restores its schedule from it.
        var engine2 = MakeEngine(NewProfile());
        Assert.That(engine2.Items.Count, Is.EqualTo(2), "the schedule must survive a restart");
        Assert.That(engine2.Items[0].Name, Is.EqualTo("M31"));
        Assert.That(engine2.Items[0].Filter, Is.EqualTo("Ha"), "per-item filter must persist");
        Assert.That(engine2.Items[1].Filter, Is.EqualTo("OIII"));
        Assert.That(engine2.Dither.Enabled, Is.True, "dither settings must persist");
        Assert.That(engine2.Dither.EveryNFrames, Is.EqualTo(2));
    }

    [Test]
    public void ClearingTheSchedule_Persists() {
        var p1 = NewProfile();
        var engine1 = MakeEngine(p1);
        engine1.LoadSequence(new List<SequenceItem> {
            new() { Name = "M42", Exposure = 60, Count = 10 }
        });
        // "Clear" posts an empty list -> LoadSequence([]).
        engine1.LoadSequence(new List<SequenceItem>());

        var engine2 = MakeEngine(NewProfile());
        Assert.That(engine2.Items, Is.Empty, "a cleared schedule must stay cleared after a restart");
    }

    [Test]
    public void EndActions_Persist() {
        var p1 = NewProfile();
        var engine1 = MakeEngine(p1);
        engine1.LoadSequence(new List<SequenceItem> { new() { Name = "T", Count = 1 } });
        engine1.EndActions = new SequenceEndActions { ParkMount = true, WarmCamera = true };
        engine1.SaveSchedule();

        var engine2 = MakeEngine(NewProfile());
        Assert.That(engine2.EndActions.ParkMount, Is.True);
        Assert.That(engine2.EndActions.WarmCamera, Is.True);
    }
}
