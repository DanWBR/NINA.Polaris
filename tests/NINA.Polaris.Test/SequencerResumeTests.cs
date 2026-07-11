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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Sequencer;
using NINA.Polaris.Services.Sequencer.Containers;
using NINA.Polaris.Services.Sequencer.Instructions;
using NINA.INDI.Client;

namespace NINA.Polaris.Test;

/// <summary>
/// Resume semantics of the Advanced Sequencer (also the engine under PLAN
/// mode): a stopped run keeps its tree state; a resumed start skips
/// top-level entities already Completed, re-runs the interrupted one, and
/// TakeExposure instructions fast-forward past frames already captured.
/// </summary>
[TestFixture]
public class SequencerResumeTests {

    private static SequenceContext BareCtx(bool isResume = false) {
        var ctx = new SequenceContext(
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger.Instance);
        ctx.IsResume = isResume;
        return ctx;
    }

    private sealed class CountingInstruction : SequenceInstruction {
        public override string Type => "TestCounting";
        public int Runs;
        public override Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
            Runs++;
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedCondition : SequenceCondition {
        public override string Type => "TestCond";
        public readonly Queue<bool> Results = new();
        public bool Default = false;
        public override Task<bool> StillTrueAsync(SequenceContext ctx, CancellationToken ct)
            => Task.FromResult(Results.Count > 0 ? Results.Dequeue() : Default);
    }

    // ---- container skip semantics ----

    [Test]
    public async Task ResumeRun_SkipsStaleCompletedChildren_RunsTheRest() {
        var a = new CountingInstruction { Name = "A" };
        var b = new CountingInstruction { Name = "B" };
        var root = new SequentialContainer { Name = "Root", Items = { a, b } };

        // Simulate the interrupted previous run: A finished, B never ran.
        a.Status = SequenceEntityStatus.Completed;

        await root.ExecuteAsync(BareCtx(isResume: true), CancellationToken.None);

        Assert.That(a.Runs, Is.EqualTo(0), "completed entity must be skipped on resume");
        Assert.That(b.Runs, Is.EqualTo(1), "pending entity must run on resume");
    }

    [Test]
    public async Task FreshRun_RunsStaleCompletedChildrenToo() {
        var a = new CountingInstruction { Name = "A" };
        var b = new CountingInstruction { Name = "B" };
        var root = new SequentialContainer { Name = "Root", Items = { a, b } };
        a.Status = SequenceEntityStatus.Completed;   // stale, but NOT a resume

        await root.ExecuteAsync(BareCtx(isResume: false), CancellationToken.None);

        Assert.That(a.Runs, Is.EqualTo(1));
        Assert.That(b.Runs, Is.EqualTo(1));
    }

    [Test]
    public async Task ResumedLoop_SkipsStaleOnlyOnFirstPass() {
        var a = new CountingInstruction { Name = "A" };
        var b = new CountingInstruction { Name = "B" };
        var cond = new ScriptedCondition();
        // Pass 1: A skipped (stale Completed, consumes no condition),
        // B gated (1 result) + end-of-pass check (2). Pass 2: A gated (3),
        // B gated (4), end-of-pass check (5) = false → stop.
        cond.Results.Enqueue(true);
        cond.Results.Enqueue(true);
        cond.Results.Enqueue(true);
        cond.Results.Enqueue(true);
        cond.Results.Enqueue(false);
        var root = new SequentialContainer {
            Name = "Root", IsLoop = true,
            Items = { a, b }, Conditions = { cond }
        };
        a.Status = SequenceEntityStatus.Completed;

        await root.ExecuteAsync(BareCtx(isResume: true), CancellationToken.None);

        Assert.That(a.Runs, Is.EqualTo(1), "stale-completed child skips pass 1 only");
        Assert.That(b.Runs, Is.EqualTo(2), "pending child runs every pass");
    }

    // ---- TakeExposure progress counter (real capture via sim camera) ----

