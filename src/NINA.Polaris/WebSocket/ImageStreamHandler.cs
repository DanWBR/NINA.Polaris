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

public static class ImageStreamHandler {
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    public static async Task Handle(HttpContext context) {
        if (!context.WebSockets.IsWebSocketRequest) {
            context.Response.StatusCode = 400;
            return;
        }

        // Accept first, resolve after: same reasoning as StatusStreamHandler.
        // Anything that fails before the 101 is reported to the browser as a
        // plain "can't establish a connection", which says nothing about why.
        using var ws = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext {
            KeepAliveInterval = PingInterval
        });

        var relay = context.RequestServices.GetRequiredService<ImageRelayService>();
        var liveStack = context.RequestServices.GetRequiredService<LiveStackingService>();
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();

        var clientId = Guid.NewGuid().ToString("N");
        relay.RegisterClient(clientId, ws);

        try {
            // Send welcome message. FIELD-3: streaming is RAW-only
            // now (the JPEG WS path was deleted because it baked
            // AutoStretch into the JPEG server-side, neutering the
            // operator's Stretch / WB controls). Keep the `modes`
            // array shape so older clients keep parsing the message,
            // but advertise only "raw".
            using var welcomeCts = new CancellationTokenSource(SendTimeout);
            var welcome = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
                type = "connected",
                stream = "image",
                clientId,
                modes = new[] { "raw" },
                defaultMode = "raw"
            }));
            await ws.SendAsync(welcome, WebSocketMessageType.Text, true, welcomeCts.Token);

            var buffer = new byte[1024];
            while (ws.State == WebSocketState.Open) {
                try {
                    // NO artificial receive deadline. This stream is a pure
                    // CONSUMER: after the handshake the browser never sends
                    // anything again, and WebSocket ping/pong are control
                    // frames handled below ReceiveAsync — so a receive timeout
                    // guillotined EVERY connection on schedule (PingInterval*3
                    // = 90 s, matching the observed "CONNECT /ws/image-stream
                    // 200 90013.1ms"). That killed whatever frame was in flight
                    // and surfaced as the misleading "Send to client timed out",
                    // leaving the browser stuck on the last frame that made it.
                    // Dead peers are still detected: KeepAliveInterval pings
                    // above, plus RequestAborted when the client goes away.
                    var result = await ws.ReceiveAsync(buffer, context.RequestAborted);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    // Handle text messages for mode switching
                    if (result.MessageType == WebSocketMessageType.Text && result.Count > 0) {
                        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        HandleClientMessage(relay, liveStack, clientId, text, logger);
                    }
                } catch (OperationCanceledException) {
                    logger.LogDebug("Image stream client {Id} timed out", clientId);
                    break;
                } catch (WebSocketException) {
                    break;
                }
            }
        } finally {
            relay.UnregisterClient(clientId);
            await CloseGracefully(ws);
        }
    }

    private static void HandleClientMessage(ImageRelayService relay, LiveStackingService liveStack, string clientId, string text, ILogger logger) {
        try {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (root.TryGetProperty("mode", out var modeProp)) {
                // FIELD-3: every client is forced to Raw now. We still
                // accept the mode message so cached older clients that
                // send {"mode":"jpeg"} on connect don't get a parse
                // error -- relay.SetClientMode silently coerces Jpeg
                // requests to Raw with a debug log.
                var mode = modeProp.GetString()?.ToLowerInvariant() switch {
                    "raw" => ImageRelayService.StreamMode.Raw,
                    _ => ImageRelayService.StreamMode.Jpeg
                };
                relay.SetClientMode(clientId, mode);
                return;
            }

            // Discriminated messages, capability handshake + per-
            // frame metrics from the client-side WASM stacker. CLST-5.
        } catch (JsonException) {
            // Ignore malformed messages
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