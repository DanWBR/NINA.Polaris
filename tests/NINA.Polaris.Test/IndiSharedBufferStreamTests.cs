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

using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using NINA.INDI.Client;
using NINA.INDI.Protocol;

namespace NINA.Polaris.Test;

/// <summary>Plays indiserver's side of the Unix socket: a <c>setBLOBVector</c>
/// whose <c>oneBLOB</c> is <c>attached="true"</c> and whose bytes travel as a
/// memfd in <c>SCM_RIGHTS</c> ancillary data on the same <c>sendmsg</c>.</summary>
[TestFixture]
[Platform("Linux")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public class IndiSharedBufferStreamTests {

    [Test]
    public async Task AttachedBlob_ArrivesThroughTheParser_ByteForByte() {
        var (server, client) = SocketPair();
        using var stream = IndiSharedBufferStream.FromConnectedSocket(client);
        var parser = new IndiXmlParser { AttachedBlobSource = stream.TakeAttachedBlob };
        var payload = new byte[300_001];
        new Random(7).NextBytes(payload);

        IndiBlobProperty? received = null;
        parser.PropertyUpdated += p => { if (p is IndiBlobProperty b) received = b; };

        using var fd = Memfd(payload);
        string xml = $"<setBLOBVector device=\"GPhoto CCD\" name=\"CCD1\" state=\"Ok\">" +
                     $"<oneBLOB name=\"CCD1\" size=\"{payload.Length}\" format=\".arw\" attached=\"true\"/></setBLOBVector>\n";
        SendWithFd(server, Encoding.UTF8.GetBytes(xml), (int)fd.DangerousGetHandle());
        server.Shutdown(SocketShutdown.Both);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await parser.ParseStreamAsync(stream, cts.Token);

        Assert.That(received, Is.Not.Null);
        var element = received!.Values["CCD1"];
        Assert.That(element.Format, Is.EqualTo(".arw"));
        Assert.That(element.Size, Is.EqualTo(payload.Length));
        Assert.That(element.Data, Is.EqualTo(payload), "the shared buffer is the frame, nothing re-encoded");
        Assert.That(stream.PendingAttachedBlobs, Is.EqualTo(0), "the descriptor was consumed");
        server.Dispose();
    }

    [Test]
    public async Task InlineBase64_StillWorks_OnTheSameStream() {
        var (server, client) = SocketPair();
        using var stream = IndiSharedBufferStream.FromConnectedSocket(client);
        var parser = new IndiXmlParser { AttachedBlobSource = stream.TakeAttachedBlob };
        var payload = Encoding.ASCII.GetBytes("inline frame");
        IndiBlobProperty? received = null;
        parser.PropertyUpdated += p => { if (p is IndiBlobProperty b) received = b; };

        string xml = $"<setBLOBVector device=\"d\" name=\"CCD1\" state=\"Ok\"><oneBLOB name=\"CCD1\" size=\"{payload.Length}\" " +
                     $"format=\".fits\" enclen=\"{Convert.ToBase64String(payload).Length}\">{Convert.ToBase64String(payload)}</oneBLOB></setBLOBVector>\n";
        server.Send(Encoding.UTF8.GetBytes(xml));
        server.Shutdown(SocketShutdown.Both);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await parser.ParseStreamAsync(stream, cts.Token);

        Assert.That(received?.Values["CCD1"].Data, Is.EqualTo(payload));
        server.Dispose();
    }

    [Test]
    public void TwoAttachedBlobs_InOneMessage_KeepTheirOrder() {
        var (server, client) = SocketPair();
        using var stream = IndiSharedBufferStream.FromConnectedSocket(client);
        using var a = Memfd(new byte[] { 1, 1, 1 });
        using var b = Memfd(new byte[] { 2, 2 });
        SendWithFds(server, Encoding.UTF8.GetBytes("<x/>\n"), new[] { (int)a.DangerousGetHandle(), (int)b.DangerousGetHandle() });

        var buf = new byte[64];
        int n = stream.Read(buf, 0, buf.Length);

        Assert.That(n, Is.EqualTo(5));
        Assert.That(stream.PendingAttachedBlobs, Is.EqualTo(2));
        Assert.That(stream.TakeAttachedBlob(3), Is.EqualTo(new byte[] { 1, 1, 1 }));
        Assert.That(stream.TakeAttachedBlob(2), Is.EqualTo(new byte[] { 2, 2 }));
        Assert.That(stream.TakeAttachedBlob(1), Is.Null, "no descriptor left");
        server.Dispose();
    }

    /// <summary>What the live connection looks like: .NET makes the descriptor
    /// non-blocking once ConnectAsync has run on it, so recvmsg returns EAGAIN
    /// until data is there. On the Pi this surfaced as "recvmsg failed, errno 11"
    /// right after connecting (0.98.267-henosys3, 2026-11-15).</summary>
    [Test]
    public async Task NonBlockingSocket_WaitsForData_InsteadOfFailing() {
        var (server, client) = SocketPair();
        client.Blocking = false;
        using var stream = IndiSharedBufferStream.FromConnectedSocket(client);
        var buf = new byte[64];

        var read = Task.Run(() => stream.Read(buf, 0, buf.Length));
        await Task.Delay(300);
        Assert.That(read.IsCompleted, Is.False, "nothing sent yet, the read must wait");

        using var fd = Memfd(new byte[] { 9, 9 });
        SendWithFd(server, Encoding.UTF8.GetBytes("<x/>\n"), (int)fd.DangerousGetHandle());

        int n = await read.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(n, Is.EqualTo(5));
        Assert.That(stream.TakeAttachedBlob(2), Is.EqualTo(new byte[] { 9, 9 }));

        var second = Task.Run(() => stream.Read(buf, 0, buf.Length));
        await Task.Delay(100);
        server.Shutdown(SocketShutdown.Both);
        Assert.That(await second.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(0), "peer closed: EOF, not an exception");
        server.Dispose();
    }

    [Test]
    public void AttachedWithoutDescriptor_YieldsNull_NotGarbage() {
        var (server, client) = SocketPair();
        using var stream = IndiSharedBufferStream.FromConnectedSocket(client);
        Assert.That(stream.TakeAttachedBlob(10), Is.Null);
        server.Dispose();
    }

    [Test]
    public async Task TryConnect_ToNothing_ReturnsNull() {
        var s = await IndiSharedBufferStream.TryConnectAsync("/tmp/polaris-test-no-such-indiserver-" + Guid.NewGuid().ToString("N"),
            TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.That(s, Is.Null);
    }

    // --- libc helpers for the server side ---

    private static (Socket server, Socket client) SocketPair() {
        var fds = new int[2];
        if (socketpair(1 /* AF_UNIX */, 1 /* SOCK_STREAM */, 0, fds) != 0)
            Assert.Fail("socketpair failed, errno " + Marshal.GetLastPInvokeError());
        return (new Socket(new SafeSocketHandle((IntPtr)fds[0], ownsHandle: true)),
                new Socket(new SafeSocketHandle((IntPtr)fds[1], ownsHandle: true)));
    }

    private static SafeFileHandle Memfd(byte[] content) {
        int fd = memfd_create("polaris-test-blob", 0);
        Assert.That(fd, Is.GreaterThanOrEqualTo(0), "memfd_create failed, errno " + Marshal.GetLastPInvokeError());
        var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        RandomAccess.Write(handle, content, 0);
        return handle;
    }

    private static void SendWithFd(Socket s, byte[] data, int fd) => SendWithFds(s, data, new[] { fd });

    private static unsafe void SendWithFds(Socket s, byte[] data, int[] fds) {
        int cmsgLen = 16 + fds.Length * sizeof(int);                 // CMSG_LEN on LP64
        int space = 16 + ((fds.Length * sizeof(int) + 7) & ~7);      // CMSG_SPACE
        byte* control = stackalloc byte[space];
        new Span<byte>(control, space).Clear();
        *(nuint*)control = (nuint)cmsgLen;
        *(int*)(control + 8) = 1;                                    // SOL_SOCKET
        *(int*)(control + 12) = 1;                                   // SCM_RIGHTS
        for (int i = 0; i < fds.Length; i++) *(int*)(control + 16 + i * sizeof(int)) = fds[i];
        fixed (byte* p = data) {
            var iov = new iovec { iov_base = (IntPtr)p, iov_len = (nuint)data.Length };
            var msg = new msghdr { msg_iov = (IntPtr)(&iov), msg_iovlen = 1, msg_control = (IntPtr)control, msg_controllen = (nuint)space };
            nint n = sendmsg((int)s.Handle, &msg, 0);
            Assert.That((int)n, Is.EqualTo(data.Length), "sendmsg failed, errno " + Marshal.GetLastPInvokeError());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct iovec { public IntPtr iov_base; public nuint iov_len; }

    [StructLayout(LayoutKind.Sequential)]
    private struct msghdr {
        public IntPtr msg_name; public uint msg_namelen;
        public IntPtr msg_iov; public nuint msg_iovlen;
        public IntPtr msg_control; public nuint msg_controllen;
        public int msg_flags;
    }

    [DllImport("libc", SetLastError = true)] private static extern int socketpair(int domain, int type, int protocol, int[] sv);
    [DllImport("libc", SetLastError = true)] private static extern int memfd_create(string name, uint flags);
    [DllImport("libc", SetLastError = true)] private static extern unsafe nint sendmsg(int sockfd, msghdr* msg, int flags);
}
