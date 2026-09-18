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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.INDI.Client;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Plan;
using NINA.Polaris.Services.Sequencer;
using NINA.Polaris.Services.Sequencer.Containers;
using NINA.Polaris.Services.Sequencer.Triggers;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The guide-loss hold: when the safety guard's breaker trips under a PLAN,
/// the target is parked and retried on a schedule, and given up when its
/// window closes, the next target's window opens, or (no window) after a
/// fixed number of attempts with something left to do. The timing rules are
/// pure functions; the plan-level behaviour is the skip propagating out of a
/// target container while the plan goes on.
/// </summary>
[TestFixture]
public class GuideLossHoldTests {

    private static readonly DateTime RunStart = new(2026, 9, 18, 22, 0, 0, DateTimeKind.Utc);

    [Test]
    public void RetryDelays_GrowThenHoldAtFifteenMinutes() {
        Assert.That(GuideLossHold.DelayFor(1), Is.EqualTo(TimeSpan.FromMinutes(3)));
        Assert.That(GuideLossHold.DelayFor(2), Is.EqualTo(TimeSpan.FromMinutes(5)));
        Assert.That(GuideLossHold.DelayFor(3), Is.EqualTo(TimeSpan.FromMinutes(10)));
        Assert.That(GuideLossHold.DelayFor(4), Is.EqualTo(TimeSpan.FromMinutes(15)));
        Assert.That(GuideLossHold.DelayFor(9), Is.EqualTo(TimeSpan.FromMinutes(15)));
        Assert.That(GuideLossHold.DelayFor(0), Is.EqualTo(TimeSpan.FromMinutes(3)));
    }

    [Test]
    public void Decide_RetriesWhileTheWindowIsOpen_NoMatterHowManyAttempts() {
        var windowEnd = RunStart.AddHours(3);
        var d = GuideLossHold.Decide(attempt: 12, nowUtc: RunStart.AddHours(2),
            windowEndUtc: windowEnd, nextTargetStartUtc: null, hasNextTarget: true);
        Assert.That(d, Is.EqualTo(HoldDecision.Retry));
    }

    [Test]
    public void Decide_GivesUpWhenTheWindowCloses() {
        var windowEnd = RunStart.AddHours(3);
        var d = GuideLossHold.Decide(2, RunStart.AddHours(3), windowEnd, null, hasNextTarget: false);
        Assert.That(d, Is.EqualTo(HoldDecision.SkipWindowClosed));
    }

    [Test]
    public void Decide_GivesUpWhenTheNextTargetsWindowOpens() {
        var nextStart = RunStart.AddHours(2);
        var d = GuideLossHold.Decide(1, RunStart.AddHours(2).AddMinutes(1), null, nextStart, hasNextTarget: true);
        Assert.That(d, Is.EqualTo(HoldDecision.SkipNextTargetDue));
    }

    [Test]
    public void Decide_WithoutAWindow_GivesUpAfterTheAttemptBudget_OnlyWhenSomethingFollows() {
        var last = GuideLossHold.MaxAttemptsWithoutWindow;
        Assert.That(GuideLossHold.Decide(last, RunStart, null, null, hasNextTarget: true),
            Is.EqualTo(HoldDecision.Retry), "the budget itself is still an attempt");
        Assert.That(GuideLossHold.Decide(last + 1, RunStart, null, null, hasNextTarget: true),
            Is.EqualTo(HoldDecision.SkipAttemptsExhausted));
        Assert.That(GuideLossHold.Decide(last + 30, RunStart, null, null, hasNextTarget: false),
            Is.EqualTo(HoldDecision.Retry), "the last target keeps trying until the plan ends");
    }

    [Test]
    public void ResolveTimeOfDay_PicksTheOccurrenceThatBelongsToThisRun() {
        Assert.That(GuideLossHold.ResolveTimeOfDay("23:30", RunStart), Is.EqualTo(RunStart.Date.AddHours(23.5)));
        Assert.That(GuideLossHold.ResolveTimeOfDay("03:00", RunStart), Is.EqualTo(RunStart.Date.AddDays(1).AddHours(3)),
            "an early-morning time is tomorrow's");
        Assert.That(GuideLossHold.ResolveTimeOfDay("", RunStart), Is.Null);
        Assert.That(GuideLossHold.ResolveTimeOfDay("nope", RunStart), Is.Null);
    }

