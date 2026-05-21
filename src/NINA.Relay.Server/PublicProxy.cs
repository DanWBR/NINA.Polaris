using System.Net.WebSockets;
using System.Text;
using NINA.Relay.Protocol;

namespace NINA.Relay.Server;

/// <summary>
/// Handles the public-facing side: an incoming HTTP request from a browser
/// is routed to the matching tenant tunnel (resolved either by Host header
/// "&lt;tenant&gt;.relay.example.com" or by a "/t/&lt;tenant&gt;/..." path prefix),
/// serialised as an HttpRequest frame, sent down the tunnel, and the
/// response frame is written back to the browser.
///
/// Per-request timeout is configurable; defaults to 60 s (long enough for
/// plate-solving uploads).
/// </summary>
public class PublicProxy {
    private readonly TenantRegistry _registry;
    private readonly TenantUsageStore _usage;
    private readonly ILogger<PublicProxy> _logger;
    private readonly TimeSpan _requestTimeout;
    private readonly string? _hostnameSuffix; // e.g. ".relay.example.com"

    public PublicProxy(TenantRegistry registry, TenantUsageStore usage,
        ILogger<PublicProxy> logger, IConfiguration config) {
        _registry = registry;
        _usage = usage;
        _logger = logger;
        _requestTimeout = TimeSpan.FromSeconds(config.GetValue("Proxy:TimeoutSeconds", 60));
        _hostnameSuffix = config.GetValue<string?>("Proxy:HostnameSuffix");
    }

    public async Task HandleAsync(HttpContext ctx) {
        var (tenantName, forwardPath) = ResolveTenant(ctx);
        if (tenantName == null) {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsync("No tenant matched (use subdomain or /t/<tenant>/...)\n");
            return;
        }

        var tunnel = _registry.GetByHostname(tenantName);
        if (tunnel == null) {
            ctx.Response.StatusCode = 502;
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.WriteAsync($"Tenant '{tenantName}' is not currently connected to the relay.\n");
            return;
        }

        // Branch: WebSocket upgrade requests use the WS-over-tunnel path.
        if (ctx.WebSockets.IsWebSocketRequest) {
            await HandleWebSocketAsync(ctx, tunnel, forwardPath);
            return;
        }

        // ---- Read the inbound body fully (we need its length to serialise) ----
        byte[] body;
        using (var ms = new MemoryStream()) {
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            body = ms.ToArray();
        }

        // ---- Enforce per-tenant rate limit (request + byte buckets) ----
        if (tunnel.RateLimiter != null &&
            !tunnel.RateLimiter.TryAcquire(body.Length, out var rl)) {
            ctx.Response.StatusCode = 429; // Too Many Requests
            var retryAfter = double.IsInfinity(rl.RetryAfterSeconds) ? 60 : Math.Ceiling(rl.RetryAfterSeconds);
            ctx.Response.Headers["Retry-After"] = retryAfter.ToString("F0");
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.WriteAsync($"Rate limit exceeded ({rl.LimitedBy}). Retry after {retryAfter:F0}s.\n");
            return;
        }

        // ---- Enforce monthly transfer quota (resets on the 1st UTC) ----
        if (tunnel.Config != null && tunnel.Config.MonthlyBytes > 0) {
            var used = _usage.BytesThisMonth(tunnel.Token);
            if (used >= tunnel.Config.MonthlyBytes) {
                ctx.Response.StatusCode = 402; // Payment Required
                ctx.Response.ContentType = "text/plain";
                ctx.Response.Headers["X-Quota-Used"] = used.ToString();
                ctx.Response.Headers["X-Quota-Limit"] = tunnel.Config.MonthlyBytes.ToString();
                await ctx.Response.WriteAsync(
                    $"Monthly transfer quota exhausted ({used:N0} / {tunnel.Config.MonthlyBytes:N0} bytes). Resets on the 1st UTC.\n");
                return;
            }
        }

        tunnel.AddBytesIn(body.Length);
        _usage.Charge(tunnel.Token, body.Length);

        // ---- Serialise + send ----
        var headers = ctx.Request.Headers
            .Select(h => new KeyValuePair<string, string>(h.Key, h.Value.ToString()))
            .ToList();
        // Add a hop marker so the client side knows the original Host
        headers.Add(new KeyValuePair<string, string>("X-Forwarded-Host", ctx.Request.Host.Value ?? ""));
        headers.Add(new KeyValuePair<string, string>("X-Forwarded-For", ctx.Connection.RemoteIpAddress?.ToString() ?? ""));
        headers.Add(new KeyValuePair<string, string>("X-Forwarded-Proto", ctx.Request.Scheme));

        var sid = tunnel.AllocateStreamId();
        var payload = HttpRequestFrame.Serialise(ctx.Request.Method, forwardPath, headers, body);
        var frame = RelayFrame.Build(RelayFrame.HttpRequest, sid, payload);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        timeoutCts.CancelAfter(_requestTimeout);

        var responseTask = tunnel.AwaitResponseAsync(sid, timeoutCts.Token);
        try {
            await TunnelHandler.SendSafelyAsync(tunnel, frame, timeoutCts.Token);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to send frame to tunnel {Host}", tenantName);
            ctx.Response.StatusCode = 502;
            await ctx.Response.WriteAsync($"Failed to deliver request to tunnel: {ex.Message}\n");
            return;
        }

        ResponseMessage resp;
        try {
            resp = await responseTask;
        } catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested) {
            ctx.Response.StatusCode = 504;
            await ctx.Response.WriteAsync($"Tunnel response timed out after {_requestTimeout.TotalSeconds}s\n");
            return;
        } catch (Exception ex) {
            ctx.Response.StatusCode = 502;
            await ctx.Response.WriteAsync($"Tunnel error: {ex.Message}\n");
            return;
        }

