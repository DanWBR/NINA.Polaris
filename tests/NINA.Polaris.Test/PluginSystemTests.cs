using NINA.Polaris.Services.Sequencer;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Verifies the in-process plugin entity registration hooks. The
/// AssemblyLoadContext-based file scanner is exercised by an integration
/// run with the sample plugin under docs/sample-plugin (not part of CI).
/// </summary>
[TestFixture]
[NonParallelizable] // mutates static registries on SequenceEntityJsonConverter
public class PluginSystemTests {

    public class TestPluginInstruction : SequenceInstruction {
        public override string Type => "Test.PluginInstruction";
        public int Counter { get; set; }
        public override Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
            Counter++;
            return Task.CompletedTask;
        }
    }

    [Test]
    public void RegisterPluginEntity_AddsToResolver() {
        var disc = SequenceEntityJsonConverter.RegisterPluginEntity(
            typeof(TestPluginInstruction), "Plugins / Test");
        Assert.That(disc, Is.EqualTo("Test.PluginInstruction"));

        var resolved = SequenceEntityJsonConverter.Resolve("Test.PluginInstruction");
        Assert.That(resolved, Is.EqualTo(typeof(TestPluginInstruction)));
    }

    [Test]
    public void RegisterPluginEntity_AppearsInKnownTypes() {
        // SetUp / TestFixture-level state from the previous test may already
        // have registered this; the second call should be idempotent.
        try {
            SequenceEntityJsonConverter.RegisterPluginEntity(typeof(TestPluginInstruction), "Plugins / Test");
        } catch { /* already registered, fine */ }

        var known = SequenceEntityJsonConverter.KnownTypes;
        Assert.That(known.Any(k => k.Type == "Test.PluginInstruction"), Is.True);
    }

    [Test]
    public void RegisterPluginEntity_RoundTripsThroughJson() {
        try {
            SequenceEntityJsonConverter.RegisterPluginEntity(typeof(TestPluginInstruction), "Plugins / Test");
        } catch { /* already registered */ }

        var doc = new SequenceDocument {
            Name = "with-plugin",
            Root = new NINA.Polaris.Services.Sequencer.Containers.SequentialContainer {
                Items = new() {
                    new TestPluginInstruction { Counter = 42, Name = "Hi" }
                }
            }
        };
        var json = SequenceJson.Serialize(doc);
        Assert.That(json, Does.Contain("Test.PluginInstruction"));
        var back = SequenceJson.Deserialize(json);
        var root = back.Root as NINA.Polaris.Services.Sequencer.Containers.SequentialContainer;
        Assert.That(root, Is.Not.Null);
        Assert.That(root!.Items[0], Is.InstanceOf<TestPluginInstruction>());
        Assert.That(((TestPluginInstruction)root.Items[0]).Counter, Is.EqualTo(42));
    }

    [Test]
    public void RegisterPluginEntity_RejectsNonEntityType() {
        Assert.Throws<ArgumentException>(() =>
            SequenceEntityJsonConverter.RegisterPluginEntity(typeof(string), "X"));
    }
}
