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
using NINA.Image.ImageData;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// What GET /api/livestack/preview is allowed to answer with.
///
/// Field report, 2026-08-08: after changing target and restarting the stack,
/// "the first restack showed and then the old image came back". The endpoint
/// served the relay's LAST FRAME OF ANY KIND, and a reset did not clear it, so
/// a pull landing in the gap between the reset and the first new frame
/// repainted the canvas with the previous target's stack.
/// </summary>
[TestFixture]
public class LiveStackPreviewSourceTests {

    private static ImageRelayService Relay() =>
        new(NullLogger<ImageRelayService>.Instance);

    private static BaseImageData Frame(int w = 32, int h = 32, ushort fill = 1000) {
        var px = new ushort[w * h];
        Array.Fill(px, fill);
        return new BaseImageData(px, new ImageProperties {
            Width = w, Height = h, BitDepth = 16
        });
    }

    [Test]
    public async Task WithNoStackYetThePreviewHasNothingToServe() {
        var relay = Relay();

        // A preview snap went past. It is not the stack.
        await relay.RelayImageAsync(Frame(), FrameKind.Preview);

        Assert.That(relay.GetStackJpeg(), Is.Null,
            "a frame of another kind must not be served as the stack: that is "
            + "how the previous target's picture reached the LIVE canvas");
    }

    [Test]
    public async Task AStackFrameIsServed() {
        var relay = Relay();

        await relay.RelayImageAsync(Frame(), FrameKind.LiveStack);

        Assert.That(relay.GetStackJpeg(), Is.Not.Null.And.Length.GreaterThan(0));
    }

    /// <summary>The reported sequence, in order.</summary>
    [Test]
    public async Task AfterAResetTheOldStackIsNotServedAgain() {
        var relay = Relay();
        await relay.RelayImageAsync(Frame(fill: 1000), FrameKind.LiveStack);
        Assert.That(relay.GetStackJpeg(), Is.Not.Null, "precondition: a stack exists");

        // Target changed, stacker reset. LiveStackingService.Reset calls this.
        relay.ClearStack();

        Assert.That(relay.GetStackJpeg(), Is.Null,
            "between the reset and the first frame of the new stack the honest "
            + "answer is 404, not the stack that was just discarded");
    }

    /// <summary>A frame of some other kind arriving during that gap must not
    /// resurrect the preview either: it is not a stack, whatever its
    /// timing.</summary>
    [Test]
    public async Task AFrameOfAnotherKindDuringTheGapDoesNotBecomeTheStack() {
        var relay = Relay();
        await relay.RelayImageAsync(Frame(), FrameKind.LiveStack);
        relay.ClearStack();

        await relay.RelayImageAsync(Frame(), FrameKind.Autorun);

        Assert.That(relay.GetStackJpeg(), Is.Null);
    }

    [Test]
    public async Task TheNewStackReplacesTheOldOne() {
        var relay = Relay();
        await relay.RelayImageAsync(Frame(w: 32, h: 32), FrameKind.LiveStack);
        relay.ClearStack();

        // Different size, so the served bytes cannot be the earlier stack's.
        await relay.RelayImageAsync(Frame(w: 64, h: 64), FrameKind.LiveStack);

        Assert.That(relay.GetStackJpeg(), Is.Not.Null.And.Length.GreaterThan(0));
    }
}
