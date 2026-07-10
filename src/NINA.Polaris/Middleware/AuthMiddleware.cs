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
///   /phd2-gui/*       (reverse-proxied embedded GUI — Linux/xpra)
///   /phd2-vnc/*       (noVNC static client — Windows/TightVNC)
///   /phd2-vnc-ws      (WebSocket bridge to local TightVNC TCP)
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
            || path.StartsWithSegments("/phd2-vnc")        // noVNC static + /phd2-vnc-ws bridge
            || path.StartsWithSegments("/phd2-vnc-ws")
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
        // Instance-identify for the mobile app's hotspot discovery
        // fallback (probing candidate addresses when mDNS multicast
        // doesn't reach the phone). Exposes only what the mDNS TXT
        // record already broadcasts.
        if (path.Equals("/api/identify",
                StringComparison.OrdinalIgnoreCase)) return true;
        // TLS-A1: cert download + install instructions stay public.
        // The whole point is letting a brand-new device (celular,
        // tablet) grab the root cert BEFORE the user can even hit
        // the login screen without warnings. The cert is by design
        // public material (it's a trust anchor; verification is via
        // the fingerprint shown in /api/tls/status which stays
        // gated). Auth still applies to /api/tls/status and the LE
        // config endpoints, those carry the DuckDNS token.
        if (path.Equals("/api/tls/ca.crt",
                StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/api/tls/install-instructions",
                StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

public static class AuthMiddlewareExtensions {
    public static IApplicationBuilder UseAuthMiddleware(this IApplicationBuilder app)
        => app.UseMiddleware<AuthMiddleware>();
}