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

using NUnit.Framework;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins <see cref="FitsStripReader"/>, the strip-at-a-time reader that
/// lets STUDIO master-frame integration stream calibration frames in
/// horizontal tiles instead of loading every frame whole (the change
/// that keeps a deep OSC stack off the OOM killer on a 4 GB Pi).
///
/// The contract that matters: reading a file strip-by-strip must
/// reconstruct EXACTLY the same pixel buffer as the full-frame
/// <see cref="FITSReader.Read(System.IO.Stream)"/>. Because the tiled
/// master integrator feeds those reconstructed pixels through the same
/// (separately tested) IntegrationMath, byte-parity here is what proves
/// the streamed master is identical to the old full-load master.
/// </summary>
[TestFixture]
public class FitsStripReaderTests {

    // --- 16-bit (the camera-native master format) via FITSWriter -----

    [Test]
    public void StripRead_16Bit_ReconstructsFullFrameExactly() {
        const int w = 64, h = 50;   // height deliberately not a tile multiple
        var pixels = SyntheticPixels(w, h, seed: 7);
        var path = WriteFitsViaWriter(w, h, pixels, gain: 222, filter: "Ha", exposure: 12.5);
        try {
            ushort[] full;
            using (var fs = System.IO.File.OpenRead(path)) full = FITSReader.Read(fs).Data;

            using var strip = FitsStripReader.Open(path);
            Assert.That(strip.IsStripable, Is.True);
            Assert.That(strip.IsFloat, Is.False);
            Assert.That(strip.Width, Is.EqualTo(w));
            Assert.That(strip.Height, Is.EqualTo(h));
            Assert.That(strip.MetaData.Camera.Gain, Is.EqualTo(222));
            Assert.That(strip.MetaData.Exposure.Filter, Is.EqualTo("Ha"));

            var reassembled = ReadAllInStrips(strip, tileRows: 7);
            Assert.That(reassembled, Is.EqualTo(full),
                "strip-by-strip read must reconstruct the full frame byte-for-byte");
        } finally {
            System.IO.File.Delete(path);
        }
    }

    [Test]
    public void StripRead_SingleMiddleStrip_MatchesThatRegionOfFull() {
        const int w = 40, h = 40;
        var pixels = SyntheticPixels(w, h, seed: 13);
        var path = WriteFitsViaWriter(w, h, pixels, gain: 0, filter: "", exposure: 1.0);
        try {
            ushort[] full;
            using (var fs = System.IO.File.OpenRead(path)) full = FITSReader.Read(fs).Data;

            using var strip = FitsStripReader.Open(path);
            const int startRow = 17, rows = 9;
            var buf = new ushort[rows * w];
            strip.ReadRows(startRow, rows, buf);

            for (int r = 0; r < rows; r++)
                for (int x = 0; x < w; x++)
                    Assert.That(buf[r * w + x], Is.EqualTo(full[(startRow + r) * w + x]),
                        $"mismatch at row {startRow + r}, col {x}");
        } finally {
            System.IO.File.Delete(path);
        }
    }

