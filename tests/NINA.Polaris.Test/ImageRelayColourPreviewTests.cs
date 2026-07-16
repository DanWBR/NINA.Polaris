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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// LIVECOL: the LIVE colour stack must not come back as B&W through
/// /api/livestack/preview.
///
/// The field bug (2026-07-16): RelayRgbJpegAsync rendered a correct RGB JPEG,
/// broadcast it (client shows COLOUR), then set _latestJpeg = null — believing
/// that would make the preview "re-encode from THIS colour stack". The re-encode
/// path is GetLatestJpeg -> ImageBuffer.ToJpeg -> JpegHelper.EncodeGrayscale,
/// which has no colour path at all. So the preview served greyscale and the
/// client painted it over the colour frame: "o frame colorido aparece rapidamente
/// e depois é substituido pelo em preto e branco".
///
/// It hid for months because both halves looked right in isolation — the WS path
/// was colour (LIVE-TRACE proved every frame took branch=COLOUR ch=3) and the
/// greyscale encoder is correct for the mono/raw path. Nothing compared them.
/// That's what this fixture does.
/// </summary>
[TestFixture]
public class ImageRelayColourPreviewTests {
    /// <summary>Minimal open WebSocket. RelayRgbJpegAsync early-returns when no
    /// client is connected, so the colour path can't be exercised without one.</summary>
    private sealed class FakeSocket : System.Net.WebSockets.WebSocket {
        public override System.Net.WebSockets.WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override System.Net.WebSockets.WebSocketState State => System.Net.WebSockets.WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(System.Net.WebSockets.WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<System.Net.WebSockets.WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken ct)
            => Task.FromResult(new System.Net.WebSockets.WebSocketReceiveResult(0, System.Net.WebSockets.WebSocketMessageType.Binary, true));
        public override Task SendAsync(ArraySegment<byte> b, System.Net.WebSockets.WebSocketMessageType t, bool eom, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>Number of colour components declared in the JPEG's SOF marker:
    /// 1 = greyscale, 3 = colour. Read from the bytes rather than trusting the
    /// call chain — the whole bug was a call chain that looked right.</summary>
    private static int JpegComponents(byte[] jpeg) {
        // SOI, then marker segments: FF <id> <len-hi> <len-lo> <payload...>
        int i = 2;
        while (i + 3 < jpeg.Length) {
            if (jpeg[i] != 0xFF) { i++; continue; }
            byte id = jpeg[i + 1];
            // SOF0/1/2/3, 9/10/11, 13/14/15 carry the frame header.
            // Exclude DHT(C4), JPG(C8), DAC(CC), which share the C0..CF range.
            bool isSof = id >= 0xC0 && id <= 0xCF && id != 0xC4 && id != 0xC8 && id != 0xCC;
            int len = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (isSof) return jpeg[i + 9];   // len(2) precision(1) height(2) width(2) -> components
            i += 2 + len;
        }
        return -1;
    }

    /// <summary>3-plane RGB frame, the shape the colour live-stacker relays.</summary>
    private static IImageData RgbFrame(int w = 8, int h = 8) {
        var px = new ushort[w * h * 3];
        for (int i = 0; i < w * h; i++) {
            px[i] = 40000;                 // R plane bright
            px[w * h + i] = 8000;          // G plane mid
            px[2 * w * h + i] = 1000;      // B plane dim  -> unmistakably not grey
        }
        var props = new ImageProperties {
            Width = w, Height = h, BitDepth = 16, Channels = 3,
            IsBayered = false, BayerPattern = BayerPatternEnum.None
        };
        return new BaseImageData(px, props, new ImageMetaData());
    }

    private static ImageRelayService NewRelayWithClient() {
        var relay = new ImageRelayService(NullLogger<ImageRelayService>.Instance);
        relay.RegisterClient("test", new FakeSocket());
        return relay;
    }

    /// <summary>THE regression. After a colour frame is relayed, the one-shot JPEG
    /// that /api/livestack/preview serves must be COLOUR. Before the fix this
    /// returned a 1-component (greyscale) JPEG — the B&W frame the user saw paint
    /// over the colour one.</summary>
    [Test]
    public async Task AfterColourRelay_PreviewJpegIsColour_NotGreyscale() {
        var relay = NewRelayWithClient();

        var relayed = await relay.RelayRgbJpegAsync(RgbFrame(), maxDim: 64, quality: 90,
                                                    kind: FrameKind.LiveStack);
        Assert.That(relayed, Is.True, "colour relay should have run (a client is connected)");

        var preview = relay.GetLatestJpeg(85);
        Assert.That(preview, Is.Not.Null, "preview must serve the stack that's on the canvas");
        Assert.That(JpegComponents(preview!), Is.EqualTo(3),
            "preview JPEG must be colour; 1 component = the B&W flip regression");
    }

    /// <summary>The preview must be the very JPEG that was broadcast, not a
    /// re-encode. Same bytes = the canvas and the preview cannot disagree, and the
    /// SBC skips a 2.7 s redundant encode of a 4144x2822 frame.</summary>
    [Test]
    public async Task AfterColourRelay_PreviewReusesTheBroadcastJpeg() {
        var relay = NewRelayWithClient();
        await relay.RelayRgbJpegAsync(RgbFrame(), maxDim: 64, quality: 90, kind: FrameKind.LiveStack);

        var a = relay.GetLatestJpeg(85);
        var b = relay.GetLatestJpeg(85);
        Assert.That(a, Is.Not.Null);
        Assert.That(b, Is.SameAs(a), "the cached colour JPEG should be served, not re-encoded per request");
    }

    /// <summary>The mono/raw path must STAY greyscale: RelayImageAsync nulls the
    /// cache on purpose, and ToJpeg's greyscale encoder is correct there. Guards
    /// against "fixing" the B&W bug by forcing colour everywhere.</summary>
    [Test]
    public async Task MonoRelay_PreviewStaysGreyscale() {
        var relay = NewRelayWithClient();
        var props = new ImageProperties {
            Width = 4, Height = 4, BitDepth = 16, Channels = 1,
            IsBayered = false, BayerPattern = BayerPatternEnum.None
        };
        var mono = new BaseImageData(new ushort[16], props, new ImageMetaData());

        await relay.RelayImageAsync(mono);

        var preview = relay.GetLatestJpeg(85);
        Assert.That(preview, Is.Not.Null);
        Assert.That(JpegComponents(preview!), Is.EqualTo(1),
            "a genuinely mono frame must not be forced into colour");
    }

    /// <summary>A colour frame followed by a mono frame must not keep serving the
    /// stale colour JPEG — RelayImageAsync's cache invalidation still has to work.
    /// This is the hazard the original `_latestJpeg = null` was defending against;
    /// it was right about the danger, wrong about which method needed the guard.</summary>
    [Test]
    public async Task ColourThenMono_PreviewFollowsTheLatestFrame() {
        var relay = NewRelayWithClient();
        await relay.RelayRgbJpegAsync(RgbFrame(), maxDim: 64, quality: 90, kind: FrameKind.LiveStack);
        Assert.That(JpegComponents(relay.GetLatestJpeg(85)!), Is.EqualTo(3));

        var props = new ImageProperties {
            Width = 4, Height = 4, BitDepth = 16, Channels = 1,
            IsBayered = false, BayerPattern = BayerPatternEnum.None
        };
        await relay.RelayImageAsync(new BaseImageData(new ushort[16], props, new ImageMetaData()));

        Assert.That(JpegComponents(relay.GetLatestJpeg(85)!), Is.EqualTo(1),
            "stale colour JPEG must not survive a later mono frame");
    }
}
