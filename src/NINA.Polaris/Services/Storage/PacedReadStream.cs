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

namespace NINA.Polaris.Services.Storage;

/// <summary>
/// A read-only wrapper that paces whoever is consuming it.
///
/// <para>For uploaders that take a Stream and do the transfer themselves, so
/// there is no chunk loop of ours to pace. SSH.NET's SftpClient.UploadFile is
/// the case in hand: it pulls from the stream at whatever rate the link allows,
/// which is exactly the behaviour that had the storage push taking the whole
/// uplink from the live view. Slowing the SOURCE slows the transfer, without
/// reaching inside the library.</para>
///
/// <para>The delay is synchronous because the consumer is: an async pause here
/// would be ignored by a caller that never awaits. This runs on the push
/// service's own consumer task, not on a request path.</para>
/// </summary>
public sealed class PacedReadStream : Stream {
    private readonly Stream _inner;
    private readonly int _sharePercent;

    public PacedReadStream(Stream inner, int sharePercent) {
        _inner = inner;
        _sharePercent = sharePercent;
    }

    public override int Read(byte[] buffer, int offset, int count) {
        var started = System.Diagnostics.Stopwatch.StartNew();
        int read = _inner.Read(buffer, offset, count);
        if (read <= 0) return read;
        // Paced on the time the READ took, which for a local file is fast; the
        // upload's own back-pressure is what actually sets the pace, and this
        // adds the idle share on top of whatever that turns out to be.
        var idle = TransferPacer.DelayAfterChunk(started.Elapsed, _sharePercent);
        if (idle > TimeSpan.Zero) Thread.Sleep(idle);
        return read;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position {
        get => _inner.Position;
        set => _inner.Position = value;
    }
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void Flush() => _inner.Flush();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing) {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
