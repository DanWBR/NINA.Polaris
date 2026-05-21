using System.Net.WebSockets;
using System.Text;
using NINA.Relay.Protocol;

namespace NINA.Relay.Server;

/// <summary>
/// Handles the lifetime of one inbound tunnel WebSocket: authentication,
/// the receive loop that demultiplexes responses + pongs, periodic pings,
/// and cleanup on close.
///
/// Sends are funnelled through <see cref="TenantTunnel.SendLock"/> so two
/// concurrent proxied requests can't interleave their frames on the wire.
/// </summary>
public class TunnelHandler {
    private readonly TenantRegistry _registry;
    private readonly TenantUsageStore _usage;
    private readonly ILogger<TunnelHandler> _logger;

    public TunnelHandler(TenantRegistry registry, TenantUsageStore usage, ILogger<TunnelHandler> logger) {
        _registry = registry;
        _usage = usage;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext ctx) {
        if (!ctx.WebSockets.IsWebSocketRequest) {
            ctx.Response.StatusCode = 400;
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);

        // Snapshot the presented client cert (if any) before the WS upgrade
        // loses easy access to it via the connection feature.
        var clientCert = ctx.Connection.ClientCertificate;

        // ---- 1. Receive the Auth frame ----
        var first = await ReadFrameAsync(ws, lifetime.Token);
        if (first == null) return;
        var (op, _, payload) = RelayFrame.Parse(first.Value);
        if (op != RelayFrame.Auth) {
            await SendAsync(ws, RelayFrame.Build(RelayFrame.AuthFail, 0, "Expected Auth frame"), CancellationToken.None);
            return;
        }
        var token = Encoding.UTF8.GetString(payload.Span).Trim();
        if (!_registry.TryAuthenticate(token, out var hostname, out var config, out var reject)) {
            _logger.LogWarning("Tunnel auth rejected for token prefix {Prefix}: {Reason}", Truncate(token, 8), reject);
            await SendAsync(ws, RelayFrame.Build(RelayFrame.AuthFail, 0, reject ?? "Auth failed"), CancellationToken.None);
            return;
        }

        // mTLS check: if this tenant pinned a client-cert thumbprint, the cert
        // presented during the TLS handshake must match. SHA-1 thumbprints
        // (X509.Thumbprint) compared case-insensitively, stripping spaces/colons.
        if (config != null && !string.IsNullOrWhiteSpace(config.ClientCertThumbprint)) {
            var expected = NormaliseThumbprint(config.ClientCertThumbprint);
            var presented = clientCert is null ? null : NormaliseThumbprint(clientCert.Thumbprint);
            if (presented == null) {
                _logger.LogWarning("Tunnel auth rejected for {Host}: tenant requires client cert but none presented", hostname);
                await SendAsync(ws, RelayFrame.Build(RelayFrame.AuthFail, 0,
                    "Tenant requires client certificate (mTLS) but none was presented"), CancellationToken.None);
                return;
            }
            if (!string.Equals(presented, expected, StringComparison.OrdinalIgnoreCase)) {
                _logger.LogWarning("Tunnel auth rejected for {Host}: client cert thumbprint mismatch", hostname);
                await SendAsync(ws, RelayFrame.Build(RelayFrame.AuthFail, 0,
                    "Client certificate thumbprint does not match the tenant's pinned cert"), CancellationToken.None);
                return;
            }
        }

        // Block at the door if the tenant has already burned through this
        // month's byte quota — saves us holding an open tunnel that would
        // 402 on every proxied request.
        if (config != null && config.MonthlyBytes > 0) {
            var used = _usage.BytesThisMonth(token);
            if (used >= config.MonthlyBytes) {
                _logger.LogWarning("Tunnel auth rejected for {Host}: monthly quota exhausted ({Used}/{Quota} bytes)",
                    hostname, used, config.MonthlyBytes);
                await SendAsync(ws, RelayFrame.Build(RelayFrame.AuthFail, 0,
                    $"Monthly transfer quota exhausted ({used:N0} / {config.MonthlyBytes:N0} bytes)"),
                    CancellationToken.None);
                return;
            }
        }

        var limiter = config != null && (config.RequestsPerSecond > 0 || config.BytesPerSecond > 0)
            ? new TenantRateLimiter(config)
            : null;
        var tunnel = new TenantTunnel(token, hostname, ws, limiter, config);
        _registry.TryRegister(tunnel);
        _logger.LogInformation("Tunnel registered: {Hostname} (token prefix {Prefix})", hostname, Truncate(token, 8));
        await SendAsync(ws, RelayFrame.Build(RelayFrame.AuthOk, 0, hostname), CancellationToken.None);

        // ---- 2. Receive loop ----
        try {
            // Periodic pings to keep NATs alive
            using var pingTimer = new Timer(_ => {
                _ = TryPingAsync(tunnel);
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

            while (ws.State == WebSocketState.Open && !lifetime.IsCancellationRequested) {
                var frame = await ReadFrameAsync(ws, lifetime.Token);
                if (frame == null) break;
                var (rop, rsid, rpayload) = RelayFrame.Parse(frame.Value);
                HandleFrame(tunnel, rop, rsid, rpayload);
            }
        } catch (OperationCanceledException) { /* expected on shutdown */ }
          catch (WebSocketException ex) {
            _logger.LogInformation("Tunnel {Host} dropped: {Msg}", hostname, ex.Message);
        } finally {
            _registry.Unregister(tunnel);
            try {
                if (ws.State == WebSocketState.Open) {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", closeCts.Token);
                }
            } catch { }
            _logger.LogInformation("Tunnel unregistered: {Host}", hostname);
        }
    }

    private void HandleFrame(TenantTunnel tunnel, byte op, uint sid, ReadOnlyMemory<byte> payload) {
        switch (op) {
            case RelayFrame.HttpResponse:
                try {
                    var (status, headers, body) = HttpResponseFrame.Parse(payload);
                    tunnel.CompleteResponse(sid, new ResponseMessage(status, headers, body.ToArray()));
                } catch (Exception ex) {
                    _logger.LogWarning(ex, "Bad HttpResponse on stream {Sid}", sid);
                    tunnel.CompleteResponse(sid, new ResponseMessage(502, new(), Array.Empty<byte>()));
                }
                break;
            case RelayFrame.WsOpenAck:
                var reason = payload.Length > 0 ? Encoding.UTF8.GetString(payload.Span) : null;
                tunnel.CompleteWsOpen(sid, new WsOpenResult(string.IsNullOrEmpty(reason), reason));
                break;
            case RelayFrame.WsMessage:
                // Forward the message to the browser side of this stream
                _ = ForwardWsMessageToBrowser(tunnel, sid, payload);
                break;
            case RelayFrame.WsClose: {
                var s = tunnel.GetWsStream(sid);
                if (s != null) {
                    var closeReason = payload.Length > 0 ? Encoding.UTF8.GetString(payload.Span) : "Tunnel closed stream";
                    _ = CloseBrowserWsAsync(s, closeReason);
                    tunnel.RemoveWsStream(sid);
                }
                break;
            }
            case RelayFrame.Pong:
                break;
            case RelayFrame.Ping:
                _ = SendSafelyAsync(tunnel, RelayFrame.BuildEmpty(RelayFrame.Pong));
                break;
            default:
                _logger.LogDebug("Tunnel {Host} unknown opcode 0x{Op:X2}", tunnel.Hostname, op);
                break;
        }
    }

    private async Task ForwardWsMessageToBrowser(TenantTunnel tunnel, uint streamId, ReadOnlyMemory<byte> payload) {
        var stream = tunnel.GetWsStream(streamId);
        if (stream == null) return;
        try {
            var (type, body) = WsMessageFrame.Parse(payload);
            var msgType = type == WsMessageFrame.TypeText
                ? WebSocketMessageType.Text
                : WebSocketMessageType.Binary;
            await stream.BrowserSendLock.WaitAsync();
            try {
                if (stream.Browser.State == WebSocketState.Open) {
                    await stream.Browser.SendAsync(body.ToArray(), msgType, true, CancellationToken.None);
                }
            } finally {
                stream.BrowserSendLock.Release();
            }
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Forward WS→browser failed on stream {Sid}", streamId);
            tunnel.RemoveWsStream(streamId);
            try { stream.Browser.Abort(); } catch { }
        }
    }

    private static async Task CloseBrowserWsAsync(WsTunnelStream s, string reason) {
        try {
            if (s.Browser.State == WebSocketState.Open || s.Browser.State == WebSocketState.CloseReceived) {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await s.Browser.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, cts.Token);
            }
        } catch { }
    }

    private async Task TryPingAsync(TenantTunnel tunnel) {
        try { await SendSafelyAsync(tunnel, RelayFrame.BuildEmpty(RelayFrame.Ping)); }
        catch { /* socket probably already broken; receive loop will catch it */ }
    }

    /// <summary>Sends a frame on the tunnel with the per-tunnel lock.</summary>
    public static async Task SendSafelyAsync(TenantTunnel tunnel, byte[] frame, CancellationToken ct = default) {
        await tunnel.SendLock.WaitAsync(ct);
        try {
            await tunnel.Socket.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
        } finally {
            tunnel.SendLock.Release();
        }
    }

    private static async Task SendAsync(WebSocket ws, byte[] data, CancellationToken ct) {
        await ws.SendAsync(data, WebSocketMessageType.Binary, true, ct);
    }

    private static async Task<ReadOnlyMemory<byte>?> ReadFrameAsync(WebSocket ws, CancellationToken ct) {
        // A single relay frame may span multiple WebSocket message fragments,
        // but we asked clients to always send EndOfMessage=true per frame, so
        // a buffered read until EndOfMessage is fine.
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true) {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
            if (ms.Length > 64 * 1024 * 1024) throw new InvalidDataException("frame too large (>64MB)");
        }
        return ms.ToArray();
    }

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s.Substring(0, n) + "…";

    private static string NormaliseThumbprint(string raw) =>
        new string(raw.Where(c => !char.IsWhiteSpace(c) && c != ':' && c != '-').ToArray()).ToLowerInvariant();
}