    private static (SequenceContext ctx, EquipmentManager equip) CaptureCtx() {
        var indi = new IndiClient("localhost", 7624);
        var equip = new EquipmentManager(indi, NullLogger<EquipmentManager>.Instance,
            new NINA.Polaris.Services.Alpaca.AlpacaDiscoveryCache(),
            new NINA.Polaris.Services.Simulator.Gear.SimGearService());
        var relay = new ImageRelayService(NullLogger<ImageRelayService>.Instance);
        var liveStack = new LiveStackingService(relay, NullLogger<LiveStackingService>.Instance);
        var emptyConfig = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var profile = new ProfileService(emptyConfig, NullLogger<ProfileService>.Instance);
        profile.Active.ImageOutputDir = "";   // SaveImage no-ops, keeps tests off disk
        var imageWriter = new ImageWriterService(equip, profile, NullLogger<ImageWriterService>.Instance);
        var ctx = new SequenceContext(
            equip, relay, liveStack, null!, null!, null!, null!, null!,
            imageWriter, profile, new CaptureProgressService(),
            NullLogger.Instance);
        return (ctx, equip);
    }

    [Test]
    public async Task TakeExposure_ResumesAtRetainedFrame_ThenSelfResetsWhenComplete() {
        var (ctx, equip) = CaptureCtx();
        var cam = equip.SelectCamera("sim", "Simulator");
        await cam.ConnectAsync();

        var tx = new TakeExposureInstruction { ExposureSeconds = 0, Count = 3 };

        // Simulate 2 frames captured before an interruption.
        tx.CompletedCount = 2;
        await tx.ExecuteAsync(ctx, CancellationToken.None);
        Assert.That(ctx.FramesCompleted, Is.EqualTo(1), "only the missing frame is captured");
        Assert.That(tx.CompletedCount, Is.EqualTo(3));

        // Re-entered fully complete (a loop container's next pass): the
        // counter self-resets and a full new set is captured.
        var (ctx2, equip2) = CaptureCtx();
        var cam2 = equip2.SelectCamera("sim", "Simulator");
        await cam2.ConnectAsync();
        await tx.ExecuteAsync(ctx2, CancellationToken.None);
        Assert.That(ctx2.FramesCompleted, Is.EqualTo(3), "full set on re-entry after completion");
        Assert.That(tx.CompletedCount, Is.EqualTo(3));
    }

    [Test]
    public void ResetRuntimeState_KeepsProgress_ResetProgressClearsIt() {
        var tx = new TakeExposureInstruction { Count = 5, CompletedCount = 2 };
        tx.ResetRuntimeState();
        Assert.That(tx.CompletedCount, Is.EqualTo(2),
            "runtime reset (retries / per-child reset) must keep capture progress");
        tx.ResetProgress();
        Assert.That(tx.CompletedCount, Is.EqualTo(0));
    }

    // ---- engine-level resumable detection + fresh-start reset ----

    private static AdvancedSequenceEngine MakeEngine() {
        var emptyConfig = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var profile = new ProfileService(emptyConfig, NullLogger<ProfileService>.Instance);
        var templates = new SequenceTemplateStore(emptyConfig, profile,
            NullLogger<SequenceTemplateStore>.Instance);
        var services = new ServiceCollection().BuildServiceProvider();
        return new AdvancedSequenceEngine(services, templates,
            NullLogger<AdvancedSequenceEngine>.Instance);
    }

    [Test]
    public void HasResumableProgress_TracksTreeState() {
        var engine = MakeEngine();
        var tx = new TakeExposureInstruction { ExposureSeconds = 1, Count = 5 };
        var root = new SequentialContainer { Name = "Root", Items = { tx } };
        engine.Load(new SequenceDocument { Name = "T", Root = root });

        // Freshly loaded: nothing ran yet.
        Assert.That(engine.HasResumableProgress, Is.False);

        // Simulate an interrupted run: cancel path marks the root Skipped
        // and the instruction kept partial progress.
        root.Status = SequenceEntityStatus.Skipped;
        tx.CompletedCount = 2;
        Assert.That(engine.HasResumableProgress, Is.True);

        // A completed run has nothing to resume.
        root.Status = SequenceEntityStatus.Completed;
        Assert.That(engine.HasResumableProgress, Is.False);
    }

    [Test]
    public void Load_ClearsRetainedProgress() {
        var engine = MakeEngine();
        var tx = new TakeExposureInstruction { ExposureSeconds = 1, Count = 5, CompletedCount = 3 };
        var root = new SequentialContainer { Name = "Root", Items = { tx } };
        root.Status = SequenceEntityStatus.Skipped;

        engine.Load(new SequenceDocument { Name = "T", Root = root });

        Assert.That(tx.CompletedCount, Is.EqualTo(0),
            "loading a document is a fresh start; retained progress must clear");
        Assert.That(engine.HasResumableProgress, Is.False);
    }
}
