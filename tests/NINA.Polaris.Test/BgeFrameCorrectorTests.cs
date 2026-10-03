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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.Polaris.Services.Bge;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Per-frame background correction for live stacking: the cadence, the honesty
/// of the reuse, and what happens when the model has nowhere to run.
///
/// The thing being replaced wrote the whole frame to a temporary FITS, drove a
/// GraXpert subprocess and read another FITS back, on every sub, inside a method
/// the capture loop awaits. The replacement subtracts on every frame and infers
/// once every N, and never waits for the inference.
///
/// The producer here is a fake whose completion the test controls, so the
/// "never waits" property is observable: the first frames come back uncorrected
/// while a request is outstanding, and the correction appears only after the
/// test lets the model finish.
/// </summary>
[TestFixture]
public class BgeFrameCorrectorTests {

    private const int W = 48;
    private const int H = 32;

    /// <summary>A producer the test drives by hand. Nothing completes until
    /// <see cref="Complete"/> is called, which is how the asynchronous
    /// behaviour becomes deterministic.</summary>
    private sealed class ManualProducer : IBgeModelProducer {
        private TaskCompletionSource<float[]?>? _pending;
        // The corrector starts a request on a thread pool thread, deliberately:
        // a host producer runs a blocking P/Invoke and must not do it on the
        // frame's thread. So the test has to wait until the request has actually
        // been made before it can complete it, rather than assume the ordering.
        private readonly ManualResetEventSlim _asked = new(false);
        public string Name => "fake";
        public bool CanRun { get; set; } = true;
        public int Requests { get; private set; }
        public int LastTile { get; private set; }

        public Task<float[]?> RunAsync(float[] nhwcTensor, int tile, CancellationToken ct) {
            LastTile = tile;
            _pending = new TaskCompletionSource<float[]?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Requests++;
            _asked.Set();
            return _pending.Task;
        }

        /// <summary>Block until the request count reaches <paramref name="n"/>.
        /// The count is incremented on the thread pool, so asserting on it
        /// straight after a frame is a race, not a measurement.</summary>
        public void WaitUntilRequests(int n) {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (Requests < n && DateTime.UtcNow < until) Thread.Sleep(5);
            Assert.That(Requests, Is.EqualTo(n), "the corrector did not ask for a model");
        }

        /// <summary>Block until the corrector has asked for a model, so a test
        /// can complete the request it just triggered.</summary>
        public void WaitForRequest() {
            Assert.That(_asked.Wait(TimeSpan.FromSeconds(5)), Is.True,
                "the corrector never asked the producer for a model");
            _asked.Reset();
        }

        /// <summary>Hand back a flat background at the given normalised level.</summary>
        public void Complete(int tile, float level) {
            WaitForRequest();
            var outp = new float[tile * tile * 3];
            for (int i = 0; i < outp.Length; i++) outp[i] = level;
            _pending!.SetResult(outp);
            _pending = null;
        }

        public void CompleteWithNothing() { WaitForRequest(); _pending!.SetResult(null); _pending = null; }
        public void Fail() { WaitForRequest(); _pending!.SetException(new InvalidOperationException("no")); _pending = null; }
        public bool HasPending => _pending != null;
    }

    private static ushort[] Flat(int w, int h, int channels, double level) {
        var px = new ushort[w * h * channels];
        var v = (ushort)Math.Clamp(level * 65535.0, 0, 65535);
        for (int i = 0; i < px.Length; i++) px[i] = v;
        return px;
    }

