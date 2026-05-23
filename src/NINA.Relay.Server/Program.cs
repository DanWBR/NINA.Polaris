using LettuceEncrypt;
using NINA.Relay.Server;

var builder = WebApplication.CreateBuilder(args);

// -----------------------------------------------------------------------------
// Built-in TLS (optional). Three modes via Tls:Mode:
//   "off"          → HTTP only on Kestrel (the original behaviour; put a
//                    reverse proxy like Caddy or nginx in front for HTTPS)
//   "pfx"          → Manual cert: Tls:PfxPath + Tls:PfxPassword loaded into
//                    Kestrel's HTTPS endpoint
//   "letsencrypt"  → LettuceEncrypt acquires + renews certs automatically.
//                    Required: Tls:LetsEncrypt:Domains (string[]) and
//                    Tls:LetsEncrypt:EmailAddress. Stores certs under
//                    Tls:LetsEncrypt:StorePath (default "./letsencrypt").
// In any TLS mode the HTTPS port is bound (default 443; override with
// Tls:HttpsPort), the HTTP listener still binds for ACME http-01 challenges,
// and Tls:RedirectHttpToHttps toggles 308 redirects.
// -----------------------------------------------------------------------------
var tlsMode = (builder.Configuration["Tls:Mode"] ?? "off").ToLowerInvariant();
var httpsPort = builder.Configuration.GetValue("Tls:HttpsPort", 443);

if (tlsMode == "letsencrypt") {
    var domains = builder.Configuration.GetSection("Tls:LetsEncrypt:Domains").Get<string[]>() ?? Array.Empty<string>();
    var email = builder.Configuration["Tls:LetsEncrypt:EmailAddress"];
    var storePath = builder.Configuration["Tls:LetsEncrypt:StorePath"] ?? "letsencrypt";
    if (domains.Length == 0 || string.IsNullOrEmpty(email)) {
        throw new InvalidOperationException(
            "Tls:Mode=letsencrypt requires Tls:LetsEncrypt:Domains (non-empty) and Tls:LetsEncrypt:EmailAddress.");
    }
    builder.Services.AddLettuceEncrypt(opt => {
        opt.AcceptTermsOfService = true;
        opt.DomainNames = domains;
        opt.EmailAddress = email;
        opt.UseStagingServer = builder.Configuration.GetValue("Tls:LetsEncrypt:UseStaging", false);
    }).PersistDataToDirectory(new DirectoryInfo(storePath), pfxPassword: null);
}

if (tlsMode == "pfx" || tlsMode == "letsencrypt") {
    // Client-cert mode: "none" (default, browsers welcome), "request"
    // (offer; verify if presented — recommended when some tenants enable mTLS),
    // or "require" (mandate for every TLS handshake; breaks browser admin UI).
    var clientCertMode = (builder.Configuration["Tls:ClientCertificateMode"] ?? "request").ToLowerInvariant();
    builder.WebHost.ConfigureKestrel(k => {
        k.ListenAnyIP(httpsPort, listen => {
            Action<Microsoft.AspNetCore.Server.Kestrel.Https.HttpsConnectionAdapterOptions> configure = h => {
                h.ClientCertificateMode = clientCertMode switch {
                    "none"    => Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.NoCertificate,
                    "require" => Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate,
                    _         => Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.AllowCertificate
                };
                // Don't auto-reject self-signed; per-tenant thumbprint is what we actually check
                h.ClientCertificateValidation = (cert, chain, errors) => true;
            };
            if (tlsMode == "pfx") {
                var pfx = builder.Configuration["Tls:PfxPath"]
                    ?? throw new InvalidOperationException("Tls:Mode=pfx requires Tls:PfxPath");
                var pw = builder.Configuration["Tls:PfxPassword"];
                listen.UseHttps(pfx, pw, configure);
            } else {
                // LettuceEncrypt populates ServerCertificateSelector via UseHttps
                listen.UseHttps(configure);
            }
        });
    });
}

builder.Services.AddSingleton<JsonTenantStore>();
builder.Services.AddSingleton<TenantUsageStore>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<TenantRegistry>();
builder.Services.AddSingleton<TunnelHandler>();
builder.Services.AddSingleton<PublicProxy>();

var app = builder.Build();

if (tlsMode != "off" && builder.Configuration.GetValue("Tls:RedirectHttpToHttps", false)) {
    app.UseHttpsRedirection();
}

