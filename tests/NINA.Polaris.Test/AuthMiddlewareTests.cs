using System.Collections.Generic;
using System.IO;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Middleware;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Auth;

namespace NINA.Polaris.Test;

/// <summary>
/// AUTH-2: middleware behaviour pinned with synthetic HttpContexts.
/// Asserts the gate-by-prefix rule, the loopback bypass, the
/// AuthEnabled toggle, the three token sources (header, query,
/// cookie), and the 401 body shape the frontend depends on.
/// Avoids spinning up a full WebApplicationFactory so it stays
/// fast and dep-free.
/// </summary>
[TestFixture]
public class AuthMiddlewareTests {

    private readonly List<string> _tempDirs = new();
    private bool _nextCalled;

    [TearDown]
    public void Cleanup() {
        foreach (var d in _tempDirs) {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); }
            catch { }
        }
        _tempDirs.Clear();
    }

    private (AuthMiddleware mw, AuthService auth, ProfileService profiles) Make() {
        var dir = Path.Combine(Path.GetTempPath(),
            "polaris-authmw-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Profiles:Directory"] = dir
            })
            .Build();
        var profiles = new ProfileService(cfg,
            NullLogger<ProfileService>.Instance);
        var auth = new AuthService(profiles,
            NullLogger<AuthService>.Instance);
        _nextCalled = false;
        var mw = new AuthMiddleware(_ => {
            _nextCalled = true;
            return Task.CompletedTask;
        }, auth);
        return (mw, auth, profiles);
    }

    private static HttpContext NewCtx(string path,
            IPAddress? remoteIp = null, string? bearer = null,
            string? queryToken = null, string? cookieToken = null) {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Connection.RemoteIpAddress = remoteIp
            ?? IPAddress.Parse("192.168.1.100");
        if (bearer != null) ctx.Request.Headers.Authorization = "Bearer " + bearer;
        if (queryToken != null) ctx.Request.QueryString = new QueryString("?token=" + queryToken);
        if (cookieToken != null) {
            ctx.Request.Headers.Cookie =
                AuthService.CookieName + "=" + cookieToken;
        }
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    [Test]
    public async Task UngatedPath_PassesWithoutToken() {
        var (mw, _, _) = Make();
        var ctx = NewCtx("/index.html");
        await mw.InvokeAsync(ctx);
        Assert.That(_nextCalled, Is.True);
        Assert.That(ctx.Response.StatusCode, Is.EqualTo(200));
    }

    [Test]
    public async Task StaticAsset_PassesWithoutToken() {
        var (mw, _, _) = Make();
        var ctx = NewCtx("/js/app.js");
        await mw.InvokeAsync(ctx);
        Assert.That(_nextCalled, Is.True);
    }

    [Test]
    public async Task ApiAuthPaths_AreExempt() {
        var (mw, _, _) = Make();
        foreach (var p in new[] { "/api/auth/status", "/api/auth/login",
                                  "/api/auth/setup" }) {
            _nextCalled = false;
            var ctx = NewCtx(p);
            await mw.InvokeAsync(ctx);
            Assert.That(_nextCalled, Is.True, $"{p} should be exempt");
        }
    }

    [Test]
    public async Task ApiSystemVersion_IsExempt() {
        var (mw, _, _) = Make();
        var ctx = NewCtx("/api/system/version");
        await mw.InvokeAsync(ctx);
        Assert.That(_nextCalled, Is.True);
    }

    [Test]
    public async Task ApiOtherWithoutToken_Returns401() {
        var (mw, _, _) = Make();
        var ctx = NewCtx("/api/camera/capture");
        await mw.InvokeAsync(ctx);
        Assert.That(_nextCalled, Is.False);
        Assert.That(ctx.Response.StatusCode, Is.EqualTo(401));
    }

    [Test]
    public async Task WsWithoutToken_Returns401() {
        var (mw, _, _) = Make();
        var ctx = NewCtx("/ws/status");
        await mw.InvokeAsync(ctx);
        Assert.That(ctx.Response.StatusCode, Is.EqualTo(401));
    }

    [Test]
    public async Task SkyWithoutToken_Returns401() {
        // The whole Stellarium sub-app sits behind auth; we don't
        // want unauthenticated browsers loading /sky/data/skydata/.
        var (mw, _, _) = Make();
        var ctx = NewCtx("/sky/index.html");
        await mw.InvokeAsync(ctx);
        Assert.That(ctx.Response.StatusCode, Is.EqualTo(401));
    }

    [Test]
    public async Task ProxyPathsWithoutToken_Return401() {
        var (mw, _, _) = Make();
        foreach (var p in new[] { "/phd2-gui/", "/indi-web/" }) {
            _nextCalled = false;
            var ctx = NewCtx(p);
            await mw.InvokeAsync(ctx);
            Assert.That(ctx.Response.StatusCode, Is.EqualTo(401),
                $"{p} should be gated");
        }
    }

    [Test]
    public async Task ValidBearer_Passes() {
        var (mw, auth, _) = Make();
        auth.SetInitialPassword("hunter2!");
        var token = auth.Login("hunter2!", IPAddress.Loopback);
        var ctx = NewCtx("/api/camera/capture", bearer: token);
        await mw.InvokeAsync(ctx);
        Assert.That(_nextCalled, Is.True);
        Assert.That(ctx.Response.StatusCode, Is.EqualTo(200));
    }

    [Test]
    public async Task ValidCookie_Passes() {
        // Cookie path: how iframes (phd2-gui, indi-web, sky) carry
        // auth on requests the JS layer never sees.
        var (mw, auth, _) = Make();
        auth.SetInitialPassword("hunter2!");
        var token = auth.Login("hunter2!", IPAddress.Loopback);
        var ctx = NewCtx("/sky/data/properties", cookieToken: token);
        await mw.InvokeAsync(ctx);
        Assert.That(_nextCalled, Is.True);
    }

    [Test]
    public async Task ValidQueryToken_Passes() {
        // Query path: how /api/files/download and other <img src=...>
        // links carry auth without a header.
        var (mw, auth, _) = Make();
        auth.SetInitialPassword("hunter2!");
        var token = auth.Login("hunter2!", IPAddress.Loopback);
        var ctx = NewCtx("/api/files/download", queryToken: token);
        await mw.InvokeAsync(ctx);
        Assert.That(_nextCalled, Is.True);
    }

    [Test]
    public async Task InvalidToken_Returns401() {
        var (mw, auth, _) = Make();
        auth.SetInitialPassword("hunter2!");
        var ctx = NewCtx("/api/camera/capture", bearer: "garbage");
        await mw.InvokeAsync(ctx);
        Assert.That(ctx.Response.StatusCode, Is.EqualTo(401));
    }

    [Test]
    public async Task LoopbackBypass_AllowsAccessWithoutToken() {
        // Quem ja esta no Pi (SSH tunnel, local script) e' confiavel.
        var (mw, auth, _) = Make();
        auth.SetInitialPassword("hunter2!");
        foreach (var ip in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }) {
            _nextCalled = false;
            var ctx = NewCtx("/api/camera/capture", remoteIp: ip);
            await mw.InvokeAsync(ctx);
            Assert.That(_nextCalled, Is.True,
                $"Loopback {ip} should bypass auth");
        }
    }

    [Test]
    public async Task AuthDisabled_PassesEverything() {
        var (mw, auth, profiles) = Make();
        auth.SetInitialPassword("hunter2!");
        Assert.That(auth.SetEnabled("hunter2!", false), Is.True);
        var ctx = NewCtx("/api/camera/capture");
        await mw.InvokeAsync(ctx);
        Assert.That(_nextCalled, Is.True);
        Assert.That(profiles.Active.AuthEnabled, Is.False);
    }

    [Test]
    public async Task Failed401_BodyIncludesAuthConfiguredFlag() {
        // The frontend reads { authConfigured } from the 401 body to
        // decide whether to show the first-run wizard or the login
        // form. Pin the shape.
        var (mw, auth, _) = Make();
        var ctx = NewCtx("/api/camera/capture");
        await mw.InvokeAsync(ctx);
        Assert.That(ctx.Response.StatusCode, Is.EqualTo(401));
        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.That(body, Does.Contain("\"error\":\"auth required\""));
        Assert.That(body, Does.Contain("\"authConfigured\":false"));
        // After setup the same 401 (still no token) reports configured=true
        auth.SetInitialPassword("hunter2!");
        var ctx2 = NewCtx("/api/camera/capture");
        await mw.InvokeAsync(ctx2);
        ctx2.Response.Body.Position = 0;
        var body2 = await new StreamReader(ctx2.Response.Body).ReadToEndAsync();
        Assert.That(body2, Does.Contain("\"authConfigured\":true"));
    }
}
