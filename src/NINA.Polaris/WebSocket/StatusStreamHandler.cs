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

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NINA.Polaris.Services;

namespace NINA.Polaris.WebSocket;

public static class StatusStreamHandler {
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);

    // NETUX-1: a send deadline is a disconnect, so it must not be a
    // throughput budget. A full status frame is tens of kilobytes; on a
    // weak WiFi leg (the field report: 65-70% signal at the far end of the
    // garden) draining one frame can take several seconds, and at the old
    // 5 s the host itself closed the socket on the very users whose link
    // was merely slow. That turned "slow" into "disconnected", which is
    // exactly the confusion the link-health work is meant to remove. 30 s
    // still kills a genuinely dead peer, one KeepAliveInterval later.
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOpts = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // PERF #365: serialize the status payload at most once per tick and
    // share the resulting bytes across all connected clients, instead of
    // every client loop building + serializing the full ~50-100 KB payload
    // independently. The only per-client part of the old payload was the
    // debugLog cursor; it becomes a shared cursor here (the frontend dedups
    // log entries by id, so shared delivery is safe — a client that misses
    // a window backfills via GET /api/logs). Keyed by 1-second tick so two
    // clients within the same second reuse one serialization.
    private static readonly object _statusCacheLock = new();
    private static byte[]? _statusCacheBytes;
    private static long _statusCacheTick = -1;
    private static long _sharedDebugCursor;

    public static async Task Handle(HttpContext context) {
        if (!context.WebSockets.IsWebSocketRequest) {
            context.Response.StatusCode = 400;
            return;
        }

        // Accept before resolving anything. This handler used to pull 42
        // services out of the request container first, and anything that threw
        // in there reached the browser as a bare "can't establish a connection
        // to the server": indistinguishable from the network being down or the
        // certificate being refused, which is exactly the ambiguity that made a
        // field report expensive to diagnose. Accept first, and a failure has a
        // real socket to report itself on.
        using var ws = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext {
            KeepAliveInterval = PingInterval
        });

        // Every service the payload needs is a constructor dependency of
        // StatusPayloadBuilder now, so the socket is accepted and this handler
        // stays about the socket. See that class for why the payload itself is
        // still one method.
        var builder = context.RequestServices.GetRequiredService<StatusPayloadBuilder>();
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();

        using var cts = new CancellationTokenSource();

        // NETUX-1: the 1 Hz tick is no longer the only writer on this socket
        // (the pong answers from the receive loop share it), and a WebSocket
        // allows exactly one outstanding send. This gate serialises them.
        using var sendGate = new SemaphoreSlim(1, 1);

        try {
            await SendJsonAsync(ws, new { type = "connected", stream = "status" }, sendGate, cts.Token);
        } catch {
            return;
        }

        var sendTask = Task.Run(async () => {
            while (!cts.Token.IsCancellationRequested && ws.State == WebSocketState.Open) {
                try {
                    // PERF #365: reuse this tick's already-serialized payload
                    // if another client (or this one) built it within the
                    // current 1-second window.
                    long tick = DateTime.UtcNow.Ticks / StatusInterval.Ticks;
                    byte[]? payload = null;
                    long localDebugCursor = 0;
                    lock (_statusCacheLock) {
                        if (_statusCacheTick == tick && _statusCacheBytes != null)
                            payload = _statusCacheBytes;
                        else
                            localDebugCursor = _sharedDebugCursor;
                    }
                    if (payload == null) {
                    var status = builder.Build(ref localDebugCursor);

                    payload = JsonSerializer.SerializeToUtf8Bytes(status, JsonOpts);
                    lock (_statusCacheLock) {
                        _statusCacheBytes = payload;
                        _statusCacheTick = tick;
                        // Advance the shared cursor monotonically; if two
                        // clients raced this tick they both built, take the
                        // furthest. Long read/write under the lock is also
                        // what keeps the cursor torn-free on 32-bit ARM.
                        if (localDebugCursor > _sharedDebugCursor)
                            _sharedDebugCursor = localDebugCursor;
                    }
                    }

                    await SendBytesAsync(ws, payload!, sendGate, cts.Token);
                    await Task.Delay(StatusInterval, cts.Token);
                } catch (OperationCanceledException) {
                    break;
                } catch (WebSocketException) {
                    break;
                } catch (Exception ex) {
                    logger.LogWarning(ex, "Status stream send error");
                    break;
                }
            }
        }, cts.Token);

        try {
            var buffer = new byte[256];
            while (ws.State == WebSocketState.Open) {
                // No artificial receive deadline (see ImageStreamHandler): this
                // stream is a pure consumer, so a CancelAfter here just closed
                // healthy connections every PingInterval*3. Dead peers are
                // caught by KeepAliveInterval + the outer cts.
                var result = await ws.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                // NETUX-1: application-level ping/pong. The browser needs to
                // measure ITS OWN leg of the link, and the WebSocket protocol
                // ping is invisible to page JavaScript. The echo carries the
                // client's own clock value back untouched, so the round trip
                // is computed from a single clock and host/client skew (which
                // is routinely tens of seconds here) cannot poison it.
                if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage)
                    continue;
                await TryAnswerPingAsync(ws, buffer.AsMemory(0, result.Count), sendGate, cts.Token);
            }
        } catch (OperationCanceledException) {
            logger.LogDebug("Status WebSocket receive timed out (client likely disconnected)");
        } catch (WebSocketException) {
            // Client disconnected abruptly
        }

        cts.Cancel();
        try { await sendTask; } catch { }

        await CloseGracefully(ws);
    }

    /// <summary>NETUX-1: answer <c>{"type":"ping","id":N,"t":clientClockMs}</c>
    /// with <c>{"type":"pong","id":N,"t":clientClockMs}</c>. Anything else the
    /// client sends on this socket is ignored, as before.
    ///
    /// <para>The reply is deliberately tiny and skips the status cache: it has
    /// to measure the link, not the payload, so it must not be able to queue
    /// behind a fat frame it is meant to be timing.</para></summary>
    private static async Task TryAnswerPingAsync(System.Net.WebSockets.WebSocket ws,
                                                 ReadOnlyMemory<byte> frame,
                                                 SemaphoreSlim gate, CancellationToken ct) {
        try {
            using var doc = JsonDocument.Parse(frame);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != "ping") return;

            long id = root.TryGetProperty("id", out var idEl)
                      && idEl.TryGetInt64(out var idVal) ? idVal : 0;
            long t = root.TryGetProperty("t", out var tEl)
                     && tEl.TryGetInt64(out var tVal) ? tVal : 0;

            await SendJsonAsync(ws, new { type = "pong", id, t }, gate, ct);
        } catch (JsonException) {
            // Not JSON, or not ours. Silence is the documented behaviour for
            // every other message on this socket.
        } catch (OperationCanceledException) {
            throw;
        } catch (WebSocketException) {
            // The send task's own failure path already tears the socket down.
        }
    }

    private static async Task SendJsonAsync(System.Net.WebSockets.WebSocket ws, object data,
                                            SemaphoreSlim gate, CancellationToken ct) {
        var json = JsonSerializer.Serialize(data, JsonOpts);
        await SendBytesAsync(ws, Encoding.UTF8.GetBytes(json), gate, ct);
    }

    /// <summary>PERF #365: send already-serialized UTF-8 JSON bytes (the
    /// per-tick shared status payload) without re-serializing per client.</summary>
    private static async Task SendBytesAsync(System.Net.WebSockets.WebSocket ws, byte[] utf8Json,
                                             SemaphoreSlim gate, CancellationToken ct) {
        await gate.WaitAsync(ct);
        try {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(SendTimeout);
            await ws.SendAsync(utf8Json, WebSocketMessageType.Text, true, sendCts.Token);
        } finally {
            gate.Release();
        }
    }

    private static async Task CloseGracefully(System.Net.WebSockets.WebSocket ws) {
        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived) {
            try {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", closeCts.Token);
            } catch { }
        }
    }
}