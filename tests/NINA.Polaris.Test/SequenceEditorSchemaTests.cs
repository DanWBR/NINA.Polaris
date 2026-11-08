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
using NINA.Polaris.Services.Sequencer.Containers;
using NINA.Polaris.Services.Sequencer.Instructions;
using NINA.Polaris.Services.Sequencer.Triggers;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class SequenceEditorSchemaTests {
    private static SequenceEntityJsonConverter.FieldInfo Field(string type, string key)
        => SequenceEntityJsonConverter.FieldSchema(type).Single(f => f.Key == key);

    // The bug: optional parameters start empty, the wire format drops nulls,
    // and the editor (which built its fields from the JSON keys) never showed
    // them. The schema has to list them.
    [Test]
    public void TakeExposure_ListsGainAndOffsetAsOptionalIntegers() {
        Assert.That(Field("TakeExposure", "gain"), Is.EqualTo(new SequenceEntityJsonConverter.FieldInfo("gain", "int", true, null)));
        Assert.That(Field("TakeExposure", "offset").Nullable, Is.True);
        Assert.That(Field("TakeExposure", "filter"), Is.EqualTo(new SequenceEntityJsonConverter.FieldInfo("filter", "string", true, null)));
        Assert.That(Field("TakeExposure", "exposureSeconds").Kind, Is.EqualTo("number"));
        Assert.That(Field("TakeExposure", "exposureSeconds").Nullable, Is.False);
        Assert.That(Field("TakeExposure", "imageType").Nullable, Is.False);
    }

    [Test]
    public void Schema_LeavesOutRuntimeAndStructuralKeys() {
        var keys = SequenceEntityJsonConverter.FieldSchema("TakeExposure").Select(f => f.Key).ToList();
        Assert.That(keys, Has.None.AnyOf("id", "name", "description", "status", "completedCount"));
        var dso = SequenceEntityJsonConverter.FieldSchema("DeepSkyObject").Select(f => f.Key).ToList();
        Assert.That(dso, Has.None.AnyOf("windowEndUtc", "nextTargetStartUtc", "hasNextTarget", "items", "triggers"));
        Assert.That(dso, Does.Contain("raHours").And.Contain("decDeg").And.Contain("isLoop"));
    }

    [Test]
    public void Schema_EnumsCarryTheirMembers() {
        var f = Field("TakeExposure", "errorBehavior");
        Assert.That(f.Kind, Is.EqualTo("enum"));
        Assert.That(f.Options, Is.EquivalentTo(new[] { "AbortRun", "ContinueOnError", "SkipBlock" }));
    }

    [Test]
    public void EveryKnownType_HasASchema() {
        foreach (var (type, _, _) in SequenceEntityJsonConverter.KnownTypes)
            Assert.DoesNotThrow(() => SequenceEntityJsonConverter.FieldSchema(type), type);
        Assert.That(SequenceEntityJsonConverter.FieldSchema("NoSuchType"), Is.Empty);
    }

    // Status is [JsonIgnore], so the editor gets it from the progress
    // snapshot, keyed by id, triggers and conditions included.
    [Test]
    public void Progress_CoversEveryEntityById_WithFrameCounts() {
        var dso = new DeepSkyObjectContainer { Name = "M42" };
        var flip = new MeridianFlipTrigger { Name = "Flip" };
        var light = new TakeExposureInstruction { Name = "Light", Count = 10, CompletedCount = 3 };
        dso.Triggers.Add(flip);
        dso.Items.Add(light);
        var root = new SequentialContainer { Name = "Night" };
        root.Items.Add(dso);
        light.Status = SequenceEntityStatus.Running;

        var map = new Dictionary<string, SequenceProgress.EntityState>();
        SequenceProgress.Collect(root, map);

        Assert.That(map.Keys, Is.EquivalentTo(new[] { root.Id, dso.Id, flip.Id, light.Id }));
        Assert.That(map[light.Id].Status, Is.EqualTo("Running"));
        Assert.That((map[light.Id].CompletedCount, map[light.Id].Count), Is.EqualTo(((int?)3, (int?)10)));
        Assert.That(map[dso.Id].CompletedCount, Is.Null);
    }
}
