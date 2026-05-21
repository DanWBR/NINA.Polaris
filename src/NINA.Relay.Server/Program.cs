using NINA.Relay.Server;

var builder = WebApplication.CreateBuilder(args);

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
        connectedAt = t.ConnectedAt
    })));

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
