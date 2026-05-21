using NINA.Headless.Services.Sequencer;
using NINA.Headless.Services.Sequencer.Conditions;
using NINA.Headless.Services.Sequencer.Containers;
using NINA.Headless.Services.Sequencer.Instructions;
using NINA.Headless.Services.Sequencer.Triggers;
using NUnit.Framework;

namespace NINA.Headless.Test;

[TestFixture]
public class AdvancedSequencerTests {
    [Test]
    public void EntityIds_AreStableAcrossRoundtrip() {
        var doc = new SequenceDocument {
            Name = "Test",
            Root = new SequentialContainer {
                Name = "Root",
                Items = new() {
                    new TakeExposureInstruction { Name = "Lights", ExposureSeconds = 30, Count = 10, Filter = "L" },
                    new SwitchFilterInstruction { Name = "→R", FilterName = "R" },
                    new DitherInstruction { Name = "Dither" }
                }
            }
        };
        var origIds = ((SequentialContainer)doc.Root).Items.Select(i => i.Id).ToArray();

        var json = SequenceJson.Serialize(doc);
        var back = SequenceJson.Deserialize(json);

        var backIds = ((SequentialContainer)back.Root).Items.Select(i => i.Id).ToArray();
        Assert.That(backIds, Is.EqualTo(origIds));
    }

    [Test]
    public void Polymorphic_Roundtrip_PreservesTypes() {
        var doc = new SequenceDocument {
            Root = new DeepSkyObjectContainer {
                Name = "M31",
                Target = "M31",
                RaHours = 0.7124,
                DecDeg = 41.269,
                Items = new() {
                    new SwitchFilterInstruction { FilterName = "L" },
                    new TakeExposureInstruction { ExposureSeconds = 60, Count = 20, ImageType = "LIGHT" }
                },
                Triggers = new() {
                    new DitherAfterNExposuresTrigger { EveryNFrames = 3 },
                    new AutoFocusEveryNMinutesTrigger { Minutes = 30 }
                },
                Conditions = new() {
                    new LoopUntilAltitudeCondition { RaHours = 0.7124, DecDeg = 41.269, MinAltitudeDeg = 30 }
                },
                IsLoop = false
            }
        };

        var json = SequenceJson.Serialize(doc);
        var back = SequenceJson.Deserialize(json);

        var dso = back.Root as DeepSkyObjectContainer;
        Assert.That(dso, Is.Not.Null);
        Assert.That(dso!.Target, Is.EqualTo("M31"));
        Assert.That(dso.Items.Count, Is.EqualTo(2));
        Assert.That(dso.Items[0], Is.InstanceOf<SwitchFilterInstruction>());
        Assert.That(dso.Items[1], Is.InstanceOf<TakeExposureInstruction>());
        Assert.That(dso.Triggers.Count, Is.EqualTo(2));
        Assert.That(dso.Triggers[0], Is.InstanceOf<DitherAfterNExposuresTrigger>());
        Assert.That(dso.Conditions.Count, Is.EqualTo(1));
        Assert.That(dso.Conditions[0], Is.InstanceOf<LoopUntilAltitudeCondition>());
    }

    [Test]
    public void Validate_BubblesUpChildErrors() {
        var root = new SequentialContainer {
            Name = "Root",
            Items = new() {
                new TakeExposureInstruction { Name = "bad", ExposureSeconds = -1, Count = 0 },
                new SwitchFilterInstruction { Name = "no-target" } // missing FilterName and Position
            }
        };
        var errors = root.Validate();
        Assert.That(errors.Count, Is.GreaterThanOrEqualTo(3));
        Assert.That(errors.Any(e => e.Contains("Exposure")), Is.True);
        Assert.That(errors.Any(e => e.Contains("Count")), Is.True);
        Assert.That(errors.Any(e => e.Contains("FilterName")), Is.True);
    }

    [Test]
    public void Resolve_AllKnownTypes_Discriminated() {
        foreach (var (type, _, _) in SequenceEntityJsonConverter.KnownTypes) {
            var clr = SequenceEntityJsonConverter.Resolve(type);
            Assert.That(clr, Is.Not.Null, "Resolve failed for " + type);
        }
    }

    [Test]
    public void DeepSkyObject_ValidatesCoords() {
        var bad = new DeepSkyObjectContainer {
            Target = "", RaHours = 30, DecDeg = 95
        };
        var errors = bad.Validate();
        Assert.That(errors.Any(e => e.Contains("Target")), Is.True);
        Assert.That(errors.Any(e => e.Contains("RA")), Is.True);
        Assert.That(errors.Any(e => e.Contains("Dec")), Is.True);
    }

    [Test]
    public void TemplatedContainer_ValidatesTemplateName() {
        var bad = new TemplatedContainer { TemplateName = "" };
        Assert.That(bad.Validate().Any(e => e.Contains("TemplateName")), Is.True);
    }
}
