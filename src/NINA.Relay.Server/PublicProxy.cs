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
    private readonly ILogger<PublicProxy> _logger;
    private readonly TimeSpan _requestTimeout;
    private readonly string? _hostnameSuffix; // e.g. ".relay.example.com"

    public PublicProxy(TenantRegistry registry, ILogger<PublicProxy> logger, IConfiguration config) {
        _registry = registry;
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

        // ---- Read the inbound body fully (we need its length to serialise) ----
        byte[] body;
        using (var ms = new MemoryStream()) {
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            body = ms.ToArray();
        }

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
}
