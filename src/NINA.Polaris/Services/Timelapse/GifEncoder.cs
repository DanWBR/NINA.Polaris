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

namespace NINA.Polaris.Services.Timelapse;

/// <summary>One RGB frame handed to <see cref="GifEncoder"/>. <see cref="Rgb"/>
/// is tightly packed R,G,B bytes, length == Width*Height*3. Deliberately
/// Skia-free so the encoder (and its tests) carry no native dependency.</summary>
public readonly struct GifFrame {
    public readonly byte[] Rgb;
    public readonly int Width;
    public readonly int Height;
    public GifFrame(byte[] rgb, int width, int height) { Rgb = rgb; Width = width; Height = height; }
}

/// <summary>
/// Self-contained animated-GIF (GIF89a) encoder. Pure managed code — no ffmpeg,
/// no native GIF library (SkiaSharp ships no multi-frame encoder) — so a
/// time-lapse GIF can always be produced regardless of what is installed on the
/// host. Writes a global 256-colour table (256 grays for a mono sequence, a
/// median-cut palette for colour), a Netscape loop extension, and one LZW image
/// block per frame.
///
/// Frames are pulled lazily via a <c>getFrame</c> delegate (packed RGB bytes) so
/// the caller can decode from disk one at a time (bounded memory) and the
/// encoder stays independent of any image library. All frames must share the
/// dimensions of frame 0.
/// </summary>
public static class GifEncoder {

    /// <summary>Encode <paramref name="frameCount"/> frames (pulled from
    /// <paramref name="getFrame"/>, indices 0..frameCount-1) into an animated GIF
    /// written to <paramref name="output"/>. <paramref name="fps"/> sets the
    /// per-frame delay; <paramref name="loop"/> loops forever when true.</summary>
    public static void Encode(Stream output, int frameCount, Func<int, GifFrame> getFrame,
                              int fps, bool loop = true, CancellationToken ct = default) {
        if (frameCount <= 0) throw new ArgumentException("No frames to encode.", nameof(frameCount));
        fps = Math.Clamp(fps, 1, 100);
        // GIF delay is in centiseconds; keep at least 2 cs so players don't run wild.
        int delayCs = Math.Max(2, (int)Math.Round(100.0 / fps));

        var first = getFrame(0);
        int w = first.Width, h = first.Height;
        if (w <= 0 || h <= 0 || first.Rgb == null || first.Rgb.Length < w * h * 3)
            throw new InvalidOperationException("Frame 0 has no pixels.");

        // Build the global palette from a sample of frames (cap the work).
        var (palette, grayscale) = BuildPalette(frameCount, getFrame, ct);
        var map = grayscale ? null : new NearestColorCache(palette);

        var bw = new BinaryWriter(output);
        WriteHeader(bw, w, h, palette, loop);

        var indices = new byte[w * h];
        for (int i = 0; i < frameCount; i++) {
            ct.ThrowIfCancellationRequested();
            var frame = getFrame(i);
            Quantize(frame, w, h, indices, grayscale, map);
            WriteFrame(bw, w, h, indices, delayCs);
        }
        bw.Write((byte)0x3B); // trailer
        bw.Flush();
    }

    // ---- Palette -------------------------------------------------------------

    private static (Rgb[] palette, bool grayscale) BuildPalette(
            int frameCount, Func<int, GifFrame> getFrame, CancellationToken ct) {
        // Sample up to 12 evenly-spaced frames, and within each a strided subset
        // of pixels, so a long sequence doesn't cost a full scan.
        int sampleFrames = Math.Min(12, frameCount);
        var samples = new List<int>(64000);          // packed 0xRRGGBB
        bool anyColour = false;
        for (int s = 0; s < sampleFrames; s++) {
            ct.ThrowIfCancellationRequested();
            int idx = sampleFrames == 1 ? 0 : (int)((long)s * (frameCount - 1) / (sampleFrames - 1));
            var f = getFrame(idx);
            var rgb = f.Rgb;
            int px = f.Width * f.Height;
            if (rgb == null || px <= 0) continue;
            int stride = Math.Max(1, px / 6000);     // ~6k px/frame
            for (int p = 0; p < px; p += stride) {
                int o = p * 3;
                byte r = rgb[o], g = rgb[o + 1], b = rgb[o + 2];
                if (r != g || g != b) anyColour = true;
                samples.Add((r << 16) | (g << 8) | b);
            }
        }

        if (!anyColour || samples.Count == 0) {
            // Mono sequence: a 256-level gray ramp needs no quantisation and maps
            // directly (index == luma), which is fast and lossless for gray.
            var gray = new Rgb[256];
            for (int i = 0; i < 256; i++) gray[i] = new Rgb((byte)i, (byte)i, (byte)i);
            return (gray, true);
        }
        return (MedianCut(samples, 256), false);
    }

