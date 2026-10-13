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

using SkiaSharp;

namespace NINA.Camera.CanonEdsdk;

/// <summary>
/// The picture Polaris shows for a Canon capture. The camera sends a RAW
/// (.CR2/.CR3), a JPEG, or both, depending on its image quality setting; the
/// RAW is what gets saved, and the preview comes from a JPEG: the one the
/// camera sent, or else the full-size JPEG every Canon RAW carries inside it.
///
/// The result is an RGGB mosaic of 16-bit samples, the same shape a colour
/// astro camera delivers, so the client debayers it back to colour. It used to
/// be a luminance plane (a grey preview), and a RAW-only capture gave a 1x1
/// placeholder: nothing on screen at all.
/// </summary>
public static class CanonPreview {
    /// <summary>The largest JPEG embedded in <paramref name="data"/>, decoded,
    /// or null. Every start-of-image marker is a candidate; only its header is
    /// read to learn the size, and the largest is decoded. Unlike scanning for
    /// the end marker, this does not cut a JPEG short at the end of the
    /// thumbnail its own EXIF block carries.</summary>
    public static SKBitmap? LargestEmbeddedJpeg(byte[] data) {
        int bestOff = -1;
        long bestArea = 0;
        for (int i = 0; i + 3 < data.Length; i++) {
            if (data[i] != 0xFF || data[i + 1] != 0xD8 || data[i + 2] != 0xFF) continue;
            try {
                using var codec = SKCodec.Create(new MemoryStream(data, i, data.Length - i, writable: false));
                if (codec == null) continue;
                long area = (long)codec.Info.Width * codec.Info.Height;
                if (area > bestArea) { bestArea = area; bestOff = i; }
            } catch { /* not a JPEG after all */ }
        }
        if (bestOff < 0) return null;
        try {
            using var codec = SKCodec.Create(new MemoryStream(data, bestOff, data.Length - bestOff, writable: false));
            return codec == null ? null : SKBitmap.Decode(codec);
        } catch {
            return null;
        }
    }

    /// <summary>Re-mosaic an RGB picture into RGGB: each pixel keeps the one
    /// channel an RGGB sensor samples there, 8-bit spread over 16 (x257).</summary>
    public static ushort[] ToRggb(SKBitmap bmp) {
        int w = bmp.Width, h = bmp.Height;
        var cols = bmp.Pixels;
        var px = new ushort[w * h];
        for (int y = 0; y < h; y++) {
            bool evenRow = (y & 1) == 0;
            int row = y * w;
            for (int x = 0; x < w; x++) {
                var c = cols[row + x];
                bool evenCol = (x & 1) == 0;
                byte v = evenRow ? (evenCol ? c.Red : c.Green) : (evenCol ? c.Green : c.Blue);
                px[row + x] = (ushort)(v * 257);
            }
        }
        return px;
    }
}
