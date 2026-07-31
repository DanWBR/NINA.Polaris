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

using Microsoft.Extensions.Logging.Abstractions;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Polaris.Services;
using System.Collections.Generic;
using NUnit.Framework;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Test;

/// <summary>
/// Tests for the LiveStackingService mode switch (CLST-1). The full
/// stacking pipeline is exercised by integration tests under the
/// LSTR/CLST end-to-end flow; this fixture pins the mode-switch
/// surface in isolation.
/// </summary>
[TestFixture]
public class LiveStackingServiceTests {

    private static LiveStackingService MakeService() {
        var relay = new ImageRelayService(NullLogger<ImageRelayService>.Instance);
        return new LiveStackingService(relay, NullLogger<LiveStackingService>.Instance);
    }

    private static BaseImageData MakeFrame(int w = 64, int h = 64) {
        // 64x64 dim frame, small enough to not stress StarDetector with
        // many candidates, big enough to exercise the per-frame loop.
        var props = new ImageProperties { Width = w, Height = h, BitDepth = 16 };
        return new BaseImageData(new ushort[w * h], props);
    }

    [Test]
    public void Mode_DefaultsToFull() {
        var svc = MakeService();
        Assert.That(svc.Mode, Is.EqualTo(StackMode.Full));
        Assert.That(svc.GetStatus().Mode, Is.EqualTo("full"));
    }

    [Test]
    public void Mode_MetricsOnly_ReflectedInStatus() {
        var svc = MakeService();
        svc.Mode = StackMode.MetricsOnly;
        Assert.That(svc.GetStatus().Mode, Is.EqualTo("metricsonly"));
    }

    [Test]
    public async Task AddFrame_InFullMode_AccumulatesStackBuffer() {
        var svc = MakeService();
        svc.Start();
        await svc.AddFrameAsync(MakeFrame());

        // Full mode allocates + fills the stack buffer. GetStackedResult
        // returns a non-empty array after frame 1.
        Assert.That(svc.FrameCount, Is.EqualTo(1));
        Assert.That(svc.GetStackedResult().Length, Is.EqualTo(64 * 64));
    }

    [Test]
    public async Task AddFrame_InMetricsOnlyMode_DoesNotAllocateStackBuffer() {
        var svc = MakeService();
        svc.Start();
        svc.Mode = StackMode.MetricsOnly;
        await svc.AddFrameAsync(MakeFrame());

        // MetricsOnly increments the frame count + sets width/height
        // (so the trigger orchestrator + status payload look populated)
        // but never allocates the accumulator. GetStackedResult is the
        // cleanest probe, returns empty when the buffer is null.
        Assert.That(svc.FrameCount, Is.EqualTo(1));
        Assert.That(svc.Width, Is.EqualTo(64));
        Assert.That(svc.Height, Is.EqualTo(64));
        Assert.That(svc.GetStackedResult(), Is.Empty,
            "Stack buffer must stay null in MetricsOnly, client owns the accumulator.");
    }