    [Test]
    public void ReadRows_OutOfRange_Throws() {
        const int w = 16, h = 16;
        var path = WriteFitsViaWriter(w, h, SyntheticPixels(w, h, 1), 0, "", 1.0);
        try {
            using var strip = FitsStripReader.Open(path);
            var buf = new ushort[w];
            Assert.Throws<System.ArgumentOutOfRangeException>(() => strip.ReadRows(h, 1, buf));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => strip.ReadRows(h - 1, 2, buf));
            Assert.Throws<System.ArgumentException>(() => strip.ReadRows(0, 2, new ushort[w])); // dest too small
        } finally {
            System.IO.File.Delete(path);
        }
    }

    // --- 8-bit + 32-bit decode branches (hand-built FITS) ------------

    [Test]
    public void StripRead_8Bit_MatchesFullReaderDecode() {
        const int w = 20, h = 24;
        var raw = new byte[w * h];
        for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(i % 256);
        var fits = BuildIntFits(w, h, bitpix: 8, raw);

        ushort[] full = FITSReader.Read(new System.IO.MemoryStream(fits)).Data;
        using var strip = FitsStripReader.Open(new System.IO.MemoryStream(fits));
        Assert.That(strip.IsStripable, Is.True);
        var reassembled = ReadAllInStrips(strip, tileRows: 5);
        Assert.That(reassembled, Is.EqualTo(full));
    }

    [Test]
    public void StripRead_32Bit_MatchesFullReaderDecode() {
        const int w = 12, h = 18;
        var raw = new byte[w * h * 4];
        // big-endian 32-bit ints ramping up
        for (int i = 0; i < w * h; i++) {
            int v = i * 37;
            raw[i * 4 + 0] = (byte)(v >> 24);
            raw[i * 4 + 1] = (byte)(v >> 16);
            raw[i * 4 + 2] = (byte)(v >> 8);
            raw[i * 4 + 3] = (byte)v;
        }
        var fits = BuildIntFits(w, h, bitpix: 32, raw);

        ushort[] full = FITSReader.Read(new System.IO.MemoryStream(fits)).Data;
        using var strip = FitsStripReader.Open(new System.IO.MemoryStream(fits));
        var reassembled = ReadAllInStrips(strip, tileRows: 4);
        Assert.That(reassembled, Is.EqualTo(full));
    }

    [Test]
    public void Float_IsNotStripable() {
        // BITPIX=-32 needs the global rescale, so the strip reader must
        // report it as non-stripable and refuse ReadRows.
        var fits = BuildFloatFits(4, 4);
        using var strip = FitsStripReader.Open(new System.IO.MemoryStream(fits));
        Assert.That(strip.IsFloat, Is.True);
        Assert.That(strip.IsStripable, Is.False);
        Assert.Throws<System.NotSupportedException>(() => strip.ReadRows(0, 1, new ushort[4]));
    }

    // --- helpers ------------------------------------------------------

    private static ushort[] SyntheticPixels(int w, int h, int seed) {
        var rng = new System.Random(seed);
        var p = new ushort[w * h];
        for (int i = 0; i < p.Length; i++) p[i] = (ushort)rng.Next(0, 65536);
        return p;
    }

    private static ushort[] ReadAllInStrips(FitsStripReader strip, int tileRows) {
        var outBuf = new ushort[strip.Width * strip.Height];
        var buf = new ushort[tileRows * strip.Width];
        for (int start = 0; start < strip.Height; start += tileRows) {
            int rows = System.Math.Min(tileRows, strip.Height - start);
            strip.ReadRows(start, rows, buf);
            System.Array.Copy(buf, 0, outBuf, start * strip.Width, rows * strip.Width);
        }
        return outBuf;
    }

    private static string WriteFitsViaWriter(int w, int h, ushort[] pixels,
            int gain, string filter, double exposure) {
        var props = new ImageProperties { Width = w, Height = h, BitDepth = 16 };
        var meta = new ImageMetaData();
        meta.Camera.Gain = gain;
        meta.Exposure.Filter = filter;
        meta.Exposure.ExposureTime = exposure;
        meta.Exposure.ImageType = "DARK";
        var img = new BaseImageData(pixels, props, meta);
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"polaris_strip_{System.Guid.NewGuid():N}.fits");
        FITSWriter.Write(img, path);
        return path;
    }

    private static byte[] BuildIntFits(int width, int height, int bitpix, byte[] rawBigEndian) {
        var cards = new System.Collections.Generic.List<string> {
            Card("SIMPLE", "T"),
            Card("BITPIX", bitpix.ToString()),
            Card("NAXIS", "2"),
            Card("NAXIS1", width.ToString()),
            Card("NAXIS2", height.ToString()),
            "END" + new string(' ', 77),
        };
        var header = new byte[2880];
        for (int i = 0; i < cards.Count; i++) {
            var bytes = System.Text.Encoding.ASCII.GetBytes(cards[i]);
            System.Array.Copy(bytes, 0, header, i * 80, System.Math.Min(80, bytes.Length));
        }
        for (int i = cards.Count * 80; i < 2880; i++) header[i] = (byte)' ';

        int padded = ((rawBigEndian.Length + 2879) / 2880) * 2880;
        var data = new byte[padded];
        System.Array.Copy(rawBigEndian, data, rawBigEndian.Length);

        var combined = new byte[header.Length + data.Length];
        System.Buffer.BlockCopy(header, 0, combined, 0, header.Length);
        System.Buffer.BlockCopy(data, 0, combined, header.Length, data.Length);
        return combined;
    }

    private static byte[] BuildFloatFits(int width, int height) {
        var cards = new System.Collections.Generic.List<string> {
            Card("SIMPLE", "T"),
            Card("BITPIX", "-32"),
            Card("NAXIS", "2"),
            Card("NAXIS1", width.ToString()),
            Card("NAXIS2", height.ToString()),
            "END" + new string(' ', 77),
        };
        var header = new byte[2880];
        for (int i = 0; i < cards.Count; i++) {
            var bytes = System.Text.Encoding.ASCII.GetBytes(cards[i]);
            System.Array.Copy(bytes, 0, header, i * 80, System.Math.Min(80, bytes.Length));
        }
        for (int i = cards.Count * 80; i < 2880; i++) header[i] = (byte)' ';

        int dataBytes = width * height * 4;
        int padded = ((dataBytes + 2879) / 2880) * 2880;
        var data = new byte[padded];
        for (int i = 0; i < width * height; i++) {
            var le = System.BitConverter.GetBytes((float)(i % 7) / 7f);
            data[i * 4 + 0] = le[3];
            data[i * 4 + 1] = le[2];
            data[i * 4 + 2] = le[1];
            data[i * 4 + 3] = le[0];
        }

        var combined = new byte[header.Length + data.Length];
        System.Buffer.BlockCopy(header, 0, combined, 0, header.Length);
        System.Buffer.BlockCopy(data, 0, combined, header.Length, data.Length);
        return combined;
    }

    private static string Card(string keyword, string value)
        => (keyword.PadRight(8) + "= " + value.PadLeft(20)).PadRight(80);
}