    [Test]
    public void Compiler_HandsEachTargetWhatTheHoldNeeds() {
        var plan = new ImagingPlan {
            Targets = {
                new PlanTarget { Name = "A", ScheduleMode = PlanScheduleMode.TimeWindow, StartAtUtc = "22:00", EndAtUtc = "00:30",
                    Frames = { new PlanFrame { Count = 1 } } },
                new PlanTarget { Name = "off", Enabled = false, Frames = { new PlanFrame { Count = 1 } } },
                new PlanTarget { Name = "B", ScheduleMode = PlanScheduleMode.Frames, Frames = { new PlanFrame { Count = 1 } } },
                new PlanTarget { Name = "C", ScheduleMode = PlanScheduleMode.TimeWindow, StartAtUtc = "02:00", EndAtUtc = "04:00",
                    Frames = { new PlanFrame { Count = 1 } } },
            }
        };
        var doc = new PlanCompilerService().Compile(plan);
        var dsos = new List<DeepSkyObjectContainer>();
        foreach (var item in ((SequenceContainer)doc.Root).Items)
            if (item is DeepSkyObjectContainer d) dsos.Add(d);

        Assert.That(dsos.Count, Is.EqualTo(3), "the disabled target is not compiled");
        Assert.That(dsos[0].WindowEndUtc, Is.EqualTo("00:30"));
        Assert.That(dsos[0].NextTargetStartUtc, Is.Null, "B has no window");
        Assert.That(dsos[0].HasNextTarget, Is.True);
        Assert.That(dsos[1].WindowEndUtc, Is.Null);
        Assert.That(dsos[1].NextTargetStartUtc, Is.EqualTo("02:00"), "C's window start, the disabled target is skipped over");
        Assert.That(dsos[1].HasNextTarget, Is.True);
        Assert.That(dsos[2].WindowEndUtc, Is.EqualTo("04:00"));
        Assert.That(dsos[2].HasNextTarget, Is.False);
    }

    // ---- skip propagation through the tree -------------------------------

    private static SequenceContext BareCtx() => new(
        null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
        new DitherBarrier(null!, NullLogger<DitherBarrier>.Instance), NullLogger.Instance);

    private sealed class GivesUpInstruction : SequenceInstruction {
        public override string Type => "TestGivesUp";
        public override Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) =>
            throw new TargetSkippedException("Skipped: its time window closed while waiting for the guide star");
    }

    private sealed class CountingInstruction : SequenceInstruction {
        public override string Type => "TestCounting";
        public int Runs;
        public override Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) { Runs++; return Task.CompletedTask; }
    }

    [Test]
    public async Task ASkippedTarget_IsMarkedSkipped_AndThePlanGoesOnToTheNext() {
        var after = new CountingInstruction { Name = "next target's work" };
        var given = new DeepSkyObjectContainer { Name = "A", Target = "A", CenterOnStart = false };
        given.Items.Add(new GivesUpInstruction { Name = "exposures" });
        var root = new SequentialContainer { Name = "plan" };
        root.Items.Add(given);
        root.Items.Add(after);

        await root.ExecuteAsync(BareCtx(), CancellationToken.None);

        Assert.That(given.Status, Is.EqualTo(SequenceEntityStatus.Skipped));
        Assert.That(given.Error, Does.Contain("time window closed"));
        Assert.That(after.Runs, Is.EqualTo(1), "the item after the skipped target still runs");
    }

    [Test]
    public void ASkipOutsideAnyTarget_LeavesTheContainer() {
        var root = new SequentialContainer { Name = "plain sequence" };
        root.Items.Add(new GivesUpInstruction { Name = "step" });
        Assert.ThrowsAsync<TargetSkippedException>(async () => await root.ExecuteAsync(BareCtx(), CancellationToken.None));
    }

    [Test]
    public void HoldState_KeepsTheFirstReason_AndClearsCompletely() {
        var h = new SequenceHoldState();
        Assert.That(h.Requested, Is.False);
        h.Request("clouds");
        Assert.That(h.Requested, Is.True);
        Assert.That(h.Reason, Is.EqualTo("clouds"));
        h.Clear();
        Assert.That(h.Requested, Is.False);
        Assert.That(h.Reason, Is.Null);
    }

    [Test]
    public void RestoreGuiding_StaysOut_WhileAHoldIsPending() {
        var config = new ConfigurationBuilder().Build();
        var profiles = new ProfileService(config, NullLogger<ProfileService>.Instance);
        profiles.ActiveEquipmentProfile.GuiderDriver = "phd2";
        var indi = new IndiClient("localhost", 7624);
        var equipment = new EquipmentManager(indi, NullLogger<EquipmentManager>.Instance,
            new NINA.Polaris.Services.Alpaca.AlpacaDiscoveryCache(),
            new NINA.Polaris.Services.Simulator.Gear.SimGearService());
        var phd2 = new PHD2Client(NullLogger<PHD2Client>.Instance);
        var native = new NativeGuider(equipment, profiles, NullLogger<NativeGuider>.Instance);
        var guiders = new ActiveGuiderProvider(profiles, phd2, native);
        var ctx = new SequenceContext(equipment, null!, null!, phd2, guiders, null!, null!, null!, null!,
            null!, profiles, null!, null!, null!,
            new DitherBarrier(guiders, NullLogger<DitherBarrier>.Instance), NullLogger.Instance);

        var trigger = new RestoreGuidingTrigger();
        ctx.Scratch[$"RestoreGuiding:{trigger.Id}:wasGuiding"] = true;   // it had been guiding
        ctx.Hold.Request("guide star lost");

        // Disconnected PHD2 short-circuits before the hold check, so the test
        // says only that a pending hold never makes the trigger fire.
        Assert.That(trigger.ShouldFireAsync(ctx, CancellationToken.None).Result, Is.False);
    }
}
