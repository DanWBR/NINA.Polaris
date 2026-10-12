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

using System.IO.Compression;
using NUnit.Framework;
using NINA.Core.Enum;
using NINA.INDI.Devices;
using SkiaSharp;

namespace NINA.Polaris.Test;

/// <summary>
/// INDI video stream frames as the driver's StreamManager sends them
/// (discussion #37: every one used to be dropped as an unreadable RAW, so the
/// stream fell back to slow single exposures).
/// </summary>
[TestFixture]
public class IndiStreamFrameDecoderTests {
    [TestCase(".stream", true)]
    [TestCase(".stream.z", true)]
    [TestCase(".stream_jpg", true)]
    [TestCase(".fits", false)]
    [TestCase(".cr2", false)]
    public void RecognisesStreamFormats(string format, bool expected)
        => Assert.That(IndiStreamFrameDecoder.IsStreamFormat(format), Is.EqualTo(expected));

    [Test]
    public void Mono8_IsSpreadOver16Bits_AndKeepsItsPrecision() {
        var data = new byte[] { 0, 1, 128, 255, 10, 20 };
        var img = IndiStreamFrameDecoder.Decode(data, ".stream", 3, 2)!;
        Assert.Multiple(() => {
            Assert.That(img.Properties.Width, Is.EqualTo(3));
            Assert.That(img.Properties.Height, Is.EqualTo(2));
            Assert.That(img.Properties.SignificantBitDepth, Is.EqualTo(8));
            Assert.That(img.Properties.IsBayered, Is.False, "mono or Bayer: the driver's CFA decides later");
            Assert.That(img.Data, Is.EqualTo(new ushort[] { 0, 257, 128 * 257, 65535, 2570, 5140 }));
        });
    }

    [Test]
    public void Mono16_IsReadLittleEndian() {
        var px = new ushort[] { 1, 4095, 65535, 300 };
        var data = new byte[px.Length * 2];
        Buffer.BlockCopy(px, 0, data, 0, data.Length);
        var img = IndiStreamFrameDecoder.Decode(data, ".stream", 2, 2)!;
        Assert.That(img.Data, Is.EqualTo(px));
        Assert.That(img.Properties.SignificantBitDepth, Is.EqualTo(0), "real depth comes from the driver");
    }

    [Test]
    public void Rgb24_IsReMosaicedToRggb() {
        // 2x2 frame, each pixel (R,G,B) distinct so the picked channel shows.
        var data = new byte[] {
            10, 20, 30,   40, 50, 60,
            70, 80, 90,   100, 110, 120
        };
        var img = IndiStreamFrameDecoder.Decode(data, ".stream", 2, 2)!;
        Assert.Multiple(() => {
            Assert.That(img.Properties.IsBayered, Is.True);
            Assert.That(img.Properties.BayerPattern, Is.EqualTo(BayerPatternEnum.RGGB));
            // R at (0,0), G at (1,0) and (0,1), B at (1,1)
            Assert.That(img.Data, Is.EqualTo(new ushort[] { 10 * 257, 50 * 257, 80 * 257, 120 * 257 }));
        });
    }

    [Test]
    public void CompressedFrames_AreInflated() {
        var raw = new byte[64 * 48];
        for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(i % 251);
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        var img = IndiStreamFrameDecoder.Decode(ms.ToArray(), ".stream.z", 64, 48)!;
        Assert.That(img.Data[100], Is.EqualTo((ushort)(100 % 251 * 257)));
    }

    [Test]
    public void ABufferThatDoesNotFitTheFrame_IsRejected() {
        Assert.Multiple(() => {
            Assert.That(IndiStreamFrameDecoder.Decode(new byte[10], ".stream", 3, 2), Is.Null);
            Assert.That(IndiStreamFrameDecoder.Decode(new byte[6 * 4], ".stream", 3, 2), Is.Null,
                "4 bytes per pixel is no INDI stream layout");
            Assert.That(IndiStreamFrameDecoder.Decode(new byte[6], ".stream", 0, 0), Is.Null,
                "no frame size known");
            Assert.That(IndiStreamFrameDecoder.Decode(new byte[] { 1, 2, 3 }, ".stream.z", 1, 3), Is.Null,
                "not zlib");
        });
    }

    [Test]
    public void JpegFrames_DecodeWithoutAFrameSize() {
        using var bmp = new SKBitmap(8, 6);
        bmp.Erase(new SKColor(200, 100, 50));
        using var jpg = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Jpeg, 95);
        var img = IndiStreamFrameDecoder.Decode(jpg.ToArray(), ".stream_jpg", 0, 0)!;
        Assert.Multiple(() => {
            Assert.That(img.Properties.Width, Is.EqualTo(8));
            Assert.That(img.Properties.Height, Is.EqualTo(6));
            Assert.That(img.Properties.IsBayered, Is.True);
        });
    }
}
