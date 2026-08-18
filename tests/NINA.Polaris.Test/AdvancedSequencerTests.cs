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

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Polaris.Services.Sequencer;
using NINA.Polaris.Services.Sequencer.Conditions;
using NINA.Polaris.Services.Sequencer.Containers;
using NINA.Polaris.Services.Sequencer.Instructions;
using NINA.Polaris.Services.Sequencer.Triggers;
using NUnit.Framework;

namespace NINA.Polaris.Test;

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

    // The Alpine frontend consumes camelCase property names and a literal
    // "$type" discriminator. Lock that wire contract so a future options
    // change doesn't silently break the tree editor's save/load.
    [Test]
    public void Serialize_EmitsCamelCaseAndDollarType() {
        var doc = new SequenceDocument {
            Root = new SequentialContainer {
                Name = "Root",
                Items = new() { new TakeExposureInstruction { ExposureSeconds = 30, Count = 5 } }
            }
        };
        var json = SequenceJson.Serialize(doc);
        Assert.That(json, Does.Contain("\"$type\""), "missing $type discriminator");
        Assert.That(json, Does.Contain("\"exposureSeconds\""), "params must be camelCase");
        Assert.That(json, Does.Not.Contain("\"ExposureSeconds\""), "must not emit PascalCase");
        Assert.That(json, Does.Contain("\"items\""), "container children must serialize");
    }

    // Round-trips through the camelCase + case-insensitive options, which is
    // what the /document endpoints now use (the default minimal-API serializer
    // can't even read the interface-typed Root).
    [Test]
    public void Deserialize_CamelCasePayload_PopulatesConcreteParams() {
        const string json = """
        { "name":"t", "root": { "$type":"Sequential", "name":"Root", "items":[
            { "$type":"TakeExposure", "exposureSeconds":42, "count":7, "filter":"Ha" }
        ]}}
        """;
        var doc = SequenceJson.Deserialize(json);
        var root = doc.Root as SequentialContainer;
        Assert.That(root, Is.Not.Null);
        var exp = root!.Items[0] as TakeExposureInstruction;
        Assert.That(exp, Is.Not.Null);
        Assert.That(exp!.ExposureSeconds, Is.EqualTo(42));
        Assert.That(exp.Count, Is.EqualTo(7));
        Assert.That(exp.Filter, Is.EqualTo("Ha"));
    }

    // Aux / guide optical-train controls: the focuser/auto-focus target and the
    // new aux-camera instructions must survive the camelCase + $type roundtrip
    // so the tree editor can drive the aux/guide trains.
    [Test]
    public void AuxAndGuide_Instructions_RoundtripTargets() {
        var doc = new SequenceDocument {
            Root = new SequentialContainer {
                Name = "Root",
                Items = new() {
                    new MoveFocuserInstruction { Position = 1234, FocuserTarget = "guide" },
                    new AutoFocusInstruction { FocuserSource = "aux" },
                    new CoolAuxCameraInstruction { TargetTempC = -5, ToleranceDegC = 0.5 },
                    new WarmAuxCameraInstruction { TargetTempC = 18 },
                    new TakeAuxExposureInstruction { ExposureSeconds = 3, Count = 4, Gain = 120, Binning = 2 }
                }
            }
        };

        var back = SequenceJson.Deserialize(SequenceJson.Serialize(doc));
        var root = back.Root as SequentialContainer;
        Assert.That(root, Is.Not.Null);

        var mf = root!.Items[0] as MoveFocuserInstruction;
        Assert.That(mf, Is.Not.Null);
        Assert.That(mf!.Position, Is.EqualTo(1234));
        Assert.That(mf.FocuserTarget, Is.EqualTo("guide"));

        var af = root.Items[1] as AutoFocusInstruction;
        Assert.That(af!.FocuserSource, Is.EqualTo("aux"));

        var cool = root.Items[2] as CoolAuxCameraInstruction;
        Assert.That(cool!.TargetTempC, Is.EqualTo(-5));
        Assert.That(cool.ToleranceDegC, Is.EqualTo(0.5));

        var warm = root.Items[3] as WarmAuxCameraInstruction;
        Assert.That(warm!.TargetTempC, Is.EqualTo(18));

        var aux = root.Items[4] as TakeAuxExposureInstruction;
        Assert.That(aux!.ExposureSeconds, Is.EqualTo(3));
        Assert.That(aux.Count, Is.EqualTo(4));
        Assert.That(aux.Gain, Is.EqualTo(120));
        Assert.That(aux.Binning, Is.EqualTo(2));
    }

    [Test]
    public void MoveFocuser_DefaultsToMainTarget() {
        Assert.That(new MoveFocuserInstruction().FocuserTarget, Is.EqualTo("main"));
        Assert.That(new AutoFocusInstruction().FocuserSource, Is.EqualTo("main"));
        var doc = new SequenceDocument {
            Root = new SequentialContainer { Items = new() { new MoveFocuserInstruction { Position = 10 } } }
        };
        var back = SequenceJson.Deserialize(SequenceJson.Serialize(doc));
        var mf = (back.Root as SequentialContainer)!.Items[0] as MoveFocuserInstruction;
        Assert.That(mf!.FocuserTarget, Is.EqualTo("main"));
    }

    [Test]
    public void DefaultParams_TakeExposure_HasCamelCaseScalarsOnly() {
        var defaults = SequenceEntityJsonConverter.DefaultParams("TakeExposure");
        Assert.That(defaults, Is.Not.Null);
        Assert.That(defaults!.ContainsKey("exposureSeconds"), Is.True);
        Assert.That(defaults.ContainsKey("count"), Is.True);
        // Structural / runtime keys must be excluded.
        Assert.That(defaults.ContainsKey("id"), Is.False);
        Assert.That(defaults.ContainsKey("name"), Is.False);
        Assert.That(defaults.ContainsKey("status"), Is.False);
    }

    [Test]
    public void DefaultParams_Container_ExcludesChildCollections() {
        var defaults = SequenceEntityJsonConverter.DefaultParams("Sequential");
        Assert.That(defaults, Is.Not.Null);
        Assert.That(defaults!.ContainsKey("items"), Is.False);
        Assert.That(defaults.ContainsKey("triggers"), Is.False);
        Assert.That(defaults.ContainsKey("conditions"), Is.False);
    }

    // ----- Execution semantics: Attempts / ErrorBehavior + trigger cascade -----

    private static SequenceContext TestCtx() => new SequenceContext(
        null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
        new NINA.Polaris.Services.DitherBarrier(null!, null!, NullLogger<NINA.Polaris.Services.DitherBarrier>.Instance),
        NullLogger.Instance);

    /// <summary>Test instruction: counts runs, optionally throws on the first N.</summary>
    private sealed class CountingInstruction : SequenceInstruction {
        public override string Type => "TestCounting";
        public int Runs;
        public int FailFirst;   // throw on the first N executions
        public override Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
            Runs++;
            if (Runs <= FailFirst) throw new InvalidOperationException($"boom #{Runs}");
            return Task.CompletedTask;
        }
    }

    /// <summary>Test trigger: always wants to fire; counts how many times it fires.</summary>
    private sealed class CountingTrigger : SequenceTrigger {
        public override string Type => "TestTrigger";
        public int Fired;
        public override Task<bool> ShouldFireAsync(SequenceContext ctx, CancellationToken ct) => Task.FromResult(true);
        public override Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) { Fired++; return Task.CompletedTask; }
    }

    /// <summary>Test condition: returns queued bools, then a default.</summary>
    private sealed class ScriptedCondition : SequenceCondition {
        public override string Type => "TestCond";
        public readonly Queue<bool> Results = new();
        public bool Default = true;
        public override Task<bool> StillTrueAsync(SequenceContext ctx, CancellationToken ct)
            => Task.FromResult(Results.Count > 0 ? Results.Dequeue() : Default);
    }

    [Test]
    public async Task Attempts_RetriesUntilSuccess() {
        var prev = SequenceContainer.RetryBackoff;
        SequenceContainer.RetryBackoff = TimeSpan.Zero;
        try {
            var inst = new CountingInstruction { Attempts = 3, FailFirst = 2 };
            var root = new SequentialContainer { Name = "Root", Items = { inst } };
            await root.ExecuteAsync(TestCtx(), CancellationToken.None);
            Assert.That(inst.Runs, Is.EqualTo(3));
            Assert.That(inst.Status, Is.EqualTo(SequenceEntityStatus.Completed));
        } finally { SequenceContainer.RetryBackoff = prev; }
    }

    [Test]
    public void AbortRun_StopsTheWholeSequence() {
        var bad = new CountingInstruction { FailFirst = 1 };   // default ErrorBehavior = AbortRun
        var next = new CountingInstruction();
        var root = new SequentialContainer { Name = "Root", Items = { bad, next } };
        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await root.ExecuteAsync(TestCtx(), CancellationToken.None));
        Assert.That(bad.Status, Is.EqualTo(SequenceEntityStatus.Failed));
        Assert.That(next.Runs, Is.EqualTo(0));   // never reached
    }

    [Test]
    public async Task ContinueOnError_RunsTheNextSibling() {
        var bad = new CountingInstruction { FailFirst = 1, ErrorBehavior = InstructionErrorBehavior.ContinueOnError };
        var good = new CountingInstruction();
        var root = new SequentialContainer { Name = "Root", Items = { bad, good } };
        await root.ExecuteAsync(TestCtx(), CancellationToken.None);   // must not throw
        Assert.That(bad.Status, Is.EqualTo(SequenceEntityStatus.Failed));
        Assert.That(good.Runs, Is.EqualTo(1));
        Assert.That(good.Status, Is.EqualTo(SequenceEntityStatus.Completed));
    }

    [Test]
    public async Task SkipBlock_StopsCurrentContainerButParentContinues() {
        var bad = new CountingInstruction { FailFirst = 1, ErrorBehavior = InstructionErrorBehavior.SkipBlock };
        var skipped = new CountingInstruction();
        var inner = new SequentialContainer { Name = "Inner", Items = { bad, skipped } };
        var afterInner = new CountingInstruction();
        var root = new SequentialContainer { Name = "Root", Items = { inner, afterInner } };
        await root.ExecuteAsync(TestCtx(), CancellationToken.None);   // must not throw
        Assert.That(bad.Status, Is.EqualTo(SequenceEntityStatus.Failed));
        Assert.That(skipped.Runs, Is.EqualTo(0));       // rest of inner skipped
        Assert.That(afterInner.Runs, Is.EqualTo(1));    // parent kept going
    }

    [Test]
    public async Task ParentTriggerCascadesToNestedChildren() {
        var trig = new CountingTrigger();
        var leaf1 = new CountingInstruction();
        var leaf2 = new CountingInstruction();
        var inner = new SequentialContainer { Name = "Inner", Items = { leaf1, leaf2 } };
        var root = new SequentialContainer { Name = "Root", Triggers = { trig }, Items = { inner } };

        await root.ExecuteAsync(TestCtx(), CancellationToken.None);

        // Root evaluates the trigger before its 1 child (inner) = 1; inner
        // inherits it and evaluates before each of its 2 leaves = 2. Total 3.
        // Without the cascade it would only be 1.
        Assert.That(trig.Fired, Is.EqualTo(3));
    }

    [Test]
    public async Task LoopConditions_GateEveryItem_NotOnlyPerIteration() {
        // Condition holds before item0, fails before item1 → the loop stops
        // mid-iteration: item0 ran once, item1 never ran.
        var i0 = new CountingInstruction();
        var i1 = new CountingInstruction();
        var cond = new ScriptedCondition { Default = false };
        cond.Results.Enqueue(true);   // before item0
        cond.Results.Enqueue(false);  // before item1 -> stop
        var root = new SequentialContainer {
            Name = "Loop", IsLoop = true, Items = { i0, i1 }, Conditions = { cond }
        };
        await root.ExecuteAsync(TestCtx(), CancellationToken.None);
        Assert.That(i0.Runs, Is.EqualTo(1));
        Assert.That(i1.Runs, Is.EqualTo(0));
    }

    [Test]
    public async Task ConditionalContainer_RunsChildrenWhenPredicateHolds() {
        var item = new CountingInstruction();
        var cond = new ScriptedCondition { Default = true };
        var c = new ConditionalContainer { Name = "If", Items = { item }, Conditions = { cond } };
        await c.ExecuteAsync(TestCtx(), CancellationToken.None);
        Assert.That(item.Runs, Is.EqualTo(1));
    }

    [Test]
    public async Task ConditionalContainer_SkipsChildrenWhenPredicateFails() {
        var item = new CountingInstruction();
        var cond = new ScriptedCondition { Default = false };
        var c = new ConditionalContainer { Name = "If", Items = { item }, Conditions = { cond } };
        await c.ExecuteAsync(TestCtx(), CancellationToken.None);
        Assert.That(item.Runs, Is.EqualTo(0));
    }
}