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
using NINA.Polaris.Services.Sequencer;
using NINA.Polaris.Services.Sequencer.Containers;
using NINA.Polaris.Services.Sequencer.Instructions;
using NINA.Polaris.Services.Sequencer.Triggers;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class SequenceExamplesTests {
    private static IEnumerable<string> Ids() => SequenceExamples.All.Select(e => e.Id);

    [TestCaseSource(nameof(Ids))]
    public void Example_BuildsAndValidatesClean(string id) {
        var doc = SequenceExamples.Build(id);
        Assert.That(doc, Is.Not.Null);
        Assert.That(doc!.Root.Validate(), Is.Empty);
        Assert.That(doc.Root.Description, Is.Not.Empty, "the root is selected on load and should explain the example");
    }

    [TestCaseSource(nameof(Ids))]
    public void Example_RoundTripsThroughTheEditorJson(string id) {
        var doc = SequenceExamples.Build(id)!;
        var back = SequenceJson.Deserialize(SequenceJson.Serialize(doc));
        var before = (SequentialContainer)doc.Root;
        var after = (SequentialContainer)back.Root;
        Assert.That(after.Items.Select(i => i.GetType()), Is.EqualTo(before.Items.Select(i => i.GetType())));
        Assert.That(back.Root.Validate(), Is.Empty);
    }

    // The flip and the altitude limit time themselves off their own
    // coordinates; a copy that drifts from the container's would flip at the
    // wrong moment, or end the target early.
    [TestCaseSource(nameof(Ids))]
    public void Example_TargetTriggersUseTheTargetCoordinates(string id) {
        var root = (SequentialContainer)SequenceExamples.Build(id)!.Root;
        foreach (var dso in root.Items.OfType<DeepSkyObjectContainer>()) {
            var flip = dso.Triggers.OfType<MeridianFlipTrigger>().Single();
            Assert.That((flip.RaHours, flip.DecDeg), Is.EqualTo((dso.RaHours, dso.DecDeg)));
            Assert.That(dso.IsLoop, Is.True);
            Assert.That(dso.Conditions, Is.Not.Empty, "a looping target needs something to end it");
        }
    }

    [Test]
    public void UnknownExample_IsNull() => Assert.That(SequenceExamples.Build("nope"), Is.Null);

    [Test]
    public void TakeExposure_ZeroSecondsIsValidOnlyForABias() {
        Assert.That(new TakeExposureInstruction { ExposureSeconds = 0, ImageType = "BIAS" }.Validate(), Is.Empty);
        Assert.That(new TakeExposureInstruction { ExposureSeconds = 0, ImageType = "DARK" }.Validate(), Is.Not.Empty);
        Assert.That(new TakeExposureInstruction { ExposureSeconds = -1, ImageType = "BIAS" }.Validate(), Is.Not.Empty);
    }

    [Test]
    public void PlanForEditing_AppendsTheEndOfSessionBlock() {
        var plan = new ImagingPlan {
            Name = "Night", AutoGuiding = true, EndWarmCoolerOff = true, EndGoHome = true,
            Targets = { new PlanTarget { Name = "M42", RaHours = 5.59, DecDeg = -5.39,
                Frames = { new PlanFrame { ExposureSeconds = 60, Count = 10 } } } }
        };
        var root = (SequentialContainer)new PlanCompilerService().CompileForEditing(plan).Root;
        var end = root.Items.Last() as SequentialContainer;
        Assert.That(end, Is.Not.Null);
        Assert.That(end!.Items.Select(i => i.GetType()), Is.EqualTo(new[] {
            typeof(StopGuidingInstruction), typeof(WarmCameraInstruction), typeof(ParkMountInstruction) }));
        Assert.That(root.Items.OfType<DeepSkyObjectContainer>().Count(), Is.EqualTo(1));
    }

    [Test]
    public void PlanForEditing_WithNoEndActions_AddsNoBlock() {
        var plan = new ImagingPlan {
            Name = "Night", AutoGuiding = false,
            Targets = { new PlanTarget { Name = "M42", RaHours = 5.59, DecDeg = -5.39,
                Frames = { new PlanFrame { ExposureSeconds = 60, Count = 10 } } } }
        };
        var root = (SequentialContainer)new PlanCompilerService().CompileForEditing(plan).Root;
        Assert.That(root.Items.Last(), Is.InstanceOf<DeepSkyObjectContainer>());
    }
}
