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

using NINA.Polaris.Services.Sequencer;
using NINA.Polaris.Services.Sequencer.Conditions;
using NINA.Polaris.Services.Sequencer.Containers;
using NINA.Polaris.Services.Sequencer.Instructions;
using NINA.Polaris.Services.Sequencer.Triggers;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class SequenceAdvisorTests {
    private const double Ra = 5.5881, Dec = -5.3911;

    // A complete, sensible night: the baseline every rule test breaks in one place.
    private static SequentialContainer Night(Action<SequentialContainer, DeepSkyObjectContainer>? tweak = null) {
        var root = new SequentialContainer { Name = "Night" };
        root.Items.Add(new CoolCameraInstruction { Name = "Cool" });
        root.Items.Add(new StartGuidingInstruction { Name = "Guide" });
        var dso = new DeepSkyObjectContainer { Name = "M42", RaHours = Ra, DecDeg = Dec, IsLoop = true };
        dso.Conditions.Add(new LoopUntilAltitudeCondition { Name = "Low", RaHours = Ra, DecDeg = Dec });
        dso.Triggers.Add(new MeridianFlipTrigger { Name = "Flip", RaHours = Ra, DecDeg = Dec });
        dso.Triggers.Add(new DitherAfterNExposuresTrigger { Name = "Dither" });
        dso.Items.Add(new TakeExposureInstruction { Name = "Light", ExposureSeconds = 60 });
        root.Items.Add(dso);
        root.Items.Add(new WarmCameraInstruction { Name = "Warm" });
        root.Items.Add(new ParkMountInstruction { Name = "Park" });
        tweak?.Invoke(root, dso);
        return root;
    }

    private static IReadOnlyList<string> Review(Action<SequentialContainer, DeepSkyObjectContainer>? tweak = null)
        => SequenceAdvisor.Review(Night(tweak));

    [Test]
    public void CompleteNight_HasNoWarnings() => Assert.That(Review(), Is.Empty);

    [TestCaseSource(typeof(SequenceExamples), nameof(SequenceExamples.All))]
    public void BuiltInExamples_HaveNoWarnings(SequenceExamples.Example ex)
        => Assert.That(SequenceAdvisor.Review(SequenceExamples.Build(ex.Id)!.Root), Is.Empty);

    [Test]
    public void FlipTrigger_WithOtherCoordinates_IsFlagged() {
        var w = Review((_, dso) => dso.Triggers.OfType<MeridianFlipTrigger>().Single().RaHours = 12.67);
        Assert.That(w, Has.Some.Contains("Meridian Flip trigger uses RA 12.67h"));
    }

    [Test]
    public void AltitudeCondition_WithOtherCoordinates_IsFlagged() {
        var w = Review((_, dso) => dso.Conditions.OfType<LoopUntilAltitudeCondition>().Single().DecDeg = 41);
        Assert.That(w, Has.Some.Contains("Loop Until Altitude condition watches"));
    }

    [Test]
    public void RaWrapAroundZero_CountsAsTheSameTarget() {
        var w = Review((_, dso) => {
            dso.RaHours = 23.9995;
            dso.Triggers.OfType<MeridianFlipTrigger>().Single().RaHours = 0.0005;
            dso.Triggers.OfType<MeridianFlipTrigger>().Single().DecDeg = dso.DecDeg;
            var c = dso.Conditions.OfType<LoopUntilAltitudeCondition>().Single();
            c.RaHours = 23.9995; c.DecDeg = dso.DecDeg;
        });
        Assert.That(w, Is.Empty);
    }

    [Test]
    public void OneFlipTriggerAtTheRoot_SharedByTwoTargets_IsFlagged() {
        var w = Review((root, dso) => {
            dso.Triggers.RemoveAll(t => t is MeridianFlipTrigger);
            root.Triggers.Add(new MeridianFlipTrigger { Name = "Flip", RaHours = Ra, DecDeg = Dec });
            var other = new DeepSkyObjectContainer { Name = "M104", RaHours = 12.67, DecDeg = -11.62 };
            other.Items.Add(new TakeExposureInstruction { Name = "Light", ExposureSeconds = 60 });
            root.Items.Insert(3, other);
        });
        Assert.That(w, Has.Some.StartsWith("[M104] The Meridian Flip trigger on \"Night\""));
        Assert.That(w, Has.None.StartsWith("[M42] The Meridian Flip trigger on"));
    }

    [Test]
    public void TargetWithLightsAndNoFlip_IsFlagged() {
        var w = Review((_, dso) => dso.Triggers.RemoveAll(t => t is MeridianFlipTrigger));
        Assert.That(w, Has.Some.Contains("No Meridian Flip trigger covers this target"));
    }

    [Test]
    public void TargetWithoutCoordinates_IsFlagged() {
        var w = Review((_, dso) => { dso.RaHours = 0; dso.DecDeg = 0; });
        Assert.That(w, Has.Some.Contains("coordinates are not set"));
    }

    [Test]
    public void RepeatWithoutCondition_AndConditionWithoutRepeat_AreFlagged() {
        Assert.That(Review((_, dso) => dso.Conditions.Clear()),
            Has.Some.Contains("repeats until you stop the run"));
        Assert.That(Review((_, dso) => dso.IsLoop = false),
            Has.Some.Contains("the condition is ignored"));
    }

    [Test]
    public void DitherWithoutStartGuiding_IsFlagged() {
        var w = Review((root, _) => root.Items.RemoveAll(i => i is StartGuidingInstruction));
        Assert.That(w, Has.Some.Contains("needs guiding running"));
    }

    [Test]
    public void StartGuidingInsideTheTarget_StillCountsBeforeItsExposures() {
        var w = Review((root, dso) => {
            root.Items.RemoveAll(i => i is StartGuidingInstruction);
            dso.Items.Insert(0, new StartGuidingInstruction { Name = "Guide" });
        });
        Assert.That(w, Is.Empty);
    }

    [Test]
    public void CoolWithoutWarm_AndNoPark_AreFlagged() {
        var w = Review((root, _) => root.Items.RemoveAll(i => i is WarmCameraInstruction or ParkMountInstruction));
        Assert.That(w, Has.Some.Contains("never warmed"));
        Assert.That(w, Has.Some.Contains("ends without parking"));
    }

    [Test]
    public void LightsBeforeAnyPointing_AreFlagged() {
        var w = Review((root, _) => root.Items.Insert(0,
            new TakeExposureInstruction { Name = "Early light", ExposureSeconds = 10 }));
        Assert.That(w, Has.Some.StartsWith("[Early light] Light frames are taken before anything points"));
    }

    [Test]
    public void CalibrationOnlySequence_HasNoMountWarnings() {
        var root = (SequentialContainer)SequenceExamples.Build("darks-biases")!.Root;
        Assert.That(SequenceAdvisor.Review(root), Is.Empty);
    }
}