    /// <summary>A sky gradient, so a correction has something to remove.</summary>
    private static ushort[] Gradient(int w, int h, int channels, double offset = 0) {
        var px = new ushort[w * h * channels];
        for (int c = 0; c < channels; c++)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) {
                    double v = 0.20 + offset + 0.20 * x / w + 0.08 * y / h;
                    px[c * w * h + y * w + x] = (ushort)Math.Clamp(v * 65535.0, 0, 65535);
                }
        return px;
    }

    private static double MedianOf(ushort[] px) {
        var copy = px.ToArray();
        Array.Sort(copy);
        return copy[copy.Length / 2] / 65535.0;
    }

    /// <summary>Let the detached request settle. The producer completes on a
    /// thread pool thread, so the corrector only sees it on a later frame,
    /// which is exactly the production behaviour.
    ///
    /// Waits on the request rather than sleeping: a fixed sleep passed on an
    /// idle machine and failed inside the full suite, where the thread pool is
    /// busy and the adoption had not happened yet.</summary>
    private static void Settle(BgeFrameCorrector c) =>
        c.WaitForModelForTest(TimeSpan.FromSeconds(10));

    [Test]
    public void BeforeAnyModelArrives_FramesAreStackedUncorrectedAndCounted() {
        var p = new ManualProducer();
        var c = new BgeFrameCorrector(p) { RecomputeEveryFrames = 10 };
        var frame = Gradient(W, H, 1);

        var r1 = c.Apply(frame, W, H, 1, 0, null);
        var r2 = c.Apply(frame, W, H, 1, 1, null);
        p.WaitUntilRequests(1);   // one in flight is enough; frame 2 must not queue another

        Assert.Multiple(() => {
            Assert.That(r1.Corrected, Is.False, "nothing has come back yet");
            Assert.That(r1.Pixels, Is.SameAs(frame), "the sub is passed through untouched");
            Assert.That(r2.Corrected, Is.False);
            Assert.That(c.FramesUncorrected, Is.EqualTo(2), "counted, not hidden");
        });
    }

    [Test]
    public void OnceTheModelArrives_EveryFrameIsCorrected() {
        var p = new ManualProducer();
        var c = new BgeFrameCorrector(p) { RecomputeEveryFrames = 10, Tile = 32 };
        var frame = Gradient(W, H, 1);

        c.Apply(frame, W, H, 1, 0, null);          // starts the request
        p.Complete(32, 0.0f);                       // a flat, zero background
        Settle(c);

        var r = c.Apply(frame, W, H, 1, 1, null);

        Assert.Multiple(() => {
            Assert.That(r.Corrected, Is.True);
            Assert.That(r.Producer, Is.EqualTo("fake"));
            Assert.That(r.Pixels, Is.Not.SameAs(frame), "a corrected frame is a new array");
            Assert.That(c.FramesUncorrected, Is.EqualTo(1), "only the first frame missed");
        });
    }

    /// <summary>The cadence: the model is asked for again only after N frames,
    /// while the subtraction happens on every one.</summary>
    [Test]
    public void TheModelIsRecomputedEveryNFrames_ButEveryFrameIsCorrected() {
        var p = new ManualProducer();
        var c = new BgeFrameCorrector(p) { RecomputeEveryFrames = 5, Tile = 32 };
        var frame = Gradient(W, H, 1);

        c.Apply(frame, W, H, 1, 0, null);
        p.Complete(32, 0.0f);
        Settle(c);

        int corrected = 0;
        for (long i = 1; i <= 4; i++)
            if (c.Apply(frame, W, H, 1, i, null).Corrected) corrected++;

        Assert.Multiple(() => {
            Assert.That(corrected, Is.EqualTo(4), "every sub in the window gets corrected");
            Assert.That(p.Requests, Is.EqualTo(1), "and none of them asks for a new model");
        });

        // Frame 5 is five frames past the model, so a new request goes out.
        var r5 = c.Apply(frame, W, H, 1, 5, null);
        p.WaitUntilRequests(2);
        Assert.Multiple(() => {
            Assert.That(r5.Corrected, Is.True, "and the old model still serves this frame");
            Assert.That(r5.BackgroundAgeFrames, Is.EqualTo(5), "the age is what the operator sees");
        });
    }

    /// <summary>The point of holding the model in normalised space. With a held
    /// background and a brighter sky, the corrected frame has to follow the new
    /// sky level, not the level the model was computed at.</summary>
    [Test]
    public void AReusedModel_FollowsTheCurrentFramesSkyLevel() {
        var p = new ManualProducer();
        var c = new BgeFrameCorrector(p) { RecomputeEveryFrames = 100, Tile = 32 };

        var dark = Gradient(W, H, 1);
        c.Apply(dark, W, H, 1, 0, null);
        p.Complete(32, 0.0f);
        Settle(c);

        var onDark = c.Apply(dark, W, H, 1, 1, null);
        // The moon comes up: the same gradient on a sky two tenths brighter.
        var bright = Gradient(W, H, 1, offset: 0.2);
        var onBright = c.Apply(bright, W, H, 1, 2, null);

        Assert.Multiple(() => {
            Assert.That(onDark.Corrected && onBright.Corrected, Is.True);
            Assert.That(p.Requests, Is.EqualTo(1), "no new model was computed in between");
            Assert.That(MedianOf(onBright.Pixels) - MedianOf(onDark.Pixels),
                Is.GreaterThan(0.15),
                "the correction is recentred on the current frame, so a brighter sky "
                + "stays brighter instead of being pulled back to the old median");
        });
    }

    [Test]
    public void AGeometryChange_DiscardsTheModelAndAsksAgain() {
        var p = new ManualProducer();
        var c = new BgeFrameCorrector(p) { RecomputeEveryFrames = 100, Tile = 32 };

        c.Apply(Gradient(W, H, 1), W, H, 1, 0, null);
        p.Complete(32, 0.0f);
        Settle(c);
        Assert.That(c.Apply(Gradient(W, H, 1), W, H, 1, 1, null).Corrected, Is.True);

        // Binning changed under us: half the frame size.
        var small = Gradient(W / 2, H / 2, 1);
        var r = c.Apply(small, W / 2, H / 2, 1, 2, null);
        p.WaitUntilRequests(2);

        Assert.That(r.Corrected, Is.False, "a model for another geometry must not be used");
    }

    [Test]
    public void AFilterChange_DiscardsTheModel() {
        var p = new ManualProducer();
        var c = new BgeFrameCorrector(p) { RecomputeEveryFrames = 100, Tile = 32 };

        c.Apply(Gradient(W, H, 1), W, H, 1, 0, "Ha");
        p.Complete(32, 0.0f);
        Settle(c);
        Assert.That(c.Apply(Gradient(W, H, 1), W, H, 1, 1, "Ha").Corrected, Is.True);

        var r = c.Apply(Gradient(W, H, 1), W, H, 1, 2, "OIII");

        Assert.That(r.Corrected, Is.False, "a different filter is a different gradient");
    }

    /// <summary>A result that comes back after the operator changed binning is
    /// dropped rather than applied to frames it does not describe.</summary>
    [Test]
    public void AResultForAStaleGeometry_IsDropped() {
        var p = new ManualProducer();
        var c = new BgeFrameCorrector(p) { RecomputeEveryFrames = 100, Tile = 32 };

        c.Apply(Gradient(W, H, 1), W, H, 1, 0, null);   // request for WxH
        p.Complete(32, 0.0f);                            // answer arrives...
        Settle(c);

        // ...but by now the frames are half the size.
        var r = c.Apply(Gradient(W / 2, H / 2, 1), W / 2, H / 2, 1, 1, null);

        Assert.That(r.Corrected, Is.False);
        Assert.That(c.Current, Is.Null, "the stale answer was not adopted");
    }

    [Test]
    public void WhenTheProducerCannotRun_NothingIsRequestedAndFramesAreCounted() {
        var p = new ManualProducer { CanRun = false };
        var c = new BgeFrameCorrector(p) { RecomputeEveryFrames = 10 };

        for (long i = 0; i < 3; i++) c.Apply(Gradient(W, H, 1), W, H, 1, i, null);

        Assert.Multiple(() => {
            Assert.That(p.Requests, Is.Zero, "no browser connected and no accelerator is not an error");
            Assert.That(c.FramesUncorrected, Is.EqualTo(3));
        });
    }

    [Test]
    public void AProducerThatFails_LeavesTheSessionStacking() {
        var p = new ManualProducer();
        var c = new BgeFrameCorrector(p) { RecomputeEveryFrames = 10, Tile = 32 };

        c.Apply(Gradient(W, H, 1), W, H, 1, 0, null);
        p.Fail();
        Settle(c);
        var r = c.Apply(Gradient(W, H, 1), W, H, 1, 1, null);
        p.WaitUntilRequests(2);   // tries again rather than latching off

        Assert.Multiple(() => {
            Assert.That(r.Corrected, Is.False);
            Assert.That(c.Current, Is.Null);
        });
    }

    [Test]
    public void AProducerThatReturnsNothing_IsNotAdopted() {
        var p = new ManualProducer();
        var c = new BgeFrameCorrector(p) { RecomputeEveryFrames = 10, Tile = 32 };

        c.Apply(Gradient(W, H, 1), W, H, 1, 0, null);
        p.CompleteWithNothing();
        Settle(c);

        Assert.That(c.Apply(Gradient(W, H, 1), W, H, 1, 1, null).Corrected, Is.False);
        Assert.That(c.Current, Is.Null);
    }

    [Test]
    public void Reset_ForgetsTheModelAndTheCount() {
        var p = new ManualProducer();
        var c = new BgeFrameCorrector(p) { RecomputeEveryFrames = 100, Tile = 32 };
        c.Apply(Gradient(W, H, 1), W, H, 1, 0, null);
        p.Complete(32, 0.0f);
        Settle(c);
        c.Apply(Gradient(W, H, 1), W, H, 1, 1, null);

        c.Reset();

        Assert.Multiple(() => {
            Assert.That(c.Current, Is.Null);
            Assert.That(c.FramesUncorrected, Is.Zero);
        });
    }

    /// <summary>A flat frame with a flat background and Subtraction is the
    /// identity, which is the cheapest check that the reuse path applies the
    /// same arithmetic as a fresh run.</summary>
    [Test]
    public void AFlatBackgroundOnAFlatFrame_ChangesNothing() {
        var p = new ManualProducer();
        var c = new BgeFrameCorrector(p) { RecomputeEveryFrames = 100, Tile = 32 };
        var flat = Flat(W, H, 1, 0.30);

        c.Apply(flat, W, H, 1, 0, null);
        p.Complete(32, 0.0f);
        Settle(c);
        var r = c.Apply(flat, W, H, 1, 1, null);

        Assert.That(r.Corrected, Is.True);
        Assert.That(r.Pixels, Is.EqualTo(flat).Within(1),
            "subtracting a zero background and recentring on the median is a no-op");
    }
}
