using System.Net;
using Microsoft.AspNetCore.Http;
using NINA.Polaris.Endpoints;
using NINA.Polaris.Services.Auth;

namespace NINA.Polaris.Middleware;

/// <summary>
/// AUTH-2: gate the local HTTP API + WebSockets + reverse-proxy
/// sub-apps behind the bearer token issued by AuthService.
///
/// Allow rules (in order):
///   1. AuthEnabled == false on the profile -> always pass.
///   2. RemoteIpAddress is loopback (127.0.0.1 / ::1) -> always
///      pass. Quem ja esta no Pi e' confiavel; covers SSH tunnels,
///      local scripts, dev. Jupyter / RStudio / Grafana use the
///      same convention.
///   3. Path is NOT in the gated set -> pass. Default-allow is
///      important so the login page itself + every static asset
///      (CSS, JS, images, fonts) can load without a token.
///   4. Path is gated AND token validates -> pass + 401 otherwise.
///
/// Gated prefixes:
///   /api/*            (with /api/auth/* and /api/system/version
///                      explicitly exempted)
///   /ws/*             (browser auto-attaches the polaris_session
///                      cookie to same-origin WS upgrades, so the
///                      same middleware check that gates /api also
///                      gates /ws. No special handshake protocol
///                      needed. ?token= in the URL is the fallback
///                      for non-cookie scenarios.)
///   /phd2-gui/*       (reverse-proxied embedded GUI)
///   /indi-web/*       (reverse-proxied INDI Web Manager)
///   /sky/*            (Stellarium sub-app, includes API calls back
///                      to /sky/data/*)
///
/// Token extraction matches AuthEndpoints.ExtractToken:
///   Authorization: Bearer <token>   (preferred)
///   ?token=<token>                  (file download URLs)
///   polaris_session cookie          (iframes + img/a)
/// </summary>
public class AuthMiddleware {
    private readonly RequestDelegate _next;
    private readonly AuthService _auth;

    public AuthMiddleware(RequestDelegate next, AuthService auth) {
        _next = next;
        _auth = auth;
    }

    public async Task InvokeAsync(HttpContext ctx) {
        if (!_auth.IsEnabled) { await _next(ctx); return; }
        if (IsLoopback(ctx.Connection.RemoteIpAddress)) {
            await _next(ctx);
            return;
        }
        var path = ctx.Request.Path;
        if (!IsGated(path)) { await _next(ctx); return; }
        if (IsExempt(path)) { await _next(ctx); return; }

        var token = AuthEndpoints.ExtractToken(ctx);
        if (string.IsNullOrEmpty(token) || !_auth.ValidateToken(token)) {
            ctx.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(
                "{\"error\":\"auth required\",\"authConfigured\":"
                + (_auth.IsConfigured ? "true" : "false") + "}");
            return;
        }
        await _next(ctx);
    }

    private static bool IsLoopback(IPAddress? ip) {
        if (ip == null) return true;     // unknown / in-process -> treat as local
        if (IPAddress.IsLoopback(ip)) return true;
        // IPv6 link-local + IPv4-mapped loopback fall under IsLoopback
        // already in .NET; nothing extra needed here.
        return false;
    }

    private static bool IsGated(PathString path) {
        return path.StartsWithSegments("/api")
            || path.StartsWithSegments("/ws")
            || path.StartsWithSegments("/phd2-gui")
            || path.StartsWithSegments("/indi-web")
            || path.StartsWithSegments("/sky");
    }

    private static bool IsExempt(PathString path) {
        // /api/auth/* MUST stay open so the frontend can hit /status
        // before deciding wizard vs login vs app, and /login itself
        // is the entry point. /api/system/version is exempted so
        // discovery probes (mDNS scanners checking who is who) work
        // without credentials.
        if (path.StartsWithSegments("/api/auth")) return true;
        if (path.Equals("/api/system/version",
                StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

public static class AuthMiddlewareExtensions {
    public static IApplicationBuilder UseAuthMiddleware(this IApplicationBuilder app)
        => app.UseMiddleware<AuthMiddleware>();
}
