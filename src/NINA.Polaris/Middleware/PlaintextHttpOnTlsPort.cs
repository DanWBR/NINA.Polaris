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

using System.Buffers;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace NINA.Polaris.Middleware;

/// <summary>
/// Issue #14: port 5000 speaks HTTPS only. A browser sent to
/// <c>http://host:5000/</c> (typed by hand, or from an old bookmark) opens a
/// TCP connection that the TLS listener cannot make sense of, so Kestrel
/// closes it and the browser shows ERR_EMPTY_RESPONSE / "Empty reply from
/// server" — while a phone that opened the app over https works on the same
/// hotspot, which looks like a network fault when it is only the scheme.
///
/// This connection middleware runs BEFORE the TLS handshake on the HTTPS
/// listener, peeks at the first bytes, and when they are a plaintext HTTP
/// request line it answers with a 301 to the same URL over https and closes.
/// Anything else (a TLS ClientHello starts with 0x16) is left untouched for
/// the TLS handshake.
/// </summary>
public static class PlaintextHttpOnTlsPort {
    private static readonly string[] Methods = { "GET ", "HEAD ", "POST ", "PUT ", "DELETE ", "OPTIONS ", "PATCH " };
    private const int MaxPeek = 4096;

    public static void Register(ListenOptions listen, int httpsPort) {
        listen.Use(next => async ctx => {
            var input = ctx.Transport.Input;
            var read = await input.ReadAsync(ctx.ConnectionClosed);
            var buffer = read.Buffer;
            string fallbackHost = ctx.LocalEndPoint is System.Net.IPEndPoint ep
                ? (ep.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ep.Address}]" : ep.Address.ToString())
                : "localhost";
            var verdict = Inspect(buffer, httpsPort, fallbackHost, out var location);
            if (verdict == Verdict.Plaintext) {
                // consume the request; we are not handing this connection on
                input.AdvanceTo(buffer.End);
                var response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 301 Moved Permanently\r\n" +
                    $"Location: {location}\r\n" +
                    "Content-Type: text/html; charset=utf-8\r\n" +
                    "Connection: close\r\n" +
                    "Content-Length: 0\r\n\r\n");
                await ctx.Transport.Output.WriteAsync(response, ctx.ConnectionClosed);
                await ctx.Transport.Output.CompleteAsync();
                return;
            }
            // leave every byte for TLS (examined so a second ReadAsync waits for more)
            input.AdvanceTo(buffer.Start, verdict == Verdict.NeedMore ? buffer.End : buffer.Start);
            await next(ctx);
        });
    }

    public enum Verdict { Tls, Plaintext, NeedMore }

    /// <summary>Classifies the first bytes of a connection. A TLS record
    /// starts with 0x16; a plaintext request starts with an HTTP method. The
    /// redirect target is rebuilt from the request line and the Host header
    /// (host only; the port is replaced by the HTTPS port).</summary>
    public static Verdict Inspect(ReadOnlySequence<byte> buffer, int httpsPort, string fallbackHost, out string location) {
        location = "";
        if (buffer.Length == 0) return Verdict.NeedMore;
        var first = buffer.FirstSpan.Length > 0 ? buffer.FirstSpan[0] : buffer.Slice(0, 1).ToArray()[0];
        if (first == 0x16 || first < 0x20 || first > 0x7e) return Verdict.Tls;   // not ASCII text: not HTTP
        int len = (int)Math.Min(buffer.Length, MaxPeek);
        var text = Encoding.ASCII.GetString(buffer.Slice(0, len).ToArray());
        bool isMethod = false;
        foreach (var m in Methods) {
            if (text.StartsWith(m, StringComparison.Ordinal)) { isMethod = true; break; }
            if (m.StartsWith(text, StringComparison.Ordinal)) return Verdict.NeedMore;   // "GE" so far
        }
        if (!isMethod) return Verdict.Tls;
        int eol = text.IndexOf("\r\n", StringComparison.Ordinal);
        if (eol < 0) return len >= MaxPeek ? Verdict.Tls : Verdict.NeedMore;
        var parts = text[..eol].Split(' ');
        string path = parts.Length >= 2 && parts[1].StartsWith('/') ? parts[1] : "/";
        string host = "";
        foreach (var line in text[(eol + 2)..].Split("\r\n")) {
            if (line.Length == 0) break;
            if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) {
                host = line[5..].Trim();
                int colon = host.LastIndexOf(':');
                if (colon > 0 && !host.EndsWith(']')) host = host[..colon];   // strip :port, keep [v6]
                break;
            }
        }
        if (host.Length == 0) {
            // no Host header yet: wait for the rest of the headers unless they
            // are complete (or too long), then fall back to the address the
            // client actually connected to
            if (!text.Contains("\r\n\r\n", StringComparison.Ordinal) && len < MaxPeek) return Verdict.NeedMore;
            host = fallbackHost;
        }
        location = $"https://{host}:{httpsPort}{path}";
        return Verdict.Plaintext;
    }
}