    /// <summary>Median-cut colour quantisation (Heckbert, 1982): recursively
    /// split the colour box with the widest channel at its median until 256
    /// boxes remain, then average each box.</summary>
    private readonly struct Rgb {
        public readonly byte R, G, B;
        public Rgb(byte r, byte g, byte b) { R = r; G = g; B = b; }
    }

    private static Rgb[] MedianCut(List<int> pixels, int maxColors) {
        var boxes = new List<(int lo, int hi)> { (0, pixels.Count) };
        var arr = pixels.ToArray();
        while (boxes.Count < maxColors) {
            // Pick the box with the largest channel range that can still split.
            int best = -1, bestRange = 0, bestChan = 0;
            for (int b = 0; b < boxes.Count; b++) {
                var (lo, hi) = boxes[b];
                if (hi - lo < 2) continue;
                int rMin = 255, rMax = 0, gMin = 255, gMax = 0, bMin = 255, bMax = 0;
                for (int i = lo; i < hi; i++) {
                    int c = arr[i]; int r = (c >> 16) & 255, g = (c >> 8) & 255, bl = c & 255;
                    if (r < rMin) rMin = r; if (r > rMax) rMax = r;
                    if (g < gMin) gMin = g; if (g > gMax) gMax = g;
                    if (bl < bMin) bMin = bl; if (bl > bMax) bMax = bl;
                }
                int rr = rMax - rMin, gr = gMax - gMin, br = bMax - bMin;
                int chan = rr >= gr && rr >= br ? 0 : (gr >= br ? 1 : 2);
                int range = Math.Max(rr, Math.Max(gr, br));
                if (range > bestRange) { bestRange = range; best = b; bestChan = chan; }
            }
            if (best < 0) break; // nothing left to split
            var (blo, bhi) = boxes[best];
            int shift = bestChan == 0 ? 16 : (bestChan == 1 ? 8 : 0);
            Array.Sort(arr, blo, bhi - blo, Comparer<int>.Create(
                (x, y) => ((x >> shift) & 255).CompareTo((y >> shift) & 255)));
            int mid = (blo + bhi) / 2;
            boxes[best] = (blo, mid);
            boxes.Add((mid, bhi));
        }

        var palette = new Rgb[boxes.Count];
        for (int b = 0; b < boxes.Count; b++) {
            var (lo, hi) = boxes[b];
            long r = 0, g = 0, bl = 0; int cnt = hi - lo;
            for (int i = lo; i < hi; i++) { int c = arr[i]; r += (c >> 16) & 255; g += (c >> 8) & 255; bl += c & 255; }
            cnt = Math.Max(1, cnt);
            palette[b] = new Rgb((byte)(r / cnt), (byte)(g / cnt), (byte)(bl / cnt));
        }
        return palette;
    }

    // Nearest-palette lookup with a coarse 32x32x32 cache: astro frames have few
    // distinct colours, so the cache turns a per-pixel 256-way search into a hit.
    private sealed class NearestColorCache {
        private readonly Rgb[] _pal;
        private readonly short[] _cache = new short[32 * 32 * 32];
        public NearestColorCache(Rgb[] pal) { _pal = pal; Array.Fill(_cache, (short)-1); }
        public int Index(byte r, byte g, byte b) {
            int key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
            int hit = _cache[key];
            if (hit >= 0) return hit;
            int best = 0, bestD = int.MaxValue;
            for (int i = 0; i < _pal.Length; i++) {
                int dr = _pal[i].R - r, dg = _pal[i].G - g, db = _pal[i].B - b;
                int d = dr * dr + dg * dg + db * db;
                if (d < bestD) { bestD = d; best = i; if (d == 0) break; }
            }
            _cache[key] = (short)best;
            return best;
        }
    }

    private static void Quantize(GifFrame frame, int w, int h, byte[] indices,
                                 bool grayscale, NearestColorCache? map) {
        var rgb = frame.Rgb;
        if (frame.Width != w || frame.Height != h || rgb == null || rgb.Length < w * h * 3)
            throw new InvalidOperationException("All frames must share frame 0's dimensions.");
        for (int i = 0, o = 0; i < w * h; i++, o += 3) {
            byte r = rgb[o], g = rgb[o + 1], b = rgb[o + 2];
            indices[i] = grayscale
                ? (byte)((r * 299 + g * 587 + b * 114) / 1000)
                : (byte)map!.Index(r, g, b);
        }
    }

    // ---- GIF stream ----------------------------------------------------------

