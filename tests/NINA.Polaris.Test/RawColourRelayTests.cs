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
using System.Threading.Tasks;
using K4os.Compression.LZ4;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The colour live stack travels to the browser as three 16-bit planes rather
/// than an already-stretched JPEG, so the client can stretch it and measure a
/// truthful histogram from the same numbers it renders.
///
/// The wire format has one genuinely dangerous property, and most of these
/// tests exist for it: width and height describe ONE plane, so a 3-plane buffer
/// sent down the single-plane path is not a crash, it is a silent greyscale
/// picture of the red channel. The channel count on the header and the guard on
/// the mono path are what make that impossible.
/// </summary>
[TestFixture]
public class RawColourRelayTests {

    private static BaseImageData MakeRgb(int w, int h,
                                         ushort r = 50000, ushort g = 20000, ushort b = 3000) {
        var props = new ImageProperties {
            Width = w, Height = h, BitDepth = 16, Channels = 3,
            IsBayered = false, BayerPattern = BayerPatternEnum.None,
        };
        int n = w * h;
        var buf = new ushort[n * 3];
        for (int i = 0; i < n; i++) { buf[i] = r; buf[n + i] = g; buf[2 * n + i] = b; }
        return new BaseImageData(buf, props);
    }

    private static ImageRelayService MakeRelay()
        => new ImageRelayService(NullLogger<ImageRelayService>.Instance);

    [Test]
    public void TheHeaderCarriesTheChannelCount() {
        var rgb = MakeRgb(8, 6);
        var buffer = ImageBuffer.FromImageData(rgb);
        Assert.That(buffer.Channels, Is.EqualTo(3));

        var header = buffer.GetStreamHeader(kind: 6);
        Assert.That(header.Length, Is.EqualTo(32), "eight int32 fields");
        Assert.That(BitConverter.ToInt32(header, 0), Is.EqualTo(8), "width is ONE plane");
        Assert.That(BitConverter.ToInt32(header, 4), Is.EqualTo(6), "height is ONE plane");
        Assert.That(BitConverter.ToInt32(header, 16), Is.EqualTo(8 * 6 * 3 * 2),
            "uncompressed byte count covers all three planes");
        Assert.That(BitConverter.ToInt32(header, 20), Is.EqualTo(6), "frame kind");
        Assert.That(BitConverter.ToInt32(header, 28), Is.EqualTo(3), "channel count");
    }

    /// <summary>A client that predates the channel field reads the first seven
    /// fields at the same offsets, which is the whole point of the length
    /// prefix.</summary>
    [Test]
    public void TheOlderFieldsDidNotMove() {
        var mono = new ImageBuffer(new ushort[8 * 6], 8, 6, 16, BayerPatternEnum.RGGB);
        var header = mono.GetStreamHeader(kind: 1, calibration: 1);
        Assert.That(BitConverter.ToInt32(header, 8), Is.EqualTo(16), "bit depth");
        Assert.That(BitConverter.ToInt32(header, 12), Is.EqualTo((int)BayerPatternEnum.RGGB));
        Assert.That(BitConverter.ToInt32(header, 16), Is.EqualTo(8 * 6 * 2));
        Assert.That(BitConverter.ToInt32(header, 20), Is.EqualTo(1));
        Assert.That(BitConverter.ToInt32(header, 24), Is.EqualTo(1));
        Assert.That(BitConverter.ToInt32(header, 28), Is.EqualTo(1), "a mono buffer says one");
    }

    [Test]
    public void ThePayloadSurvivesTheRoundTrip() {
        var rgb = MakeRgb(16, 12);
        var buffer = ImageBuffer.FromImageData(rgb);
        var compressed = buffer.RentLz4Compressed(out int len);
        try {
            int n = 16 * 12;
            var bytes = new byte[n * 3 * 2];
            int wrote = LZ4Codec.Decode(compressed.AsSpan(0, len), bytes.AsSpan());
            Assert.That(wrote, Is.EqualTo(bytes.Length), "decoded size");
            var back = new ushort[n * 3];
            Buffer.BlockCopy(bytes, 0, back, 0, bytes.Length);
            Assert.That(back[0], Is.EqualTo(50000), "R plane");
            Assert.That(back[n], Is.EqualTo(20000), "G plane");
            Assert.That(back[2 * n], Is.EqualTo(3000), "B plane");
        } finally {
            System.Buffers.ArrayPool<byte>.Shared.Return(compressed);
        }
    }

    /// <summary>16-bit RGB is about ten times the bytes of the JPEG it replaced,
    /// so the cap is load-bearing, not a nicety: a full-frame colour stack is
    /// 70 MB on the wire.</summary>
    [Test]
    public async Task TheFrameIsDownsampledToTheCap() {
        var relay = MakeRelay();
        await relay.RelayRgbRawAsync(MakeRgb(800, 600), maxDim: 200);

        var sent = relay.GetLatestImage();
        Assert.That(sent, Is.Not.Null);
        Assert.That(Math.Max(sent!.Width, sent.Height), Is.LessThanOrEqualTo(200 * 2),
            "a box average lands at or just above the cap, never far past it");
        Assert.That(sent.Channels, Is.EqualTo(3), "still three planes after the reduction");
        Assert.That(sent.PixelData.Length, Is.EqualTo(sent.Width * sent.Height * 3));

        // The channels must not be mixed by the reduction.
        int n = sent.Width * sent.Height;
        var px = sent.PixelData.Span;
        Assert.That(px[0], Is.EqualTo(50000).Within(2), "R");
        Assert.That(px[n], Is.EqualTo(20000).Within(2), "G");
        Assert.That(px[2 * n], Is.EqualTo(3000).Within(2), "B");
    }

    [Test]
    public async Task ASmallFrameIsSentUntouched() {
        var relay = MakeRelay();
        await relay.RelayRgbRawAsync(MakeRgb(64, 48), maxDim: 1536);

        var sent = relay.GetLatestImage();
        Assert.That(sent!.Width, Is.EqualTo(64));
        Assert.That(sent.Height, Is.EqualTo(48));
    }

    /// <summary>The trap this whole change exists to close: before the guard, a
    /// 3-plane buffer went through the mono path without complaint and the
    /// browser rendered the red plane as a greyscale picture. No error, wrong
    /// picture.</summary>
    [Test]
    public void TheMonoPathRefusesAColourBuffer() {
        var relay = MakeRelay();
        Assert.That(() => relay.RelayImageAsync(MakeRgb(8, 6), FrameKind.LiveStack),
            Throws.ArgumentException.With.Message.Contains("RelayRgbRawAsync"));
    }

    [Test]
    public void TheColourPathRefusesASinglePlaneBuffer() {
        var relay = MakeRelay();
        var mono = new BaseImageData(new ushort[8 * 6],
            new ImageProperties { Width = 8, Height = 6, BitDepth = 16, Channels = 1 });
        Assert.That(() => relay.RelayRgbRawAsync(mono),
            Throws.ArgumentException);
    }

    /// <summary>The full-resolution stack is what the preview endpoint, annotate
    /// and plate solve want; only the wire copy is reduced.</summary>
    [Test]
    public async Task ThePreviewKeepsTheFullResolutionStack() {
        var relay = MakeRelay();
        await relay.RelayRgbRawAsync(MakeRgb(400, 300), maxDim: 100, kind: FrameKind.LiveStack);

        var jpeg = relay.GetStackJpeg(quality: 60);
        Assert.That(jpeg, Is.Not.Null.And.Not.Empty,
            "the stack preview still renders after the switch to raw");
    }
}
