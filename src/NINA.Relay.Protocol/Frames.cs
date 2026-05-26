using System.Buffers.Binary;
using System.Text;

namespace NINA.Relay.Protocol;

/// <summary>
/// Wire protocol used between a N.I.N.A. Polaris instance (the "tunnel client")
/// and the relay server (the "tunnel server"). All frames flow over a single
/// outbound WebSocket the client opens to the server, and use a small fixed
/// binary header so multiple browser HTTP requests can be multiplexed over
/// the one socket without TCP head-of-line blocking from inside the user's
/// network.
///
/// Frame on the wire:
///   [opcode : 1 byte]
///   [streamId : 4 bytes, big-endian]
///   [length : 4 bytes, big-endian]    ← payload length in bytes
///   [payload : N bytes]
///
/// Stream IDs are server-allocated for incoming requests and client-allocated
/// for control replies. Opcodes:
///
///   0x01 Auth         (client → server, payload = tenant token UTF-8)
///   0x02 AuthOk       (server → client, payload = assigned hostname UTF-8)
///   0x03 AuthFail     (server → client, payload = reason UTF-8)
///
///   0x10 HttpRequest  (server → client, payload = serialised HttpRequestFrame)
///   0x11 HttpResponse (client → server, payload = serialised HttpResponseFrame)
///
///   0x20 WsOpen       (server → client, payload = WsOpenFrame)
///   0x21 WsOpenAck    (client → server, payload = empty / error UTF-8)
///   0x22 WsMessage    (both directions, payload = WsMessageFrame)
///   0x23 WsClose      (both directions, payload = optional UTF-8 reason)
///
///   0xF0 Ping         (either direction, payload = empty)
///   0xF1 Pong         (either direction, payload = empty)
/// </summary>
public static class RelayFrame {
    public const byte Auth         = 0x01;
    public const byte AuthOk       = 0x02;
    public const byte AuthFail     = 0x03;
    public const byte HttpRequest  = 0x10;
    public const byte HttpResponse = 0x11;
    public const byte WsOpen       = 0x20;
    public const byte WsOpenAck    = 0x21;
    public const byte WsMessage    = 0x22;
    public const byte WsClose      = 0x23;
    public const byte Ping         = 0xF0;
    public const byte Pong         = 0xF1;

    public const int HeaderSize = 9;

    public static byte[] Build(byte opcode, uint streamId, ReadOnlySpan<byte> payload) {
        var buf = new byte[HeaderSize + payload.Length];
        buf[0] = opcode;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(1, 4), streamId);
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(5, 4), payload.Length);
        payload.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    public static byte[] Build(byte opcode, uint streamId, string utf8Payload) =>
        Build(opcode, streamId, Encoding.UTF8.GetBytes(utf8Payload));

    public static byte[] BuildEmpty(byte opcode, uint streamId = 0) =>
        Build(opcode, streamId, ReadOnlySpan<byte>.Empty);

    public static (byte opcode, uint streamId, ReadOnlyMemory<byte> payload) Parse(ReadOnlyMemory<byte> frame) {
        if (frame.Length < HeaderSize) throw new InvalidDataException("frame too short");
        var span = frame.Span;
        byte op = span[0];
        uint sid = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(1, 4));
        int len = BinaryPrimitives.ReadInt32BigEndian(span.Slice(5, 4));
        if (len < 0 || HeaderSize + len > frame.Length)
            throw new InvalidDataException($"frame length {len} mismatched");
        return (op, sid, frame.Slice(HeaderSize, len));
    }
}

/// <summary>
/// Serialised HTTP request flowing from the relay server to the tunnel
/// client. The client replays it against its local Kestrel.
///
/// Wire format (UTF-8 textual header block, then binary body):
///   line 1: "<METHOD> <path-and-query>"
///   line 2..N: "<Header-Name>: <value>"   (zero or more)
///   blank line
///   raw body bytes
/// </summary>
public static class HttpRequestFrame {
    public static byte[] Serialise(string method, string pathAndQuery,
        IEnumerable<KeyValuePair<string, string>> headers, ReadOnlyMemory<byte> body) {
        var sb = new StringBuilder();
        sb.Append(method).Append(' ').Append(pathAndQuery).Append("\r\n");
        foreach (var h in headers) {
            // Skip hop-by-hop headers that don't make sense over a relay
            if (IsHopByHop(h.Key)) continue;
            sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
        }
        sb.Append("\r\n");
        var header = Encoding.UTF8.GetBytes(sb.ToString());
        var buf = new byte[header.Length + body.Length];
        header.CopyTo(buf, 0);
        body.Span.CopyTo(buf.AsSpan(header.Length));
        return buf;
    }