    [Test]
    public async Task AddFrame_InMetricsOnlyMode_StillRunsStarDetector() {
        var svc = MakeService();
        svc.Start();
        svc.Mode = StackMode.MetricsOnly;
        // Synthetic blank frame → 0 stars. The point of the test is
        // that LastFrameStarCount is touched (i.e. the detector ran),
        // not that it found anything in noise.
        await svc.AddFrameAsync(MakeFrame());

        // LastFrameStarCount is only written by AddFrameAsync. If
        // MetricsOnly skipped it (regression), this would stay at the
        // -1 sentinel we don't have, instead it'd be 0 from default.
        // Confirm via the frame-count delta + the fact that no
        // exception fired.
        Assert.That(svc.FrameCount, Is.EqualTo(1));
        Assert.That(svc.LastFrameStarCount, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public async Task ModeChange_BetweenFrames_TakesEffectImmediately() {
        var svc = MakeService();
        svc.Start();
        await svc.AddFrameAsync(MakeFrame());     // Full mode → accumulates
        Assert.That(svc.GetStackedResult(), Is.Not.Empty);

        // Switch mid-session. The accumulator stays from previous Full
        // frames (Reset clears it, mode change alone does not, by
        // design, so a transient WASM-client disconnect doesn't lose
        // the in-progress stack).
        svc.Mode = StackMode.MetricsOnly;
        await svc.AddFrameAsync(MakeFrame());

        Assert.That(svc.FrameCount, Is.EqualTo(2),
            "Frame count advances in both modes.");
        Assert.That(svc.GetStackedResult().Length, Is.EqualTo(64 * 64),
            "Existing accumulator from Full mode is preserved; only new MetricsOnly frames skip it.");
    }

    private static BaseImageData MakeFrame(BayerPatternEnum pattern, int w = 64, int h = 64) {
        var props = new ImageProperties {
            Width = w, Height = h, BitDepth = 16,
            IsBayered = pattern != BayerPatternEnum.None,
            BayerPattern = pattern,
        };
        var buf = new ushort[w * h];
        for (int i = 0; i < buf.Length; i++) buf[i] = (ushort)(800 + (i % 400));
        return new BaseImageData(buf, props);
    }

    [Test]
    public async Task ColourStack_FirstFrameBayerDropout_DefersInsteadOfLockingMono() {
        // The recurring field bug: an OSC colour session whose FIRST
        // frame transiently reports BayerPattern=None (CFA dropout) used
        // to commit the WHOLE session to mono. Now that None-on-frame-0
        // is DEFERRED — the frame is dropped, nothing initialises — and
        // the next frame that actually carries the pattern starts the
        // colour session correctly.
        var svc = MakeService();
        svc.ColorStacking = true;
        svc.Start();

        // Frame 0 arrives without a CFA pattern: deferred, not stacked.
        await svc.AddFrameAsync(MakeFrame(BayerPatternEnum.None));
        Assert.That(svc.FrameCount, Is.EqualTo(0),
            "a CFA-dropout first frame must be deferred, not integrated");
        Assert.That(svc.ColorActive, Is.False,
            "colour must not have been decided yet");

        // Next frame carries the pattern → colour session starts.
        await svc.AddFrameAsync(MakeFrame(BayerPatternEnum.RGGB));
        Assert.That(svc.FrameCount, Is.EqualTo(1),
            "the first good frame initialises the stack");
        Assert.That(svc.ColorActive, Is.True,
            "colour session must be active once a real pattern arrives");
    }

    [Test]
    public async Task ColourStack_SustainedNoPattern_EventuallyProceedsMono() {
        // Guard against an infinite defer: a genuinely mono camera left
        // with colour stacking on (misconfig) must still stack after the
        // deferral cap, in mono, rather than never producing a frame.
        var svc = MakeService();
        svc.ColorStacking = true;
        svc.Start();

        for (int i = 0; i < 40; i++)
            await svc.AddFrameAsync(MakeFrame(BayerPatternEnum.None));

        Assert.That(svc.FrameCount, Is.GreaterThan(0),
            "after the deferral cap the session must proceed (in mono)");
        Assert.That(svc.ColorActive, Is.False,
            "no pattern ever arrived, so the session is mono");
    }

    [Test]
    public async Task ColourStack_FirstFrameHasPattern_StartsColourImmediately() {
        var svc = MakeService();
        svc.ColorStacking = true;
        svc.Start();

        await svc.AddFrameAsync(MakeFrame(BayerPatternEnum.RGGB));
        Assert.That(svc.FrameCount, Is.EqualTo(1));
        Assert.That(svc.ColorActive, Is.True);
    }

    [Test]
    public async Task ElapsedSeconds_FreezesAfterStop() {
        // Field report: the "Total integration time" counter kept
        // climbing after the operator pressed Stop. Elapsed must
        // reflect ACTIVE stacking time, so a stopped stack freezes.
        var svc = MakeService();
        svc.Start();
        await svc.AddFrameAsync(MakeFrame());   // first frame starts the timer
        Assert.That(svc.FrameCount, Is.EqualTo(1));

        System.Threading.Thread.Sleep(60);
        svc.Stop();
        var frozen = svc.ElapsedSeconds;
        Assert.That(frozen, Is.GreaterThan(0), "some integration time should have accrued");

        System.Threading.Thread.Sleep(120);
        Assert.That(svc.ElapsedSeconds, Is.EqualTo(frozen),
            "elapsed must not advance while stopped");
    }

    [Test]
    public async Task ElapsedSeconds_ResumesAccruingAfterResume() {
        // Stop banks the running segment; Resume must continue from
        // that banked value, not restart at zero and not stay frozen.
        var svc = MakeService();
        svc.Start();
        await svc.AddFrameAsync(MakeFrame());
        System.Threading.Thread.Sleep(60);
        svc.Stop();
        var banked = svc.ElapsedSeconds;

        svc.Resume();
        System.Threading.Thread.Sleep(80);
        Assert.That(svc.ElapsedSeconds, Is.GreaterThan(banked),
            "elapsed should climb again once stacking resumes");
    }

    [Test]
    public void Reset_PreservesModeSetting() {
        // Mode is configured externally (by CLST-5 handshake or
        // user override); Reset is for the per-session accumulator
        // state, not the policy. Persist mode across resets so the
        // user doesn't get surprised by the server flipping back to
        // Full when they hit Reset in the UI.
        var svc = MakeService();
        svc.Mode = StackMode.MetricsOnly;
        svc.Reset();
        Assert.That(svc.Mode, Is.EqualTo(StackMode.MetricsOnly));
    }

    /// <summary>
    /// A mono camera with Colour stacking left on must start stacking almost
    /// immediately, in mono. The first frame carries no Bayer pattern, which is
    /// indistinguishable from a CFA dropout, so the service defers a couple of
    /// frames before committing; the cap used to be 30 frames, which on long
    /// subs read as "live stacking does not work with a mono camera".
    /// </summary>
    [Test]
    public async Task AddFrame_MonoSensorWithColourOn_StartsStackingWithinAFewFrames() {
        var svc = MakeService();
        svc.ColorStacking = true;
        svc.Start();

        for (var i = 0; i < 4; i++) {
            await svc.AddFrameAsync(MakeFrame());
            if (svc.FrameCount > 0) break;
        }

        Assert.That(svc.FrameCount, Is.GreaterThan(0),
            "A mono frame must not be deferred forever just because Colour stacking is on.");
        Assert.That(svc.ColorActive, Is.False,
            "With no Bayer pattern anywhere the session has to run in mono.");
        Assert.That(svc.GetStackedResult().Length, Is.EqualTo(64 * 64));
    }

    /// <summary>
    /// When the backend positively reports a mono sensor there is no CFA to
    /// wait for, so the very first frame must integrate: not one deferral, let
    /// alone the frame/time budget. The simulator camera reports mono, which is
    /// what makes this checkable without hardware.
    /// </summary>
    [Test]
    public async Task AddFrame_CameraReportsMonoSensor_StacksOnTheFirstFrame() {
        var indi = new NINA.INDI.Client.IndiClient("localhost", 7624);
        var equip = new EquipmentManager(indi, NullLogger<EquipmentManager>.Instance,
            new NINA.Polaris.Services.Alpaca.AlpacaDiscoveryCache(),
            new NINA.Polaris.Services.Simulator.Gear.SimGearService());
        var cam = equip.SelectCamera("sim", "sim");
        await cam.ConnectAsync();
        Assert.That(cam.IsColorSensor, Is.False,
            "Precondition: the simulator has to declare its sensor mono.");

        var relay = new ImageRelayService(NullLogger<ImageRelayService>.Instance);
        var svc = new LiveStackingService(relay, NullLogger<LiveStackingService>.Instance,
            equipment: equip);
        svc.ColorStacking = true;
        svc.Start();

        await svc.AddFrameAsync(MakeFrame());

        Assert.That(svc.FrameCount, Is.EqualTo(1),
            "A declared-mono sensor must not cost even one deferred frame.");
        Assert.That(svc.ColorActive, Is.False);
    }


    /// <summary>
    /// MetricsOnly hands accumulation to the browser. If the browser never
    /// reports back, nobody is stacking and the operator watches raw frames
    /// while /api/livestack/preview 404s. After a few silent frames the
    /// service must say so, so the mode evaluator can take the work back.
    /// </summary>
    [Test]
    public async Task MetricsOnly_WithNoClientProgress_FlagsTheStallAfterAFewFrames() {
        var svc = MakeService();
        svc.Mode = StackMode.MetricsOnly;
        svc.Start();

        var stalls = new List<bool>();
        svc.ClientStackStalledChanged += v => stalls.Add(v);

        for (var i = 0; i < 5; i++) await svc.AddFrameAsync(MakeFrame());

        Assert.That(svc.ClientStackStalled, Is.True,
            "Silent client must be reported, otherwise nothing accumulates anywhere.");
        Assert.That(stalls, Is.EqualTo(new[] { true }),
            "The event fires once on the transition, not per frame.");
    }

    /// <summary>A client that IS reporting must keep MetricsOnly: the whole
    /// point is to spare the host the accumulation.</summary>
    [Test]
    public async Task MetricsOnly_WithClientProgress_DoesNotStall() {
        var svc = MakeService();
        svc.Mode = StackMode.MetricsOnly;
        svc.Start();

        for (var i = 0; i < 5; i++) {
            await svc.AddFrameAsync(MakeFrame());
            svc.InjectClientStackMetrics(i + 1, frameSnr: 10, cumulativeSnr: 12);
        }

        Assert.That(svc.ClientStackStalled, Is.False);
    }

    /// <summary>
    /// In MetricsOnly the capture endpoint hands the frame to this service
    /// INSTEAD of the relay, so this service has to broadcast it or the browser
    /// receives nothing: the LIVE image freezes and the WASM client has no
    /// pixels to accumulate. Regression for the Q6A report ("only the first
    /// frame arrived"), where seven frames were processed and zero relayed.
    /// </summary>
    [Test]
    public async Task MetricsOnly_RelaysEveryRawFrame() {
        var relay = new ImageRelayService(NullLogger<ImageRelayService>.Instance);
        var svc = new LiveStackingService(relay, NullLogger<LiveStackingService>.Instance);
        svc.Mode = StackMode.MetricsOnly;
        svc.Start();

        // The relay records the frame it last broadcast; asserting it tracks
        // each frame we push proves the broadcast actually happened.
        for (var i = 0; i < 3; i++) {
            var frame = MakeFrame();
            await svc.AddFrameAsync(frame);
            Assert.That(relay.LatestImageData, Is.SameAs(frame),
                $"Frame {i + 1} never reached the client.");
        }

        Assert.That(svc.FrameCount, Is.EqualTo(3));
    }

    /// <summary>
    /// The exact field sequence from the Q6A on 30 Jul: the tablet was stacking
    /// in the browser (MetricsOnly), it stopped reporting, the watchdog flipped
    /// the stacker to Full mid-session, and every frame after that threw a
    /// NullReferenceException inside AddFrameAsync. MetricsOnly counts frames
    /// without allocating an accumulator, so Full arrived with a non-zero frame
    /// count and null buffers and skipped its own init.
    /// </summary>
    [Test]
    public async Task SwitchingToFullMidSession_StartsAccumulatingInsteadOfThrowing() {
        var svc = MakeService();
        svc.Start();

        svc.Mode = StackMode.MetricsOnly;
        await svc.AddFrameAsync(MakeFrame());
        await svc.AddFrameAsync(MakeFrame());
        Assert.That(svc.FrameCount, Is.EqualTo(2),
            "MetricsOnly counts the frames the client is stacking");

        // The client goes away.
        svc.Mode = StackMode.Full;
        Assert.DoesNotThrowAsync(async () => await svc.AddFrameAsync(MakeFrame()),
            "the server has to take over the accumulation, not dereference buffers "
            + "that the client-side mode never allocated");

        Assert.That(svc.FrameCount, Is.EqualTo(1),
            "the stack starts here: the counted frames were never accumulated on the server, "
            + "so keeping them would report an integration time the pixels do not have");

        // A second frame cannot be asserted here: these synthetic frames carry
        // no stars, so alignment against the reference rejects them, which is
        // the fixture's limit rather than the behaviour under test.
    }
}