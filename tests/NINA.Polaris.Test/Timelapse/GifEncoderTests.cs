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

using System.Text;
using NUnit.Framework;
using NINA.Polaris.Services.Timelapse;

namespace NINA.Polaris.Test.Timelapse;

// Deliberately Skia-free: the encoder takes raw RGB and validation walks the GIF
// byte structure, so these run on any host (the app's SkiaSharp native asset is
// Linux-only and would crash a Windows test host).
[TestFixture]
public class GifEncoderTests {

    private static GifFrame Solid(int w, int h, byte r, byte g, byte b) {
        var a = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++) { a[i * 3] = r; a[i * 3 + 1] = g; a[i * 3 + 2] = b; }
        return new GifFrame(a, w, h);
    }

    private static GifFrame Gradient(int w, int h, int seed) {
        var a = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                int o = (y * w + x) * 3;
                a[o] = (byte)((x * 7 + seed) & 255);
                a[o + 1] = (byte)((y * 11 + seed * 2) & 255);
                a[o + 2] = (byte)((x * y + seed * 3) & 255);
            }
        return new GifFrame(a, w, h);
    }

    private static byte[] Encode(GifFrame[] frames, int fps = 10, bool loop = true) {
        using var ms = new MemoryStream();
        GifEncoder.Encode(ms, frames.Length, i => frames[i], fps, loop);
        return ms.ToArray();
    }

    private static bool Contains(byte[] hay, string needle) {
        var n = Encoding.ASCII.GetBytes(needle);
        for (int i = 0; i + n.Length <= hay.Length; i++) {
            bool ok = true;
            for (int j = 0; j < n.Length; j++) if (hay[i + j] != n[j]) { ok = false; break; }
            if (ok) return true;
        }
        return false;
    }

    // Walk the GIF89a block structure end to end. Throws on any malformed block
    // (bad sub-block framing, truncated LZW, missing trailer) and returns the
    // number of image frames — a structural decode without a native library.
    private static (int frames, int gctSize) Walk(byte[] g) {
        Assert.That(Encoding.ASCII.GetString(g, 0, 6), Is.EqualTo("GIF89a"), "signature");
        int p = 6;
        int packed = g[p + 4];
        int gctSize = (packed & 0x80) != 0 ? 1 << ((packed & 7) + 1) : 0;
        p += 7;                          // logical screen descriptor
        p += gctSize * 3;                // global colour table
        int frames = 0;
        while (true) {
            Assert.That(p, Is.LessThan(g.Length), "ran off the end before the trailer");
            byte b = g[p++];
            if (b == 0x3B) break;        // trailer
            if (b == 0x21) {             // extension: label + sub-blocks
                p++;                     // label
                p = SkipSubBlocks(g, p);
            } else if (b == 0x2C) {      // image descriptor
                p += 8;                  // left,top,w,h
                int ipacked = g[p++];
                if ((ipacked & 0x80) != 0) p += (1 << ((ipacked & 7) + 1)) * 3; // local table
                p++;                     // LZW min code size
                p = SkipSubBlocks(g, p);
                frames++;
            } else {
                Assert.Fail($"unexpected block byte 0x{b:X2} at {p - 1}");
            }
        }
        Assert.That(p, Is.EqualTo(g.Length), "exactly one trailer at the very end");
        return (frames, gctSize);
    }

    private static int SkipSubBlocks(byte[] g, int p) {
        while (true) {
            Assert.That(p, Is.LessThan(g.Length), "truncated sub-block");
            int len = g[p++];
            if (len == 0) return p;
            p += len;
        }
    }

    [Test]
    public void Encode_ColourFrames_ProducesWellFormedAnimatedGif() {
        var frames = new[] { Gradient(24, 18, 0), Gradient(24, 18, 40), Gradient(24, 18, 90) };
        var gif = Encode(frames);

        Assert.That(gif[^1], Is.EqualTo(0x3B), "trailer byte");
        Assert.That(Contains(gif, "NETSCAPE2.0"), Is.True, "loop extension present");
        // Logical screen size == frame 0.
        Assert.That(gif[6] | (gif[7] << 8), Is.EqualTo(24));
        Assert.That(gif[8] | (gif[9] << 8), Is.EqualTo(18));
        var (nFrames, gct) = Walk(gif);
        Assert.That(nFrames, Is.EqualTo(3));
        Assert.That(gct, Is.EqualTo(256), "full global colour table");

        // Save a real GIF for an independent (PIL) cross-check outside NUnit.
        var outDir = Path.Combine(Path.GetTempPath(), "polaris-gif-test");
        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, "colour.gif"), gif);
    }

    [Test]
    public void Encode_MonoFrames_UsesDirectGrayscalePalette() {
        var frames = new[] { Solid(20, 20, 30, 30, 30), Solid(20, 20, 120, 120, 120), Solid(20, 20, 210, 210, 210) };
        var gif = Encode(frames, fps: 5);
        var (nFrames, gct) = Walk(gif);
        Assert.That(nFrames, Is.EqualTo(3));
        Assert.That(gct, Is.EqualTo(256));
        // Grayscale path writes a 0..255 gray ramp as the global colour table
        // (starts at byte 13, three bytes per entry).
        int gc = 13;
        for (int i = 0; i < 256; i++) {
            Assert.That(gif[gc + i * 3], Is.EqualTo((byte)i), $"gray ramp R at {i}");
            Assert.That(gif[gc + i * 3 + 1], Is.EqualTo((byte)i));
            Assert.That(gif[gc + i * 3 + 2], Is.EqualTo((byte)i));
        }
    }

    [Test]
    public void Encode_NoLoop_OmitsNetscapeBlock() {
        var frames = new[] { Solid(8, 8, 10, 20, 30), Solid(8, 8, 200, 100, 50) };
        var gif = Encode(frames, loop: false);
        Assert.That(Contains(gif, "NETSCAPE2.0"), Is.False);
        Assert.That(Walk(gif).frames, Is.EqualTo(2));
    }

    [Test]
    public void Encode_ZeroFrames_Throws() {
        Assert.That(() => GifEncoder.Encode(new MemoryStream(), 0, _ => default, 10),
            Throws.ArgumentException);
    }

    [Test]
    public void Encode_MismatchedFrameSize_Throws() {
        var frames = new[] { Solid(8, 8, 1, 2, 3), Solid(10, 8, 4, 5, 6) };
        Assert.That(() => Encode(frames), Throws.TypeOf<InvalidOperationException>());
    }
}