    public static (string method, string pathAndQuery,
        List<KeyValuePair<string, string>> headers, ReadOnlyMemory<byte> body) Parse(ReadOnlyMemory<byte> payload) {
        // Find the blank line separating header from body
        var span = payload.Span;
        int sep = IndexOfDoubleCrLf(span);
        if (sep < 0) throw new InvalidDataException("no header/body separator");

        var headerText = Encoding.UTF8.GetString(span.Slice(0, sep));
        var lines = headerText.Split("\r\n");
        if (lines.Length < 1) throw new InvalidDataException("empty request");

        var requestLine = lines[0];
        var firstSpace = requestLine.IndexOf(' ');
        if (firstSpace < 0) throw new InvalidDataException("bad request line");
        var method = requestLine[..firstSpace];
        var path = requestLine[(firstSpace + 1)..];

        var headers = new List<KeyValuePair<string, string>>();
        for (int i = 1; i < lines.Length; i++) {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            headers.Add(new KeyValuePair<string, string>(
                lines[i][..colon].Trim(), lines[i][(colon + 1)..].Trim()));
        }

        return (method, path, headers, payload.Slice(sep + 4));
    }

    private static int IndexOfDoubleCrLf(ReadOnlySpan<byte> span) {
        for (int i = 0; i <= span.Length - 4; i++) {
            if (span[i] == 13 && span[i + 1] == 10 && span[i + 2] == 13 && span[i + 3] == 10) return i;
        }
        return -1;
    }

    private static bool IsHopByHop(string name) {
        return name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("TE", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Host", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// WebSocket open request (relay → client). Payload is just the path-and-query
/// string the browser hit. The client opens a local WebSocket against
/// http://127.0.0.1:5000&lt;path&gt; and reports back with WsOpenAck.
/// Subprotocols are not negotiated through the tunnel, keep WS endpoints
/// simple (no Sec-WebSocket-Protocol).
/// </summary>
public static class WsOpenFrame {
    public static byte[] Serialise(string pathAndQuery) =>
        System.Text.Encoding.UTF8.GetBytes(pathAndQuery);

    public static string Parse(ReadOnlyMemory<byte> payload) =>
        System.Text.Encoding.UTF8.GetString(payload.Span);
}

/// <summary>
/// Single WebSocket message (either direction). 1-byte type prefix:
///   0x01 = Text   (UTF-8 payload)
///   0x02 = Binary
/// </summary>
public static class WsMessageFrame {
    public const byte TypeText = 0x01;
    public const byte TypeBinary = 0x02;

    public static byte[] Serialise(byte messageType, ReadOnlyMemory<byte> body) {
        var buf = new byte[1 + body.Length];
        buf[0] = messageType;
        body.Span.CopyTo(buf.AsSpan(1));
        return buf;
    }

    public static (byte type, ReadOnlyMemory<byte> body) Parse(ReadOnlyMemory<byte> payload) {
        if (payload.Length < 1) throw new InvalidDataException("WS message frame too short");
        return (payload.Span[0], payload.Slice(1));
    }
}

/// <summary>HTTP response: status line + headers + body, same shape as request.</summary>
public static class HttpResponseFrame {
    public static byte[] Serialise(int status,
        IEnumerable<KeyValuePair<string, string>> headers, ReadOnlyMemory<byte> body) {
        var sb = new StringBuilder();
        sb.Append(status.ToString()).Append("\r\n");
        foreach (var h in headers) {
            sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
        }
        sb.Append("\r\n");
        var header = Encoding.UTF8.GetBytes(sb.ToString());
        var buf = new byte[header.Length + body.Length];
        header.CopyTo(buf, 0);
        body.Span.CopyTo(buf.AsSpan(header.Length));
        return buf;
    }

    public static (int status, List<KeyValuePair<string, string>> headers, ReadOnlyMemory<byte> body)
        Parse(ReadOnlyMemory<byte> payload) {
        var span = payload.Span;
        int sep = FindDoubleCrLf(span);
        if (sep < 0) throw new InvalidDataException("no header/body separator");
        var headerText = Encoding.UTF8.GetString(span.Slice(0, sep));
        var lines = headerText.Split("\r\n");
        if (lines.Length < 1 || !int.TryParse(lines[0], out var status))
            throw new InvalidDataException("bad status line");

        var headers = new List<KeyValuePair<string, string>>();
        for (int i = 1; i < lines.Length; i++) {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            headers.Add(new KeyValuePair<string, string>(
                lines[i][..colon].Trim(), lines[i][(colon + 1)..].Trim()));
        }
        return (status, headers, payload.Slice(sep + 4));
    }

    private static int FindDoubleCrLf(ReadOnlySpan<byte> span) {
        for (int i = 0; i <= span.Length - 4; i++) {
            if (span[i] == 13 && span[i + 1] == 10 && span[i + 2] == 13 && span[i + 3] == 10) return i;
        }
        return -1;
    }
}