        // ---- Write the response back to the browser ----
        ctx.Response.StatusCode = resp.Status;
        foreach (var h in resp.Headers) {
            // Skip headers Kestrel manages
            if (IsRestrictedResponseHeader(h.Key)) continue;
            ctx.Response.Headers[h.Key] = h.Value;
        }
        // Track outbound bytes against the tenant's bandwidth bucket. We don't
        // refuse mid-response (clients hate that), but the tokens will go
        // negative and the next request gets rejected until the bucket refills.
        tunnel.AddBytesOut(resp.Body.LongLength);
        tunnel.RateLimiter?.ChargeBytes(resp.Body.LongLength);
        _usage.Charge(tunnel.Token, resp.Body.LongLength);
        await ctx.Response.Body.WriteAsync(resp.Body, ctx.RequestAborted);
    }

    private (string? tenant, string path) ResolveTenant(HttpContext ctx) {
        // 1. Subdomain routing: tenant.relay.example.com → "tenant"
        if (!string.IsNullOrEmpty(_hostnameSuffix)) {
            var host = ctx.Request.Host.Host;
            if (host.EndsWith(_hostnameSuffix, StringComparison.OrdinalIgnoreCase)) {
                var sub = host[..^_hostnameSuffix.Length];
                if (!string.IsNullOrEmpty(sub) && !sub.Contains('.')) {
                    return (sub, ctx.Request.Path + ctx.Request.QueryString);
                }
            }
        }

        // 2. Path-prefix routing: /t/<tenant>/whatever
        var path = ctx.Request.Path.Value ?? "";
        if (path.StartsWith("/t/", StringComparison.OrdinalIgnoreCase)) {
            var rest = path[3..];
            var slash = rest.IndexOf('/');
            if (slash > 0) {
                var sub = rest[..slash];
                var forward = rest[slash..] + ctx.Request.QueryString;
                return (sub, forward);
            }
        }

        return (null, "");
    }

    private static bool IsRestrictedResponseHeader(string name) {
        return name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase);
    }

    // ---- WebSocket-over-tunnel ----

    /// <summary>
    /// Accept the browser's WS upgrade, then ask the tunnel client to open a
    /// matching local WS via the WsOpen frame. Wait for WsOpenAck, then run
    /// the bidirectional pump until either side closes.
    /// </summary>
    private async Task HandleWebSocketAsync(HttpContext ctx, TenantTunnel tunnel, string forwardPath) {
        using var browserWs = await ctx.WebSockets.AcceptWebSocketAsync();
        var streamId = tunnel.AllocateStreamId();
        var stream = new WsTunnelStream(browserWs);
        tunnel.RegisterWsStream(streamId, stream);

        // 1. Ask the tunnel client to open the local WS for us
        var openFrame = RelayFrame.Build(RelayFrame.WsOpen, streamId, WsOpenFrame.Serialise(forwardPath));
        try {
            await TunnelHandler.SendSafelyAsync(tunnel, openFrame, ctx.RequestAborted);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "WS-open send failed for {Path}", forwardPath);
            tunnel.RemoveWsStream(streamId);
            try { await browserWs.CloseAsync(WebSocketCloseStatus.InternalServerError, "Tunnel unreachable", CancellationToken.None); } catch { }
            return;
        }

        // 2. Wait for WsOpenAck (up to 15s)
        using var openCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        openCts.CancelAfter(TimeSpan.FromSeconds(15));
        WsOpenResult ack;
        try { ack = await tunnel.AwaitWsOpenAckAsync(streamId, openCts.Token); }
        catch (OperationCanceledException) {
            tunnel.RemoveWsStream(streamId);
            try { await browserWs.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Tunnel did not ack WS open in time", CancellationToken.None); } catch { }
            return;
        }
        if (!ack.Success) {
            tunnel.RemoveWsStream(streamId);
            try { await browserWs.CloseAsync(WebSocketCloseStatus.PolicyViolation, ack.Error ?? "Tunnel rejected WS open", CancellationToken.None); } catch { }
            return;
        }

        // 3. Pump browser → tunnel until browser closes
        try {
            var buffer = new byte[64 * 1024];
            while (browserWs.State == WebSocketState.Open && !ctx.RequestAborted.IsCancellationRequested) {
                WebSocketReceiveResult r;
                using var ms = new MemoryStream();
                do {
                    r = await browserWs.ReceiveAsync(buffer, ctx.RequestAborted);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buffer, 0, r.Count);
                    if (ms.Length > 8 * 1024 * 1024) throw new InvalidDataException("WS frame too large (>8MB)");
                } while (!r.EndOfMessage);

                if (r.MessageType == WebSocketMessageType.Close) break;

                var msgType = r.MessageType == WebSocketMessageType.Text ? WsMessageFrame.TypeText : WsMessageFrame.TypeBinary;
                var payload = WsMessageFrame.Serialise(msgType, ms.ToArray());
                var frame = RelayFrame.Build(RelayFrame.WsMessage, streamId, payload);
                await TunnelHandler.SendSafelyAsync(tunnel, frame, ctx.RequestAborted);
            }
        } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) {
            // Normal disconnect
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Browser→tunnel WS pump crashed on stream {Sid}", streamId);
        } finally {
            // Tell the tunnel client to close its local WS too
            try {
                var closeFrame = RelayFrame.BuildEmpty(RelayFrame.WsClose, streamId);
                await TunnelHandler.SendSafelyAsync(tunnel, closeFrame, CancellationToken.None);
            } catch { }
            tunnel.RemoveWsStream(streamId);
        }
    }
}
