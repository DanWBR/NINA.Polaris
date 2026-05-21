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
    private readonly ILogger<TunnelHandler> _logger;

    public TunnelHandler(TenantRegistry registry, ILogger<TunnelHandler> logger) {
        _registry = registry;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext ctx) {
        if (!ctx.WebSockets.IsWebSocketRequest) {
            ctx.Response.StatusCode = 400;
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);

        // ---- 1. Receive the Auth frame ----
        var first = await ReadFrameAsync(ws, lifetime.Token);
        if (first == null) return;
        var (op, _, payload) = RelayFrame.Parse(first.Value);
        if (op != RelayFrame.Auth) {
            await SendAsync(ws, RelayFrame.Build(RelayFrame.AuthFail, 0, "Expected Auth frame"), CancellationToken.None);
            return;
        }
        var token = Encoding.UTF8.GetString(payload.Span).Trim();
        if (!_registry.TryAuthenticate(token, out var hostname)) {
            _logger.LogWarning("Tunnel auth rejected for token prefix {Prefix}", Truncate(token, 8));
            await SendAsync(ws, RelayFrame.Build(RelayFrame.AuthFail, 0, "Unknown token"), CancellationToken.None);
            return;
        }

        var tunnel = new TenantTunnel(token, hostname, ws);
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
            case RelayFrame.Pong:
                // No-op, just proves liveness
                break;
            case RelayFrame.Ping:
                _ = SendSafelyAsync(tunnel, RelayFrame.BuildEmpty(RelayFrame.Pong));
                break;
            default:
                _logger.LogDebug("Tunnel {Host} unknown opcode 0x{Op:X2}", tunnel.Hostname, op);
                break;
        }
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
}