app.UseWebSockets(new WebSocketOptions {
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

// Serve static assets shipped with the relay (the admin SPA lives in wwwroot/admin)
app.UseStaticFiles();

// -----------------------------------------------------------------------------
// Admin gate: anything under /_admin/* requires Admin:Password (HTTP Basic or
// X-Admin-Password header). If no password is configured, /_admin endpoints
// return 503 to make the misconfiguration obvious instead of silently exposing
// tenant data.
// -----------------------------------------------------------------------------
var adminPassword = builder.Configuration["Admin:Password"];
app.Use(async (ctx, next) => {
    if (!ctx.Request.Path.StartsWithSegments("/_admin")) {
        await next(); return;
    }
    if (string.IsNullOrEmpty(adminPassword)) {
        ctx.Response.StatusCode = 503;
        ctx.Response.ContentType = "text/plain";
        await ctx.Response.WriteAsync("Admin API disabled — set Admin:Password in appsettings.json to enable.\n");
        return;
    }
    if (TryGetAdminPassword(ctx, out var provided) && provided == adminPassword) {
        await next(); return;
    }
    ctx.Response.StatusCode = 401;
    ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"relay-admin\"";
    await ctx.Response.WriteAsync("Unauthorized\n");
});

static bool TryGetAdminPassword(HttpContext ctx, out string? pw) {
    if (ctx.Request.Headers.TryGetValue("X-Admin-Password", out var h) && !string.IsNullOrEmpty(h)) {
        pw = h.ToString(); return true;
    }
    if (ctx.Request.Headers.TryGetValue("Authorization", out var auth) && auth.ToString().StartsWith("Basic ")) {
        try {
            var decoded = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(auth.ToString()[6..]));
            var colon = decoded.IndexOf(':');
            pw = colon >= 0 ? decoded[(colon + 1)..] : decoded;
            return true;
        } catch { }
    }
    pw = null; return false;
}

// Health probe
app.MapGet("/_health", (TenantRegistry reg) => Results.Ok(new {
    status = "ok",
    activeTunnels = reg.ActiveTunnels.Count(),
    uptime = DateTime.UtcNow
}));

// List active tunnels (admin-ish; lock down in real deployments)
app.MapGet("/_tunnels", (TenantRegistry reg) => Results.Ok(
    reg.ActiveTunnels.Select(t => new {
        hostname = t.Hostname,
        connectedAt = t.ConnectedAt,
        bytesIn = t.BytesIn,
        bytesOut = t.BytesOut,
        rateLimited = t.RateLimiter != null,
        requestsPerSecond = t.RateLimiter?.RequestsPerSecond ?? 0,
        bytesPerSecond = t.RateLimiter?.BytesPerSecond ?? 0
    })));

// List configured tenants. Endpoints under /_admin are gated by the Admin
// middleware above, so we return full tokens here.
app.MapGet("/_admin/tenants", (JsonTenantStore store, TenantUsageStore usage) => Results.Ok(
    store.All.Select(t => new {
        token = t.Token,
        hostname = t.Hostname,
        enabled = t.Enabled,
        requestsPerSecond = t.RequestsPerSecond,
        burstRequests = t.BurstRequests,
        bytesPerSecond = t.BytesPerSecond,
        burstBytes = t.BurstBytes,
        monthlyBytes = t.MonthlyBytes,
        bytesThisMonth = usage.BytesThisMonth(t.Token),
        quotaUsedPercent = t.MonthlyBytes > 0
            ? Math.Round(100.0 * usage.BytesThisMonth(t.Token) / t.MonthlyBytes, 1)
            : (double?)null,
        expiresAt = t.ExpiresAt,
        expired = t.ExpiresAt.HasValue && DateTime.UtcNow >= t.ExpiresAt.Value,
        clientCertThumbprint = t.ClientCertThumbprint,
        note = t.Note
    })));

// Create / update a tenant by token. Validates the incoming TenantConfig
// and atomically rewrites tenants.json.
app.MapPost("/_admin/tenants", (TenantConfig body, JsonTenantStore store) => {
    if (string.IsNullOrWhiteSpace(body.Token) || string.IsNullOrWhiteSpace(body.Hostname))
        return Results.BadRequest(new { error = "token and hostname are required" });
    var next = store.All.Where(t => !t.Token.Equals(body.Token, StringComparison.OrdinalIgnoreCase))
                       .Append(body).ToList();
    store.Save(next);
    return Results.Ok(new { saved = true, count = next.Count });
});

app.MapDelete("/_admin/tenants/{token}", (string token, JsonTenantStore store) => {
    var next = store.All.Where(t => !t.Token.Equals(token, StringComparison.OrdinalIgnoreCase)).ToList();
    if (next.Count == store.All.Count) return Results.NotFound();
    store.Save(next);
    return Results.Ok(new { deleted = true, remaining = next.Count });
});

// Generate a cryptographically-random bearer token. Convenience for the UI's
// "new tenant" flow so operators don't have to shell out to openssl.
app.MapGet("/_admin/generate-token", () => {
    var bytes = new byte[32];
    System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
    return Results.Ok(new { token = Convert.ToHexString(bytes).ToLowerInvariant() });
});

// Hot-reload trigger (in addition to filesystem watch)
app.MapPost("/_admin/reload-tenants", (JsonTenantStore store) => {
    store.Reload();
    return Results.Ok(new { reloaded = true, count = store.All.Count });
});

// Forgive a tenant mid-month — resets only their byte counter to zero
app.MapPost("/_admin/usage/{token}/reset", (string token, TenantUsageStore usage) => {
    usage.Reset(token);
    return Results.Ok(new { token = token.Length > 8 ? token[..8] + "…" : token, reset = true });
});

// Read recent audit records. Optional ?tenant= filter and ?limit= cap.
app.MapGet("/_admin/audit", (AuditLog audit, string? tenant, int? limit) => Results.Ok(new {
    enabled = audit.Enabled,
    path = audit.Path,
    records = audit.Snapshot(tenant, limit ?? 200)
}));


// Tunnel registration WebSocket (from N.I.N.A. Polaris instances)
app.Map("/_tunnel", async (HttpContext ctx, TunnelHandler handler) => {
    await handler.HandleAsync(ctx);
});

// Catchall proxy — everything else is forwarded to the matching tenant's tunnel
app.MapMethods("/{**catch}", new[] { "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS" },
    async (HttpContext ctx, PublicProxy proxy) => {
        await proxy.HandleAsync(ctx);
    });

app.Run();
