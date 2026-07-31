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

namespace NINA.Polaris.Services.Planetary;

/// <summary>
/// Reads a SER v3 file written by <see cref="SerFileWriter"/> or any other
/// compliant tool (FireCapture, SharpCap export, INDI ccdciel-rec, etc.).
/// Frames are random-access, open once, jump to any frame index by stride.
/// </summary>
public sealed class SerFileReader : IDisposable {
    private readonly FileStream _fs;
    private readonly BinaryReader _br;
    private readonly long _frameDataStart;
    private readonly long _bytesPerFrame;
    private readonly DateTime[] _timestamps;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public int BitDepth { get; }
    public SerColorMode ColorMode { get; }
    public int FrameCount { get; private set; }

    /// <summary>True when the header said zero frames and the count was
    /// rebuilt from the file length: an interrupted recording, readable
    /// anyway. Callers may want to say so rather than pretend it was
    /// normal.</summary>
    public bool RecoveredFrameCount { get; }

    /// <summary>Set to the header's claim when the file is SHORTER than that
    /// (truncated recording); FrameCount then holds what is actually
    /// present.</summary>
    public int TruncatedFrameCount { get; }

    public string Observer { get; }
    public string Instrument { get; }
    public string Telescope { get; }
    public DateTime StartUtc { get; }

    public SerFileReader(string path) {
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _br = new BinaryReader(_fs, Encoding.ASCII, leaveOpen: true);

        // Parse the fixed 178-byte header.
        var fileId = Encoding.ASCII.GetString(_br.ReadBytes(14)).TrimEnd();
        if (fileId != "LUCAM-RECORDER" && fileId != "LUCAM-RECORDE")
            throw new InvalidDataException($"Not a SER file: id='{fileId}'");
        _br.ReadUInt32();                                // LuID
        ColorMode = (SerColorMode)_br.ReadUInt32();
        _ = _br.ReadUInt32();                            // LittleEndian flag (we assume LE)
        Width = (int)_br.ReadUInt32();
        Height = (int)_br.ReadUInt32();
        BitDepth = (int)_br.ReadUInt32();
        FrameCount = (int)_br.ReadUInt32();
        Observer   = ReadAscii(40);
        Instrument = ReadAscii(40);
        Telescope  = ReadAscii(40);
        StartUtc   = new DateTime(_br.ReadInt64(), DateTimeKind.Utc);
        _ = _br.ReadInt64();                             // local-time ticks (ignored)

        _frameDataStart = SerFileWriter.HeaderSize;
        int planes = ColorMode is SerColorMode.Rgb or SerColorMode.Bgr ? 3 : 1;
        _bytesPerFrame = (long)Width * Height * planes * (BitDepth / 8);

        // FIELD8-3: trust the file, not just the header.
        //
        // The count is stamped when the recording closes, so a clip that was
        // interrupted carries a header count of 0 (or a stale, lower one)
        // while holding every frame it recorded. The operator hit this with a
        // 4.1 GB file the stacker called empty. The bytes on disk are the
        // ground truth: whatever fits between the header and the end of the
        // file is a frame that was written.
        //
        // The other direction matters too: a file cut off mid-frame (killed
        // recorder, full disk) claims more frames than it holds, and reading
        // the last one throws deep in the decoder. Clamp both ways.
        long capacity = _bytesPerFrame > 0
            ? Math.Max(0, (_fs.Length - _frameDataStart) / _bytesPerFrame) : 0;
        if (FrameCount <= 0 && capacity > 0) {
            RecoveredFrameCount = true;
            FrameCount = (int)Math.Min(int.MaxValue, capacity);
        } else if (FrameCount > capacity) {
            TruncatedFrameCount = FrameCount;
            FrameCount = (int)Math.Min(int.MaxValue, capacity);
        }

        // Optional timestamp trailer, present when the file size
        // exceeds header + frame data by FrameCount * 8 bytes.
        var trailerStart = _frameDataStart + _bytesPerFrame * FrameCount;
        _timestamps = new DateTime[FrameCount];
        if (_fs.Length >= trailerStart + (long)FrameCount * 8) {
            _fs.Seek(trailerStart, SeekOrigin.Begin);
            for (int i = 0; i < FrameCount; i++) {
                _timestamps[i] = new DateTime(_br.ReadInt64(), DateTimeKind.Utc);
            }
        } else {
            // No trailer, synthesize evenly-spaced timestamps from
            // StartUtc just so callers always get something.
            for (int i = 0; i < FrameCount; i++) _timestamps[i] = StartUtc;
        }
    }

    private string ReadAscii(int len) =>
        Encoding.ASCII.GetString(_br.ReadBytes(len)).TrimEnd('\0', ' ');

    /// <summary>UTC timestamp of frame <paramref name="index"/>.</summary>
    public DateTime TimestampOf(int index) {
        if (index < 0 || index >= FrameCount) throw new ArgumentOutOfRangeException(nameof(index));
        return _timestamps[index];
    }

    /// <summary>Reads frame <paramref name="index"/> into a fresh byte buffer.
    /// For 16-bit mono frames, prefer <see cref="ReadFrameAsUshort"/> to
    /// skip the extra copy.</summary>
    public byte[] ReadFrameBytes(int index) {
        if (_disposed) throw new ObjectDisposedException(nameof(SerFileReader));
        if (index < 0 || index >= FrameCount) throw new ArgumentOutOfRangeException(nameof(index));
        _fs.Seek(_frameDataStart + _bytesPerFrame * index, SeekOrigin.Begin);
        var buf = new byte[_bytesPerFrame];
        _fs.ReadExactly(buf, 0, (int)_bytesPerFrame);
        return buf;
    }

    /// <summary>Reads a 16-bit mono frame as a ushort[]. Throws for any
    /// other format, use ReadFrameBytes for 8-bit / RGB / Bayer.</summary>
    public ushort[] ReadFrameAsUshort(int index) {
        if (BitDepth != 16) throw new InvalidOperationException(
            $"ReadFrameAsUshort requires BitDepth=16, file has {BitDepth}");
        var bytes = ReadFrameBytes(index);
        var pixels = new ushort[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, pixels, 0, bytes.Length);
        return pixels;
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        try { _br.Dispose(); } catch { }
        try { _fs.Dispose(); } catch { }
    }
}