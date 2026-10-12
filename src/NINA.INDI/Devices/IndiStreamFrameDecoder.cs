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
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using SkiaSharp;

namespace NINA.INDI.Devices;

/// <summary>
/// Frames of an INDI video stream (CCD_VIDEO_STREAM). They are not FITS: the
/// driver's StreamManager sends the pixel buffer itself, cropped to
/// CCD_STREAM_FRAME, as
/// <list type="bullet">
/// <item><c>.stream</c>: raw samples, row by row, 1 or 3 components (RGB
/// interleaved), 8-bit unless the driver's STREAM_FULL_DEPTH is 16-bit;</item>
/// <item><c>.stream.z</c>: the same, zlib-compressed;</item>
/// <item><c>.stream_jpg</c>: a JPEG (MJPEG encoder, or a camera that only
/// streams JPEG).</item>
/// </list>
/// Until this existed every such frame went to the DSLR RAW decoder, came back
/// empty, and the stream fell back to slow single exposures (discussion #37).
/// The output is what the rest of the pipeline expects of any frame: one plane
/// of 16-bit samples, mono or a Bayer mosaic. The driver's CFA and bit depth
/// are applied afterwards by IndiCamera, as for FITS frames.
/// </summary>
public static class IndiStreamFrameDecoder {
    public static bool IsStreamFormat(string format)
        => format.StartsWith(".stream", StringComparison.OrdinalIgnoreCase);

    /// <param name="width">CCD_STREAM_FRAME width.</param>
    /// <param name="height">CCD_STREAM_FRAME height.</param>
    /// <returns>The frame, or null when the buffer does not fit the frame
    /// size (the caller counts it as an empty stream frame).</returns>
    public static IImageData? Decode(byte[] data, string format, int width, int height) {
        format = format.Trim().ToLowerInvariant();
        if (format.StartsWith(".stream_jpg")) return DecodeJpeg(data);

        if (format.EndsWith(".z")) {
            try { data = Inflate(data); } catch (InvalidDataException) { return null; }
        }
        if (width <= 0 || height <= 0) return null;
        long n = (long)width * height;
        if (n == 0 || data.Length % n != 0) return null;
        int bytesPerPixel = (int)(data.Length / n);
        return bytesPerPixel switch {
            1 => Mono8(data, width, height),
            2 => Mono16(data, width, height),
            3 => Rgb(data, width, height, 1),
            6 => Rgb(data, width, height, 2),
            _ => null
        };
    }

    private static byte[] Inflate(byte[] data) {
        using var input = new MemoryStream(data);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(data.Length * 4);
        z.CopyTo(output);
        return output.ToArray();
    }

    // 8-bit samples spread over the 16-bit container (x257, as the JPEG path
    // does), with the real precision reported so recorders do not take them
    // for 16-bit data.
    private static IImageData Mono8(byte[] d, int w, int h) {
        var px = new ushort[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = (ushort)(d[i] * 257);
        return Frame(px, w, h, significantBits: 8, rggb: false);
    }

    // Native byte order of the driver host: little-endian on every board and
    // PC Polaris runs on.
    private static IImageData Mono16(byte[] d, int w, int h) {
        var px = new ushort[w * h];
        Buffer.BlockCopy(d, 0, px, 0, px.Length * 2);
        return Frame(px, w, h, significantBits: 0, rggb: false);
    }

    // The relay carries one plane plus a Bayer pattern, so colour is re-mosaiced
    // into RGGB: each pixel keeps the channel an RGGB sensor would sample there.
    private static IImageData Rgb(byte[] d, int w, int h, int bytesPerSample) {
        var px = new ushort[w * h];
        for (int y = 0; y < h; y++) {
            bool evenRow = (y & 1) == 0;
            for (int x = 0; x < w; x++) {
                bool evenCol = (x & 1) == 0;
                int channel = evenRow ? (evenCol ? 0 : 1) : (evenCol ? 1 : 2);
                int i = (y * w + x) * 3 + channel;
                px[y * w + x] = bytesPerSample == 1
                    ? (ushort)(d[i] * 257)
                    : BitConverter.ToUInt16(d, i * 2);
            }
        }
        return Frame(px, w, h, significantBits: bytesPerSample == 1 ? 8 : 0, rggb: true);
    }

    private static IImageData? DecodeJpeg(byte[] data) {
        SKBitmap? bmp;
        try { bmp = SKBitmap.Decode(data); } catch { return null; }
        if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0) { bmp?.Dispose(); return null; }
        try {
            int w = bmp.Width, h = bmp.Height;
            var cols = bmp.Pixels;
            bool gray = bmp.ColorType == SKColorType.Gray8;
            var px = new ushort[w * h];
            for (int y = 0; y < h; y++) {
                bool evenRow = (y & 1) == 0;
                for (int x = 0; x < w; x++) {
                    var c = cols[y * w + x];
                    bool evenCol = (x & 1) == 0;
                    byte v = gray ? c.Red
                        : evenRow ? (evenCol ? c.Red : c.Green) : (evenCol ? c.Green : c.Blue);
                    px[y * w + x] = (ushort)(v * 257);
                }
            }
            return Frame(px, w, h, significantBits: 8, rggb: !gray);
        } finally { bmp.Dispose(); }
    }

    private static IImageData Frame(ushort[] px, int w, int h, int significantBits, bool rggb) {
        var props = new ImageProperties {
            Width = w, Height = h, BitDepth = 16, SignificantBitDepth = significantBits,
            IsBayered = rggb, BayerPattern = rggb ? BayerPatternEnum.RGGB : BayerPatternEnum.None
        };
        return new BaseImageData(px, props, new ImageMetaData { CreationTime = DateTime.UtcNow });
    }
}
