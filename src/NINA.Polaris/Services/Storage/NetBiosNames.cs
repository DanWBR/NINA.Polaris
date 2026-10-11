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

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NINA.Polaris.Services.Storage;

/// <summary>
/// The two NetBIOS name service questions (RFC 1002) the SMB push needs to
/// find a file server again after its address changes: "what is your name?"
/// asked of an address (node status), and "who has this name?" asked of the
/// whole subnet (broadcast name query). Windows answers both out of the box,
/// as do Samba servers and most NAS boxes, with nothing to install on the rig.
///
/// <para>Why: a share saved by IP stops working the moment DHCP hands the
/// server another address, which on a field network whose uplink drops (a
/// Starlink outage resets the router) happens every time. The name does not
/// change, so it is what gets remembered.</para>
/// </summary>
public static class NetBiosNames {
    private const int Port = 137;

    /// <summary>The NetBIOS name of the machine at <paramref name="address"/>,
    /// or null when it does not answer.</summary>
    public static async Task<string?> GetNameAsync(IPAddress address, TimeSpan timeout, CancellationToken ct = default) {
        var id = (ushort)Random.Shared.Next(1, 0xFFFF);
        var reply = await AskAsync(BuildNodeStatusRequest(id), new IPEndPoint(address, Port), broadcast: false, id, timeout, ct);
        return reply == null ? null : ParseNodeStatusResponse(reply);
    }

    /// <summary>The IPv4 address answering for <paramref name="name"/> on the
    /// local subnet, or null when nobody does.</summary>
    public static async Task<IPAddress?> ResolveAsync(string name, TimeSpan timeout, CancellationToken ct = default) {
        var id = (ushort)Random.Shared.Next(1, 0xFFFF);
        var reply = await AskAsync(BuildNameQuery(id, name), new IPEndPoint(IPAddress.Broadcast, Port), broadcast: true, id, timeout, ct);
        return reply == null ? null : ParseNameQueryResponse(reply);
    }

    private static async Task<byte[]?> AskAsync(byte[] packet, IPEndPoint to, bool broadcast, ushort id,
            TimeSpan timeout, CancellationToken ct) {
        try {
            using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = broadcast };
            await udp.SendAsync(packet, to, ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            while (true) {
                var r = await udp.ReceiveAsync(cts.Token);
                if (r.Buffer.Length >= 12 && ((r.Buffer[0] << 8) | r.Buffer[1]) == id
                    && (r.Buffer[2] & 0x80) != 0)
                    return r.Buffer;
            }
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            return null;   // nobody answered in time
        } catch (SocketException) {
            return null;
        }
    }

    // ---- packets ----

    /// <summary>RFC 1002 first-level encoding: 16 bytes, each split into two
    /// nibbles written as 'A'..'P', length-prefixed and root-terminated.</summary>
    internal static byte[] EncodeName(byte[] raw16) {
        var o = new byte[34];
        o[0] = 32;
        for (int i = 0; i < 16; i++) {
            o[1 + 2 * i] = (byte)('A' + (raw16[i] >> 4));
            o[2 + 2 * i] = (byte)('A' + (raw16[i] & 0x0F));
        }
        o[33] = 0;
        return o;
    }

    /// <summary>A name as NetBIOS stores it: upper case, padded with spaces to
    /// fifteen characters, then the service suffix byte.</summary>
    internal static byte[] RawName(string name, byte suffix) {
        var raw = new byte[16];
        var up = Encoding.ASCII.GetBytes(name.Trim().ToUpperInvariant());
        for (int i = 0; i < 15; i++) raw[i] = i < up.Length ? up[i] : (byte)' ';
        raw[15] = suffix;
        return raw;
    }

    internal static byte[] BuildNodeStatusRequest(ushort id) {
        var raw = new byte[16];
        raw[0] = (byte)'*';   // wildcard, the rest zero
        return Packet(id, flags: 0x0000, EncodeName(raw), qtype: 0x0021);
    }

    internal static byte[] BuildNameQuery(ushort id, string name)
        // 0x0110: recursion desired + broadcast. Suffix 0x20 is the file
        // server service, which is the one an SMB host registers.
        => Packet(id, flags: 0x0110, EncodeName(RawName(name, 0x20)), qtype: 0x0020);

    private static byte[] Packet(ushort id, ushort flags, byte[] encodedName, ushort qtype) {
        var p = new byte[12 + encodedName.Length + 4];
        p[0] = (byte)(id >> 8); p[1] = (byte)id;
        p[2] = (byte)(flags >> 8); p[3] = (byte)flags;
        p[5] = 1;   // one question
        Buffer.BlockCopy(encodedName, 0, p, 12, encodedName.Length);
        int q = 12 + encodedName.Length;
        p[q] = (byte)(qtype >> 8); p[q + 1] = (byte)qtype;
        p[q + 3] = 1;   // class IN
        return p;
    }

    /// <summary>The machine's own name out of a node status answer: the
    /// unique file server entry (suffix 0x20) if listed, else the unique
    /// workstation entry (suffix 0x00).</summary>
    internal static string? ParseNodeStatusResponse(byte[] r) {
        try {
            if (AnswerCount(r) < 1) return null;
            int i = SkipName(r, 12);          // the answer repeats the question's name
            i += 2 + 2 + 4;                   // type, class, ttl
            int rdLength = (r[i] << 8) | r[i + 1];
            i += 2;
            if (rdLength < 1 || i + rdLength > r.Length) return null;
            int count = r[i++];
            string? workstation = null;
            for (int n = 0; n < count && i + 18 <= r.Length; n++, i += 18) {
                var name = Encoding.ASCII.GetString(r, i, 15).TrimEnd(' ', '\0');
                var suffix = r[i + 15];
                bool group = (r[i + 16] & 0x80) != 0;
                if (group || name.Length == 0) continue;
                if (suffix == 0x20) return name;
                if (suffix == 0x00) workstation ??= name;
            }
            return workstation;
        } catch (IndexOutOfRangeException) {
            return null;
        }
    }

    /// <summary>The first address out of a positive name query answer.</summary>
    internal static IPAddress? ParseNameQueryResponse(byte[] r) {
        try {
            if ((r[3] & 0x0F) != 0 || AnswerCount(r) < 1) return null;   // rcode must be 0
            int i = SkipName(r, 12);
            i += 2 + 2 + 4;
            int rdLength = (r[i] << 8) | r[i + 1];
            i += 2;
            if (rdLength < 6 || i + 6 > r.Length) return null;
            return new IPAddress(new[] { r[i + 2], r[i + 3], r[i + 4], r[i + 5] });   // after 2 flag bytes
        } catch (IndexOutOfRangeException) {
            return null;
        }
    }

    private static int AnswerCount(byte[] r) => r.Length < 12 ? 0 : (r[6] << 8) | r[7];

    private static int SkipName(byte[] r, int i) {
        while (true) {
            int len = r[i];
            if (len == 0) return i + 1;
            if ((len & 0xC0) == 0xC0) return i + 2;   // compression pointer
            i += 1 + len;
        }
    }
}
