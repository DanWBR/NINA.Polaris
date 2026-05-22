using System.Buffers.Binary;

namespace NINA.Image.FileFormat.TIFF;

/// <summary>
/// Minimal uncompressed grayscale TIFF writer (8-bit and 16-bit). The
/// STUDIO export pipeline relies on this for "preserve the linear
/// dynamic range" output that PixInsight, Siril and Photoshop can
/// re-process without our stretch baked in. SkiaSharp doesn't ship a
/// TIFF encoder, and pulling a full ImageSharp dependency just for
/// this is overkill — the spec'd subset we need is tiny.
///
/// Format: little-endian "II" baseline TIFF with these IFD tags:
///   ImageWidth (256), ImageLength (257), BitsPerSample (258),
///   Compression (259) = 1 (none), PhotometricInterpretation (262) = 1
///   (BlackIsZero), StripOffsets (273), SamplesPerPixel (277) = 1,
///   RowsPerStrip (278), StripByteCounts (279), XResolution (282) = 72,
///   YResolution (283) = 72, ResolutionUnit (296) = 2 (inch).
/// All pixel data is one strip; the byte order is little-endian to
/// match what SkiaSharp / .NET produce by default.
/// </summary>
public static class TiffWriter {

    /// <summary>Write an 8-bit grayscale TIFF to disk.</summary>
    public static void Write8(byte[] pixels, int width, int height, string path) {
        using var fs = File.Create(path);
        Write(fs, pixels, width, height, bitsPerSample: 8);
    }

    /// <summary>Write a 16-bit grayscale TIFF to disk. Pixel data is
    /// little-endian to match in-memory ushort[] layout.</summary>
    public static void Write16(ushort[] pixels, int width, int height, string path) {
        // Reinterpret ushort[] as little-endian bytes. On every platform
        // we target (.NET 10 on x64/arm64) this is already LE.
        var bytes = System.Runtime.InteropServices.MemoryMarshal
            .AsBytes(pixels.AsSpan()).ToArray();
        using var fs = File.Create(path);
        Write(fs, bytes, width, height, bitsPerSample: 16);
    }

    private static void Write(Stream s, byte[] pixelBytes, int width, int height, int bitsPerSample) {
        // -- Header (8 bytes) ----------------------------------------
        Span<byte> hdr = stackalloc byte[8];
        hdr[0] = (byte)'I'; hdr[1] = (byte)'I';                  // little-endian
        BinaryPrimitives.WriteUInt16LittleEndian(hdr[2..], 42);  // magic
        // IFD offset comes right after the pixel strip.
        uint ifdOffset = (uint)(8 + pixelBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[4..], ifdOffset);
        s.Write(hdr);

        // -- Strip (raw pixel bytes) ---------------------------------
        s.Write(pixelBytes, 0, pixelBytes.Length);

        // -- IFD -----------------------------------------------------
        // 12 entries. After the entries: 2-byte count + 12·N entries +
        // 4-byte "next IFD" pointer (= 0). XResolution + YResolution
        // need 8 bytes each of trailing rational data, stored after.
        const ushort EntryCount = 12;
        int ifdSize = 2 + EntryCount * 12 + 4;
        uint xResOffset = ifdOffset + (uint)ifdSize;
        uint yResOffset = xResOffset + 8;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(EntryCount);

        WriteEntry(bw, 256, 3, 1, (uint)width);                    // ImageWidth
        WriteEntry(bw, 257, 3, 1, (uint)height);                   // ImageLength
        // BitsPerSample fits in 4 bytes when count=1 (one sample).
        WriteEntry(bw, 258, 3, 1, (uint)bitsPerSample);
        WriteEntry(bw, 259, 3, 1, 1);                              // Compression = none
        WriteEntry(bw, 262, 3, 1, 1);                              // PhotometricInterp = BlackIsZero
        WriteEntry(bw, 273, 4, 1, 8u);                             // StripOffsets = right after header
        WriteEntry(bw, 277, 3, 1, 1);                              // SamplesPerPixel
        WriteEntry(bw, 278, 3, 1, (uint)height);                   // RowsPerStrip
        WriteEntry(bw, 279, 4, 1, (uint)pixelBytes.Length);        // StripByteCounts
        WriteEntry(bw, 282, 5, 1, xResOffset);                     // XResolution (rational, offset)
        WriteEntry(bw, 283, 5, 1, yResOffset);                     // YResolution (rational, offset)
        WriteEntry(bw, 296, 3, 1, 2);                              // ResolutionUnit = inch

        bw.Write(0u);  // next IFD offset = 0 (no more)

        // -- Trailing rationals for XResolution / YResolution --------
        // 72/1 each — totally arbitrary, just present so apps like
        // Photoshop don't complain.
        bw.Write(72u); bw.Write(1u);
        bw.Write(72u); bw.Write(1u);

        var buf = ms.ToArray();
        s.Write(buf, 0, buf.Length);
    }

    /// <summary>
    /// Write a single 12-byte IFD entry. For SHORT (type=3) the value
    /// fits in the 4-byte value/offset field; the TIFF spec says shorter
    /// values are right-padded with zeros. For LONG (type=4) and RATIONAL
    /// (type=5 — uses offset-to-rational) we just write the value as-is.
    /// </summary>
    private static void WriteEntry(BinaryWriter bw, ushort tag, ushort type, uint count, uint value) {
        bw.Write(tag);
        bw.Write(type);
        bw.Write(count);
        if (type == 3 /* SHORT */ && count == 1) {
            bw.Write((ushort)value);
            bw.Write((ushort)0);  // padding to 4 bytes
        } else {
            bw.Write(value);
        }
    }
}