    private static void WriteHeader(BinaryWriter bw, int w, int h, Rgb[] palette, bool loop) {
        bw.Write(new[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' });
        // Logical Screen Descriptor. Packed: GCT flag=1, colour res=7, sort=0,
        // GCT size=7 (2^(7+1)=256 entries).
        bw.Write((ushort)w);
        bw.Write((ushort)h);
        bw.Write((byte)0xF7);
        bw.Write((byte)0);   // background colour index
        bw.Write((byte)0);   // pixel aspect ratio
        // Global colour table, padded to 256 entries.
        for (int i = 0; i < 256; i++) {
            var c = i < palette.Length ? palette[i] : new Rgb(0, 0, 0);
            bw.Write(c.R); bw.Write(c.G); bw.Write(c.B);
        }
        if (loop) {
            // NETSCAPE2.0 application extension: loop forever.
            bw.Write((byte)0x21); bw.Write((byte)0xFF); bw.Write((byte)0x0B);
            bw.Write(new[] { (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A', (byte)'P', (byte)'E', (byte)'2', (byte)'.', (byte)'0' });
            bw.Write((byte)0x03); bw.Write((byte)0x01);
            bw.Write((ushort)0); // 0 = infinite
            bw.Write((byte)0x00);
        }
    }

    private static void WriteFrame(BinaryWriter bw, int w, int h, byte[] indices, int delayCs) {
        // Graphic Control Extension (delay + no transparency, disposal = none).
        bw.Write((byte)0x21); bw.Write((byte)0xF9); bw.Write((byte)0x04);
        bw.Write((byte)0x00);            // packed: no transparency, disposal 0
        bw.Write((ushort)delayCs);
        bw.Write((byte)0x00);            // transparent colour index (unused)
        bw.Write((byte)0x00);            // block terminator
        // Image Descriptor (full frame, no local colour table, no interlace).
        bw.Write((byte)0x2C);
        bw.Write((ushort)0); bw.Write((ushort)0);
        bw.Write((ushort)w); bw.Write((ushort)h);
        bw.Write((byte)0x00);
        // LZW image data.
        LzwCompress(bw, indices, minCodeSize: 8);
    }

    // GIF-variant LZW: variable-width codes packed LSB-first, chunked into
    // sub-blocks of at most 255 bytes.
    private static void LzwCompress(BinaryWriter bw, byte[] data, int minCodeSize) {
        bw.Write((byte)minCodeSize);

        int clearCode = 1 << minCodeSize;      // 256
        int endCode = clearCode + 1;           // 257
        int codeSize = minCodeSize + 1;        // 9
        int nextCode = endCode + 1;            // 258
        var dict = new Dictionary<int, int>();

        var sub = new List<byte>(256);         // current sub-block (<=255 bytes)
        int bitBuffer = 0, bitCount = 0;

        void EmitBits(int code) {
            bitBuffer |= code << bitCount;
            bitCount += codeSize;
            while (bitCount >= 8) {
                sub.Add((byte)(bitBuffer & 0xFF));
                bitBuffer >>= 8; bitCount -= 8;
                if (sub.Count == 255) { bw.Write((byte)255); bw.Write(sub.ToArray()); sub.Clear(); }
            }
        }
        void ResetDict() { dict.Clear(); codeSize = minCodeSize + 1; nextCode = endCode + 1; }

        EmitBits(clearCode);
        if (data.Length == 0) { EmitBits(endCode); FlushLzw(bw, sub, ref bitBuffer, ref bitCount); return; }

        int prefix = data[0];
        for (int i = 1; i < data.Length; i++) {
            int k = data[i];
            int key = (prefix << 8) | k;
            if (dict.TryGetValue(key, out var combined)) {
                prefix = combined;
            } else {
                EmitBits(prefix);
                dict[key] = nextCode++;
                if (nextCode > (1 << codeSize) && codeSize < 12) codeSize++;
                if (nextCode >= 4096) { EmitBits(clearCode); ResetDict(); }
                prefix = k;
            }
        }
        EmitBits(prefix);
        EmitBits(endCode);
        FlushLzw(bw, sub, ref bitBuffer, ref bitCount);
    }

    private static void FlushLzw(BinaryWriter bw, List<byte> sub, ref int bitBuffer, ref int bitCount) {
        if (bitCount > 0) { sub.Add((byte)(bitBuffer & 0xFF)); bitBuffer = 0; bitCount = 0; }
        if (sub.Count > 0) { bw.Write((byte)sub.Count); bw.Write(sub.ToArray()); sub.Clear(); }
        bw.Write((byte)0x00); // block terminator
    }
}
