using NINA.Relay.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<JsonTenantStore>();
builder.Services.AddSingleton<TenantRegistry>();
builder.Services.AddSingleton<TunnelHandler>();
builder.Services.AddSingleton<PublicProxy>();

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions {
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

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

// List configured tenants (admin-ish; lock down in real deployments)
app.MapGet("/_admin/tenants", (JsonTenantStore store) => Results.Ok(
    store.All.Select(t => new {
        token = t.Token.Length > 8 ? t.Token[..8] + "…" : t.Token,
        hostname = t.Hostname,
        enabled = t.Enabled,
        requestsPerSecond = t.RequestsPerSecond,
        bytesPerSecond = t.BytesPerSecond,
        note = t.Note
    })));

// Hot-reload trigger (in addition to filesystem watch)
app.MapPost("/_admin/reload-tenants", (JsonTenantStore store) => {
    store.Reload();
    return Results.Ok(new { reloaded = true, count = store.All.Count });
});

// Tunnel registration WebSocket (from NINA Headless instances)
app.Map("/_tunnel", async (HttpContext ctx, TunnelHandler handler) => {
    await handler.HandleAsync(ctx);
});

// Catchall proxy — everything else is forwarded to the matching tenant's tunnel
app.MapMethods("/{**catch}", new[] { "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS" },
    async (HttpContext ctx, PublicProxy proxy) => {
        await proxy.HandleAsync(ctx);
    });

app.Run();
