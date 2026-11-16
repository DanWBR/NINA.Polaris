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
using NINA.INDI.Protocol;

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
    // libindi rounds every shared BLOB allocation up to this (BLOB_SIZE_UNIT in sharedblob.c),
    // so a descriptor's file is between the announced size and one unit above it.
    private const long BlobSizeUnit = 0x100000;
    /// <summary>indiserver's <c>-u</c> default (INDIUNIXSOCK).</summary>
    public const string DefaultSocketPath = "/tmp/indiserver";

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
    /// <exception cref="IndiAttachedBlobException">The descriptor's file is not
    /// the size the element announced (allowing for libindi's rounding up to
    /// 1 MiB). The queue has then drifted from the parser and the connection
    /// has to be dropped; every pending descriptor is closed here.</exception>
    public byte[]? TakeAttachedBlob(int size) {
        if (!_fds.TryDequeue(out int fd)) return null;
        using var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        if (size <= 0) return Array.Empty<byte>();
        long length = RandomAccess.GetLength(handle);
        if (length < size || length >= size + BlobSizeUnit) {
            DiscardPendingDescriptors();
            throw new IndiAttachedBlobException(
                $"attached BLOB descriptor holds {length} bytes, the message announced {size}: " +
                "descriptor queue out of step with the XML, dropping the INDI connection");
        }
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

    /// <summary>Closes every descriptor received and not yet consumed. Called when
    /// the queue can no longer be matched to the XML; the stream is about to be
    /// dropped by its owner.</summary>
    public void DiscardPendingDescriptors() {
        while (_fds.TryDequeue(out int fd)) close(fd);
    }

    /// <summary>Finds the Unix socket of the indiserver that owns <paramref name="port"/>
    /// on this host, by reading the command line of every running <c>indiserver</c>
    /// in <c>/proc</c>. Null when no such process is visible, in which case TCP is
    /// the right choice: a fixed default path would reach a different indiserver
    /// than the configured port, or none at all when INDI Web Manager started it
    /// with its own <c>-u</c>, as it does on a Polaris rig.</summary>
    public static string? FindSocketOfIndiserverOnPort(int port) {
        if (!IsSupported) return null;
        string[] dirs;
        try { dirs = Directory.GetDirectories("/proc"); } catch { return null; }
        foreach (var dir in dirs) {
            if (!int.TryParse(System.IO.Path.GetFileName(dir), out _)) continue;
            string[] argv;
            try {
                var raw = File.ReadAllBytes(System.IO.Path.Combine(dir, "cmdline"));
                argv = System.Text.Encoding.UTF8.GetString(raw).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            } catch { continue; }   // the process ended, or it is not ours to read
            var path = SocketPathFromCommandLine(argv, port);
            if (path != null) return path;
        }
        return null;
    }

    /// <summary>The socket path an <c>indiserver</c> command line implies, or null
    /// when it is not indiserver or serves another port. indiserver's value
    /// options are <c>-l -m -d -u -p -r -f</c>; <c>-v</c>, <c>-vv</c> and friends
    /// take none, and the first non-option is the first driver.</summary>
    public static string? SocketPathFromCommandLine(IReadOnlyList<string> argv, int port) {
        if (argv.Count == 0) return null;
        var exe = System.IO.Path.GetFileName(argv[0]);
        if (exe != "indiserver") return null;
        int serverPort = 7624;
        string path = DefaultSocketPath;
        for (int i = 1; i < argv.Count; i++) {
            var arg = argv[i];
            if (arg.Length < 2 || arg[0] != '-') break;         // first driver
            bool takesValue = "lmdupfr".Contains(arg[1]) && arg.Length == 2;
            if (!takesValue) continue;                           // -v, -vv, -vvv
            if (++i >= argv.Count) break;
            if (arg[1] == 'p' && int.TryParse(argv[i], out int p)) serverPort = p;
            if (arg[1] == 'u' && argv[i].Length > 0) path = argv[i];
        }
        return serverPort == port ? path : null;
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
            DiscardPendingDescriptors();
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
