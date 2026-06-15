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

using NINA.Polaris.Services.Plan;
using NINA.Polaris.Services.Sequencer.Containers;
using NINA.Polaris.Services.Sequencer.Instructions;
using NINA.Polaris.Services.Sequencer.Triggers;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class PlanCompilerTests {
    private PlanCompilerService _c = null!;

    [SetUp]
    public void SetUp() => _c = new PlanCompilerService();

    private static ImagingPlan TwoTargetPlan() => new() {
        Name = "Night",
        Targets = {
            new PlanTarget {
                Name = "M31", RaHours = 0.71, DecDeg = 41.27,
                Frames = { new PlanFrame { ExposureSeconds = 120, Count = 20 } }
            },
            new PlanTarget {
                Name = "M42", RaHours = 5.59, DecDeg = -5.39,
                Frames = { new PlanFrame { ExposureSeconds = 60, Count = 30 } }
            }
        }
    };

    [Test]
    public void Compile_OneContainerPerEnabledTarget() {
        var doc = _c.Compile(TwoTargetPlan());
        var root = (SequentialContainer)doc.Root;
        var dsos = root.Items.OfType<DeepSkyObjectContainer>().ToList();
        Assert.That(dsos.Count, Is.EqualTo(2));
        Assert.That(dsos[0].Target, Is.EqualTo("M31"));
        Assert.That(dsos[0].CenterOnStart, Is.True);
        Assert.That(dsos[0].Items.OfType<TakeExposureInstruction>().Single().Count, Is.EqualTo(20));
    }

    [Test]
    public void Compile_DisabledTargetsAreSkipped() {
        var plan = TwoTargetPlan();
        plan.Targets[1].Enabled = false;
        var root = (SequentialContainer)_c.Compile(plan).Root;
        Assert.That(root.Items.OfType<DeepSkyObjectContainer>().Count(), Is.EqualTo(1));
    }

    [Test]
    public void Compile_PrologueRespectsOptions() {
        var plan = TwoTargetPlan();
        plan.StartMode = PlanStartMode.AtTime; plan.StartAtUtc = "22:30";
        plan.AutoCooling = true; plan.CoolTargetC = -15;
        plan.AutoGuiding = true; plan.AutoFocusOnStart = true;

        var root = (SequentialContainer)_c.Compile(plan).Root;
        // Prologue items precede the first DSO container, in order.
        var firstDso = root.Items.FindIndex(i => i is DeepSkyObjectContainer);
        var prologue = root.Items.Take(firstDso).ToList();

        Assert.That(prologue.OfType<WaitUntilTimeInstruction>().Single().TimeOfDayUtc, Is.EqualTo("22:30"));
        Assert.That(prologue.OfType<CoolCameraInstruction>().Single().TargetTempC, Is.EqualTo(-15));
        Assert.That(prologue.OfType<StartGuidingInstruction>().Any(), Is.True);
        Assert.That(prologue.OfType<AutoFocusInstruction>().Any(), Is.True);
    }

    [Test]
    public void Compile_NoPrologueWhenAllOptionsOff() {
        var plan = TwoTargetPlan();
        plan.StartMode = PlanStartMode.Now;
        plan.AutoCooling = false; plan.AutoGuiding = false; plan.AutoFocusOnStart = false;
        var root = (SequentialContainer)_c.Compile(plan).Root;
        Assert.That(root.Items.First(), Is.InstanceOf<DeepSkyObjectContainer>());
    }

    [Test]
    public void Compile_MeridianFlipTriggerPerTargetWhenEnabled() {
        var plan = TwoTargetPlan();
        plan.AutoMeridianFlip = true;
        var dsos = ((SequentialContainer)_c.Compile(plan).Root).Items.OfType<DeepSkyObjectContainer>().ToList();
        Assert.That(dsos[0].Triggers.OfType<MeridianFlipTrigger>().Single().RaHours, Is.EqualTo(0.71).Within(1e-9));
        Assert.That(dsos[1].Triggers.OfType<MeridianFlipTrigger>().Single().RaHours, Is.EqualTo(5.59).Within(1e-9));
    }

    [Test]
    public void Compile_NoMeridianFlipTriggerWhenDisabled() {
        var plan = TwoTargetPlan();
        plan.AutoMeridianFlip = false;
        var dsos = ((SequentialContainer)_c.Compile(plan).Root).Items.OfType<DeepSkyObjectContainer>().ToList();
        Assert.That(dsos.SelectMany(d => d.Triggers).Any(), Is.False);
    }

    [Test]
    public void Compile_FirstDelayInsertsWaitBeforeFrames() {
        var plan = TwoTargetPlan();
        plan.Targets[0].FirstDelaySec = 45;
        var dso = ((SequentialContainer)_c.Compile(plan).Root).Items.OfType<DeepSkyObjectContainer>().First();
        Assert.That(dso.Items.First(), Is.InstanceOf<WaitForTimeInstruction>());
        Assert.That(((WaitForTimeInstruction)dso.Items.First()).Seconds, Is.EqualTo(45));
    }

    [Test]
    public void CompileEndActions_BuildsSelectedActionsOnly() {
        var plan = TwoTargetPlan();
        plan.AutoGuiding = true;       // → StopGuiding
        plan.EndWarmCoolerOff = true;  // → WarmCamera
        plan.EndGoHome = true;         // → ParkMount
        plan.EndEafZero = true;        // → MoveFocuser 0

        var end = _c.CompileEndActions(plan);
        Assert.That(end, Is.Not.Null);
        var items = ((SequentialContainer)end!.Root).Items;
        Assert.That(items.OfType<StopGuidingInstruction>().Any(), Is.True);
        Assert.That(items.OfType<WarmCameraInstruction>().Any(), Is.True);
        Assert.That(items.OfType<ParkMountInstruction>().Any(), Is.True);
        Assert.That(items.OfType<MoveFocuserInstruction>().Single().Position, Is.EqualTo(0));
    }

    [Test]
    public void CompileEndActions_NullWhenNothingToDo() {
        var plan = TwoTargetPlan();
        plan.AutoGuiding = false;
        plan.EndWarmCoolerOff = false; plan.EndGoHome = false; plan.EndEafZero = false;
        Assert.That(_c.CompileEndActions(plan), Is.Null);
    }

    [Test]
    public void EstimateSeconds_SumsFramesPlusOverhead() {
        // One target: 2 frames × (10s + 5s overhead) = 30s + slew(30) + solve(20) = 80s.
        var plan = new ImagingPlan {
            Targets = { new PlanTarget {
                Frames = { new PlanFrame { ExposureSeconds = 10, Count = 2 } }
            } }
        };
        Assert.That(_c.EstimateSeconds(plan), Is.EqualTo(80).Within(0.001));
    }

    [Test]
    public void Compile_HostShutdownNeverAppearsInDocuments() {
        var plan = TwoTargetPlan();
        plan.EndShutdownHost = true;
        plan.AutoGuiding = true;
        var main = ((SequentialContainer)_c.Compile(plan).Root).Items;
        // Shutdown is the runner's job; nothing in either document represents it.
        Assert.That(main.OfType<DeepSkyObjectContainer>().Count(), Is.EqualTo(2));
        var end = _c.CompileEndActions(plan);
        Assert.That(end, Is.Not.Null);
        // No mount park unless EndGoHome; here only StopGuiding should be present.
        Assert.That(((SequentialContainer)end!.Root).Items.OfType<ParkMountInstruction>().Any(), Is.False);
    }
}
