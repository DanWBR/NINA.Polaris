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
    public async Task AddFrame_AccumulatesStackBuffer() {
        var svc = MakeService();
        svc.Start();
        await svc.AddFrameAsync(MakeFrame());

        // The service allocates + fills the stack buffer. GetStackedResult
        // returns a non-empty array after frame 1.
        Assert.That(svc.FrameCount, Is.EqualTo(1));
        Assert.That(svc.GetStackedResult().Length, Is.EqualTo(64 * 64));
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
    /// The browser has to receive something every time a frame is integrated,
    /// or the LIVE image freezes while the counter climbs. Regression for the
    /// Q6A report ("only the first frame arrived"), where seven frames were
    /// processed and zero reached the client.
    ///
    /// The service relays the ACCUMULATOR (FrameKind.LiveStack), not the input
    /// frame: the client ignores raw frames on the LIVE canvas while the stack
    /// runs, which is what stopped it flashing an unstacked sub between
    /// updates. Asserting identity with the input frame would therefore assert
    /// the bug.
    /// </summary>
    [Test]
    public async Task RelaysTheStackOnTheFirstFrame() {
        var relay = new ImageRelayService(NullLogger<ImageRelayService>.Instance);
        var svc = new LiveStackingService(relay, NullLogger<LiveStackingService>.Instance);
        svc.Start();

        var frame = MakeFrame();
        await svc.AddFrameAsync(frame);

        Assert.That(relay.LatestImageData, Is.Not.Null,
            "nothing reached the client, the LIVE canvas would sit empty");
        Assert.That(relay.LatestImageData, Is.Not.SameAs(frame),
            "what goes out is the accumulator, not the raw sub");
        Assert.That(svc.FrameCount, Is.EqualTo(1));

        // Frames 2+ cannot be asserted here: these synthetic frames carry no
        // stars, so alignment rejects them and no new stack is broadcast. That
        // is the fixture's limit, not the behaviour under test.
    }

}