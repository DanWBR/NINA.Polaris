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

using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace NINA.INDI.Client;

/// <summary>
/// The INDI connection over indiserver's local Unix socket, where BLOBs arrive
/// as shared memory instead of base64 text.
///
/// <para>indiserver (libindi 2.x) treats every client on its Unix socket as
/// shared-buffer capable: a <c>setBLOBVector</c> then carries
/// <c>&lt;oneBLOB ... size="N" attached="true"/&gt;</c> with no content, and the
/// file descriptor of a memfd holding the N bytes rides along as
/// <c>SCM_RIGHTS</c> ancillary data on the <c>sendmsg</c> that starts the message.
/// The descriptors arrive in the order of the attached elements. Over TCP the
/// same frame is base64, 4/3 the size, encoded by indiserver and decoded here:
/// on a Raspberry Pi 4 that hop alone cost 0.7 s for a 35 MB DSLR RAW and
/// 1.4 s for a 67 MB one, every sub.</para>
///
/// <para>This stream is what the XML parser reads. <see cref="Read"/> is
/// <c>recvmsg</c>, which queues any descriptors it receives; the parser asks for
/// them through <see cref="TakeAttachedBlob"/> when it meets an attached
/// element. The socket address is Linux abstract (a leading NUL), matching
/// indiserver's <c>-u</c> default of <c>/tmp/indiserver</c>.</para>
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class IndiSharedBufferStream : Stream {
    private const int MaxFdsPerMessage = 16;                 // libindi MAXFD_PER_MESSAGE
    private const int MSG_CMSG_CLOEXEC = 0x40000000;
    private const int SOL_SOCKET = 1;
    private const int SCM_RIGHTS = 1;
    private const int EINTR = 4;
    private const int EAGAIN = 11;

    private readonly Socket _socket;
    private readonly ConcurrentQueue<int> _fds = new();
    private readonly string _path;
    private bool _disposed;

    public static bool IsSupported => OperatingSystem.IsLinux();

    /// <summary>Abstract socket name indiserver listens on (its <c>-u</c>, default <c>/tmp/indiserver</c>).</summary>
    public string Path => _path;

    /// <summary>Descriptors received and not yet consumed by the parser.</summary>
    public int PendingAttachedBlobs => _fds.Count;

    private IndiSharedBufferStream(Socket socket, string path) {
        _socket = socket;
        _path = path;
    }

    /// <summary>Connects to indiserver's abstract Unix socket. Returns null when
    /// nothing listens there, so the caller can fall back to TCP.</summary>
    public static async Task<IndiSharedBufferStream?> TryConnectAsync(string path, TimeSpan timeout, CancellationToken ct) {
        if (!IsSupported || string.IsNullOrEmpty(path)) return null;
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint("\0" + path), cts.Token);
            return new IndiSharedBufferStream(socket, path);
        } catch (Exception) when (!ct.IsCancellationRequested) {
            socket.Dispose();
            return null;
        }
    }

    /// <summary>Wraps an already connected Unix socket (tests use a socketpair).</summary>
    public static IndiSharedBufferStream FromConnectedSocket(Socket socket, string path = "") =>
        new(socket, path);

    /// <summary>Takes the next attached BLOB: reads <paramref name="size"/> bytes
    /// from the oldest pending descriptor and closes it. Null when no descriptor
    /// is pending, which means the server announced an attachment it never
    /// sent.</summary>
    public byte[]? TakeAttachedBlob(int size) {
        if (!_fds.TryDequeue(out int fd)) return null;
        using var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        if (size <= 0) return Array.Empty<byte>();
        var buffer = new byte[size];
        int total = 0;
        while (total < size) {
            // pread, so the offset the server left on the shared description does not matter.
            int n = RandomAccess.Read(handle, buffer.AsSpan(total), total);
            if (n <= 0) break;
            total += n;
        }
        if (total < size) Array.Resize(ref buffer, total);
        return buffer;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override unsafe int Read(byte[] buffer, int offset, int count) {
        ArgumentNullException.ThrowIfNull(buffer);
        if (count == 0) return 0;
        ObjectDisposedException.ThrowIf(_disposed, this);

        int controlLen = CmsgSpace(MaxFdsPerMessage * sizeof(int));
        byte* control = stackalloc byte[controlLen];
        fixed (byte* data = &buffer[offset]) {
            while (true) {
                var iov = new iovec { iov_base = (IntPtr)data, iov_len = (nuint)count };
                var msg = new msghdr {
                    msg_iov = (IntPtr)(&iov),
                    msg_iovlen = 1,
                    msg_control = (IntPtr)control,
                    msg_controllen = (nuint)controlLen
                };
                nint n = recvmsg((int)_socket.Handle, &msg, MSG_CMSG_CLOEXEC);
                if (n < 0) {
                    int errno = Marshal.GetLastPInvokeError();
                    if (errno == EINTR) continue;
                    if (errno == EAGAIN) {
                        // .NET switches the descriptor to non-blocking as soon as an async
                        // operation (ConnectAsync, SendAsync) has run on it. Wait for data
                        // the way the runtime would, then read again.
                        if (!WaitReadable()) return 0;
                        continue;
                    }
                    if (_disposed) return 0;
                    throw new IOException($"recvmsg on the INDI unix socket failed, errno {errno}");
                }
                CollectFds(control, (int)msg.msg_controllen);
                return (int)n;
            }
        }
    }

    /// <summary>Blocks until the socket is readable or closed. False once disposed.</summary>
    private bool WaitReadable() {
        try {
            while (!_disposed) {
                if (_socket.Poll(1_000_000, SelectMode.SelectRead)) return true;
            }
        } catch (ObjectDisposedException) { }
        catch (SocketException) { }
        return false;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<int>(cancellationToken);
        // recvmsg has no async form; the parser's read loop already runs on its own task.
        return Task.Run(() => Read(buffer, offset, count), cancellationToken);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
        if (MemoryMarshal.TryGetArray<byte>(buffer, out var segment))
            return new ValueTask<int>(ReadAsync(segment.Array!, segment.Offset, segment.Count, cancellationToken));
        return ReadIntoMemoryAsync(buffer, cancellationToken);
    }

    private async ValueTask<int> ReadIntoMemoryAsync(Memory<byte> buffer, CancellationToken cancellationToken) {
        var tmp = new byte[buffer.Length];
        int n = await ReadAsync(tmp, 0, tmp.Length, cancellationToken);
        tmp.AsSpan(0, n).CopyTo(buffer.Span);
        return n;
    }

    private unsafe void CollectFds(byte* control, int controlLen) {
        int pos = 0;
        while (pos + sizeof(cmsghdr) <= controlLen) {
            var hdr = (cmsghdr*)(control + pos);
            int len = (int)hdr->cmsg_len;
            if (len < sizeof(cmsghdr)) break;
            if (hdr->cmsg_level == SOL_SOCKET && hdr->cmsg_type == SCM_RIGHTS) {
                int count = (len - sizeof(cmsghdr)) / sizeof(int);
                var fds = (int*)(control + pos + sizeof(cmsghdr));
                for (int i = 0; i < count; i++) _fds.Enqueue(fds[i]);
            }
            pos += CmsgAlign(len);
        }
    }

    public override void Write(byte[] buffer, int offset, int count) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int sent = 0;
        while (sent < count) sent += _socket.Send(buffer, offset + sent, count - sent, SocketFlags.None);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int sent = 0;
        while (sent < count)
            sent += await _socket.SendAsync(new ArraySegment<byte>(buffer, offset + sent, count - sent), SocketFlags.None, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) {
        if (MemoryMarshal.TryGetArray(buffer, out var segment))
            return new ValueTask(WriteAsync(segment.Array!, segment.Offset, segment.Count, cancellationToken));
        return new ValueTask(WriteAsync(buffer.ToArray(), 0, buffer.Length, cancellationToken));
    }

    protected override void Dispose(bool disposing) {
        if (_disposed) return;
        _disposed = true;
        if (disposing) {
            // Wakes a blocked recvmsg with EOF before the handle goes away.
            try { _socket.Shutdown(SocketShutdown.Both); } catch { }
            _socket.Dispose();
            while (_fds.TryDequeue(out int fd)) close(fd);
        }
        base.Dispose(disposing);
    }

    private static int CmsgAlign(int len) => (len + IntPtr.Size - 1) & ~(IntPtr.Size - 1);
    private static unsafe int CmsgSpace(int dataLen) => sizeof(cmsghdr) + CmsgAlign(dataLen);

    // LP64 layouts (linux-x64 and linux-arm64 are identical here).
    [StructLayout(LayoutKind.Sequential)]
    private struct iovec { public IntPtr iov_base; public nuint iov_len; }

    [StructLayout(LayoutKind.Sequential)]
    private struct msghdr {
        public IntPtr msg_name; public uint msg_namelen;
        public IntPtr msg_iov; public nuint msg_iovlen;
        public IntPtr msg_control; public nuint msg_controllen;
        public int msg_flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct cmsghdr { public nuint cmsg_len; public int cmsg_level; public int cmsg_type; }

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe nint recvmsg(int sockfd, msghdr* msg, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
