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

using NINA.Core.Enum;
using NINA.Image.ImageData;

namespace NINA.Image.FileFormat.FITS;

/// <summary>
/// Streaming, strip-at-a-time FITS reader for integer-format files
/// (BITPIX 8/16/32, single 2-D plane). Reads the header once, then lets
/// the caller pull horizontal row strips on demand via
/// <see cref="ReadRows"/>, decoding only that slice into a <c>ushort[]</c>.
/// <para>
/// This is what lets the STUDIO master-frame stacker integrate N frames
/// while holding only one strip of each in RAM at a time, instead of all
/// N full-resolution buffers at once (the old behaviour that pushed a Pi
/// to the edge of its memory on a 20-frame OSC stack).
/// </para>
/// <para>
/// Float FITS (BITPIX -32/-64) is intentionally <b>not</b> supported here:
/// <see cref="FITSReader"/>'s float path rescales using a GLOBAL min/max
/// scan over the whole image, which a per-strip reader can't reproduce
/// without a full pass. Callers must check <see cref="IsStripable"/> (or
/// <see cref="IsFloat"/>) and fall back to <see cref="FITSReader.Read(Stream)"/>
/// for those — rare for raw calibration frames, which are camera 16-bit.
/// </para>
/// </summary>
public sealed class FitsStripReader : IDisposable {
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly long _dataStart;
    private readonly int _bytesPerPixel;
    private byte[] _rawScratch = Array.Empty<byte>();

    /// <summary>Image width in pixels (NAXIS1).</summary>
    public int Width { get; }
    /// <summary>Image height in rows (NAXIS2).</summary>
    public int Height { get; }
    /// <summary>Raw FITS BITPIX (8 / 16 / 32 / -32 / -64).</summary>
    public int Bitpix { get; }
    /// <summary>Number of image planes (1 for mono/CFA, 3 for an RGB cube).</summary>
    public int Planes { get; }
    public int Bzero { get; }
    public double Bscale { get; }
    public BayerPatternEnum BayerPattern { get; }
    public ImageProperties Properties { get; }
    public ImageMetaData MetaData { get; }

    /// <summary>True when the payload is IEEE float (BITPIX &lt; 0).</summary>
    public bool IsFloat => Bitpix < 0;

    /// <summary>
    /// True when this file can be read strip-by-strip: an integer
    /// BITPIX (8/16/32) with a single 2-D plane. False for float files
    /// and RGB cubes — the caller should fall back to the full-frame
    /// <see cref="FITSReader.Read(Stream)"/> in those cases.
    /// </summary>
    public bool IsStripable => !IsFloat && Planes == 1;

    private FitsStripReader(Stream stream, bool ownsStream) {
        _stream = stream;
        _ownsStream = ownsStream;

        // ReadHeadersOnly leaves the stream positioned exactly at the
        // first byte of the pixel-data block (the spec puts data on the
        // next 2880 boundary after the header, which is where the block
        // reader stops).
        var headers = FITSReader.ReadHeadersOnly(stream);
        _dataStart = stream.Position;

        Bitpix = FITSReader.GetIntHeader(headers, "BITPIX", 16);
        int naxis = FITSReader.GetIntHeader(headers, "NAXIS", 2);
        Width = FITSReader.GetIntHeader(headers, "NAXIS1", 0);
        Height = FITSReader.GetIntHeader(headers, "NAXIS2", 0);
        Planes = (naxis >= 3) ? FITSReader.GetIntHeader(headers, "NAXIS3", 1) : 1;
        if (Planes != 1 && Planes != 3) Planes = 1;
        Bzero = FITSReader.GetIntHeader(headers, "BZERO", 0);
        Bscale = FITSReader.GetDoubleHeader(headers, "BSCALE", 1.0);

        if (Width <= 0 || Height <= 0) {
            throw new InvalidDataException(
                $"FITS image has no pixels (NAXIS1={Width}, NAXIS2={Height}); " +
                "the file is empty or malformed.");
        }

        var bayerPat = FITSReader.GetStringHeader(headers, "BAYERPAT", "");
        BayerPattern = bayerPat.ToUpperInvariant() switch {
            "RGGB" => BayerPatternEnum.RGGB,
            "BGGR" => BayerPatternEnum.BGGR,
            "GBRG" => BayerPatternEnum.GBRG,
            "GRBG" => BayerPatternEnum.GRBG,
            _ => BayerPatternEnum.None
        };

        Properties = new ImageProperties {
            Width = Width,
            Height = Height,
            // Mirror FITSReader.Read: 8-bit is promoted into the 16-bit
            // range on decode, and anything above 16 collapses to 16.
            BitDepth = Math.Abs(Bitpix) <= 8 ? 16 : (Math.Abs(Bitpix) > 16 ? 16 : Math.Abs(Bitpix)),
            IsBayered = BayerPattern != BayerPatternEnum.None,
            BayerPattern = BayerPattern,
            Channels = Planes,
            Wcs = WcsHeaders.Read(headers),
        };
        MetaData = FITSReader.ExtractMetaData(headers);
        _bytesPerPixel = Math.Abs(Bitpix) / 8;
    }

    /// <summary>Open a FITS file for strip reading; owns the file handle.</summary>
    public static FitsStripReader Open(string path)
        => new(File.OpenRead(path), ownsStream: true);

    /// <summary>Wrap an existing seekable stream (caller keeps ownership).</summary>
    public static FitsStripReader Open(Stream stream)
        => new(stream, ownsStream: false);

    /// <summary>
    /// Decode <paramref name="rowCount"/> rows starting at row
    /// <paramref name="startRow"/> (0-based, plane 0) into
    /// <paramref name="dest"/>, which must hold at least
    /// <c>rowCount * Width</c> pixels. Only valid when
    /// <see cref="IsStripable"/> is true.
    /// </summary>
    public void ReadRows(int startRow, int rowCount, ushort[] dest) {
        if (!IsStripable) {
            throw new NotSupportedException(
                $"FitsStripReader cannot strip-read this file (BITPIX={Bitpix}, planes={Planes}); " +
                "use FITSReader.Read for float / RGB-cube sources.");
        }
        if (startRow < 0 || rowCount <= 0 || startRow + rowCount > Height) {
            throw new ArgumentOutOfRangeException(nameof(startRow),
                $"Requested rows [{startRow}, {startRow + rowCount}) out of range for height {Height}.");
        }
        long pixelsToRead = (long)rowCount * Width;
        if (dest.Length < pixelsToRead) {
            throw new ArgumentException(
                $"Destination buffer too small: {dest.Length} < {pixelsToRead}.", nameof(dest));
        }

        long byteOffset = _dataStart + (long)startRow * Width * _bytesPerPixel;
        _stream.Seek(byteOffset, SeekOrigin.Begin);

        int rawBytes = (int)(pixelsToRead * _bytesPerPixel);
        if (_rawScratch.Length < rawBytes) _rawScratch = new byte[rawBytes];

        int totalRead = 0;
        while (totalRead < rawBytes) {
            int read = _stream.Read(_rawScratch, totalRead, rawBytes - totalRead);
            if (read == 0) {
                throw new EndOfStreamException(
                    $"FITS pixel data truncated: expected {rawBytes} bytes for rows " +
                    $"[{startRow}, {startRow + rowCount}), got {totalRead}.");
            }
            totalRead += read;
        }

        FITSReader.DecodeIntegerPixels(_rawScratch, dest, pixelsToRead, Bitpix, Bzero, Bscale);
    }

    public void Dispose() {
        if (_ownsStream) _stream.Dispose();
    }
}
