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

using Microsoft.AspNetCore.Http;
using NINA.Polaris.Services.Auth;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// AUTH-1: HTTP surface for the local-auth feature.
///
/// All routes under /api/auth/* are exempted from AuthMiddleware so
/// the frontend can hit /status before deciding whether to show the
/// first-run wizard, the login overlay, or the app itself.
///
/// Endpoints also set / clear a same-origin HttpOnly cookie so the
/// embedded iframes (phd2-gui, indi-web, sky) carry auth automatically
/// without needing JS to intercept their requests.
/// </summary>
public static class AuthEndpoints {
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app) {
        var g = app.MapGroup("/api/auth");

        // GET /api/auth/status
        // Public. Tells the frontend which boot path to take:
        //   configured=false       -> show wizard
        //   enabled=false          -> let app load with no login
        //   authenticated=false    -> show login overlay
        //   authenticated=true     -> app ready
        g.MapGet("/status", (HttpContext ctx, AuthService auth) => {
            var token = ExtractToken(ctx);
            return Results.Ok(auth.GetStatus(token));
        });

        // POST /api/auth/setup { password }
        // First-run only; rejects when a password is already set.
        g.MapPost("/setup", (HttpContext ctx, AuthService auth, SetupRequest req) => {
            if (auth.IsConfigured)
                return Results.Conflict(new { error = "already configured" });
            try {
                // First-run setup happens on the operator's own device, so
                // remember it by default (the setup form has no checkbox and
                // would otherwise send the stale false, never remembering).
                var token = auth.SetInitialPassword(req.Password ?? "", remember: true);
                if (token == null)
                    return Results.BadRequest(new { error = "setup failed" });
                SetSessionCookie(ctx, token, persist: true);
                return Results.Ok(new { token });
            } catch (ArgumentException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // POST /api/auth/login { password }
        // Returns the session token + sets the cookie. Rate-limited
        // per IP; lockout response includes a Retry-After header.
        g.MapPost("/login", (HttpContext ctx, AuthService auth, LoginRequest req) => {
            if (!auth.IsConfigured)
                return Results.BadRequest(new { error = "not configured" });
            var token = auth.Login(req.Password ?? "",
                ctx.Connection.RemoteIpAddress, req.Remember);
            if (token == null)
                return Results.Json(new { error = "invalid password" },
                    statusCode: 401);
            SetSessionCookie(ctx, token, req.Remember);
            return Results.Ok(new { token });
        });

        // POST /api/auth/logout
        // Invalidates the presented session and clears the cookie.
        g.MapPost("/logout", (HttpContext ctx, AuthService auth) => {
            var token = ExtractToken(ctx);
            if (!string.IsNullOrEmpty(token)) auth.Logout(token);
            ClearSessionCookie(ctx);
            return Results.Ok(new { ok = true });
        });

        // POST /api/auth/change-password { current, new }
        // Authenticated. Invalidates every other session.
        g.MapPost("/change-password",
                (HttpContext ctx, AuthService auth, ChangePasswordRequest req) => {
            var current = ExtractToken(ctx);
            if (string.IsNullOrEmpty(current) || !auth.ValidateToken(current))
                return Results.Json(new { error = "not authenticated" },
                    statusCode: 401);
            try {
                var newToken = auth.ChangePassword(
                    req.Current ?? "", req.New ?? "", current);
                if (newToken == null)
                    return Results.Json(new { error = "current password invalid" },
                        statusCode: 401);
                SetSessionCookie(ctx, newToken);
                return Results.Ok(new { ok = true });
            } catch (ArgumentException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // POST /api/auth/disable { password }
        // Persists AuthEnabled=false on the active profile. Requires
        // current password so a stolen session token alone cannot
        // disable auth.
        g.MapPost("/disable", (AuthService auth, DisableEnableRequest req) => {
            if (!auth.SetEnabled(req.Password ?? "", false))
                return Results.Json(new { error = "invalid password" },
                    statusCode: 401);
            return Results.Ok(new { ok = true, enabled = false });
        });

        // POST /api/auth/enable { password }
        // Same as /disable but flips it back on. Useful when the
        // operator toggles auth off temporarily on a closed LAN and
        // wants to reactivate before going somewhere public.
        g.MapPost("/enable", (AuthService auth, DisableEnableRequest req) => {
            if (!auth.SetEnabled(req.Password ?? "", true))
                return Results.Json(new { error = "invalid password" },
                    statusCode: 401);
            return Results.Ok(new { ok = true, enabled = true });
        });
    }

    // ----- helpers ---------------------------------------------------

    /// <summary>Token lookup order matches AuthMiddleware:
    /// Authorization header > ?token= query > polaris_session cookie.
    /// Kept here too so endpoints can read the caller's session
    /// without depending on the middleware having stashed it.</summary>
    internal static string? ExtractToken(HttpContext ctx) {
        var hdr = ctx.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(hdr) &&
                hdr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) {
            return hdr["Bearer ".Length..].Trim();
        }
        // Path-embedded token for cross-origin embedded sub-apps (xpra PHD2
        // GUI). The Capacitor wrapper loads the Polaris UI in a cross-origin
        // iframe, so the Android/iOS WebView blocks the third-party session
        // cookie and the embedded xpra client — whose own asset + WebSocket
        // requests we can't add an Authorization header to — has no way to
        // authenticate. The client carries the token as a path segment
        // (/phd2-gui/t/<token>/...) so every relative sub-request AND the
        // WebSocket inherit it; the proxy strips /t/<token> before forwarding
        // upstream. Checked before ?token=/cookie so it wins for these
        // requests even when a stale cookie is also present.
        var pathToken = ExtractPathToken(ctx.Request.Path.Value);
        if (!string.IsNullOrEmpty(pathToken)) return pathToken;
        var q = ctx.Request.Query["token"].ToString();
        if (!string.IsNullOrEmpty(q)) return q;
        if (ctx.Request.Cookies.TryGetValue(AuthService.CookieName, out var c))
            return c;
        return null;
    }

    /// <summary>
    /// Pulls a token from a <c>/&lt;proxy-root&gt;/t/&lt;token&gt;/...</c>
    /// path used by cross-origin embedded sub-apps (see ExtractToken). Only
    /// the proxied sub-app roots in <see cref="PathTokenRoots"/> opt in.
    /// Returns null when the path doesn't match the convention.
    /// </summary>
    internal static string? ExtractPathToken(string? path) {
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var root in PathTokenRoots) {
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) {
                var after = path[root.Length..];
                var slash = after.IndexOf('/');
                var tok = slash >= 0 ? after[..slash] : after;
                if (!string.IsNullOrEmpty(tok)) return Uri.UnescapeDataString(tok);
            }
        }
        return null;
    }

    // Proxy roots that accept a path-embedded token. Keep in sync with the
    // matching strip logic in the reverse-proxy map in Program.cs.
    private static readonly string[] PathTokenRoots = { "/phd2-gui/t/" };

    private static void SetSessionCookie(HttpContext ctx, string token, bool persist = false) {
        // Cookie carries auth for the embedded sub-apps (phd2-gui,
        // indi-web, sky) and any <iframe>/<img>/<a> navigation that
        // can't send the Authorization header. The bearer token in
        // localStorage remains the primary path for JS fetch/XHR.
        //
        // SameSite: the official mobile wrapper (Capacitor) loads the
        // whole Polaris UI inside a CROSS-ORIGIN iframe (app shell
        // origin https://localhost, Polaris on https://<host>:5000).
        // A SameSite=Strict/Lax cookie is treated as third-party there
        // and the WebView never sends it, so every cookie-dependent
        // request 401s (version badge, embedded iframes, etc.). Over
        // HTTPS we therefore issue SameSite=None (which REQUIRES
        // Secure) so the cookie survives the cross-origin iframe. Over
        // plain HTTP we can't use None (browsers reject None without
        // Secure), so fall back to Lax; the bearer token still covers
        // the wrapper there.
        //
        // Trade-off: SameSite=None relaxes CSRF protection. It is
        // acceptable here because the cookie is HttpOnly + Secure, the
        // server is a single-user LAN host, and bearer-token auth is
        // the primary mechanism for state-changing JS requests.
        var sameSite = ctx.Request.IsHttps
            ? SameSiteMode.None
            : SameSiteMode.Lax;
        var opts = new CookieOptions {
            HttpOnly = true,
            Secure = ctx.Request.IsHttps,
            SameSite = sameSite,
            Path = "/",
        };
        // "Remember on this device": give the cookie an explicit lifetime
        // so it survives a full browser / app restart, mirroring the
        // localStorage bearer token the client keeps in the same case.
        // Without this the cookie was always a session cookie that died
        // on close, so cookie-authenticated requests (embedded iframes,
        // <img>/<ws>, and the whole UI in the cross-origin Capacitor
        // mobile wrapper) had to re-login every launch even though
        // "remember" was ticked. Unchecked stays a session cookie
        // (mirrors sessionStorage). The server-side session TTL
        // (AuthSessionTimeoutHours, sliding) is still the real auth gate;
        // a cookie that outlives its session just 401s into a re-login.
        if (persist)
            opts.Expires = DateTimeOffset.UtcNow.AddDays(30);
        ctx.Response.Cookies.Append(AuthService.CookieName, token, opts);
    }

    private static void ClearSessionCookie(HttpContext ctx) {
        ctx.Response.Cookies.Delete(AuthService.CookieName,
            new CookieOptions { Path = "/" });
    }

    public record SetupRequest(string? Password, bool Remember = false);
    public record LoginRequest(string? Password, bool Remember = false);
    public record ChangePasswordRequest(string? Current, string? New);
    public record DisableEnableRequest(string? Password);
}