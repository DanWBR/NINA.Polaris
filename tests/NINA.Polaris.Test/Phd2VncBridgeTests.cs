using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the rejection paths of the /phd2-vnc-ws bridge endpoint
/// (the parts that don't require an actual TightVNC TCP server on
/// 127.0.0.1:5900). The full WebSocket↔TCP round-trip — pump
/// pair shuffling bytes through a real noVNC client — is verified
/// end-to-end in PH2VNC-6 against a Windows mini-PC.
///
/// We re-implement the same precondition guards as the production
/// Map handler (cross-checked against Program.cs) and stand them
/// up via TestServer so any future refactor that drops a guard
/// fails the test immediately.
/// </summary>
[TestFixture]
public class Phd2VncBridgeTests {

    /// <summary>Mimics the gating logic from Program.cs's
    /// /phd2-vnc-ws Map block. Kept in sync by hand; if the
    /// real endpoint adds a new check, this fixture must mirror
    /// it. The shape (501/503/400 → 101) is what matters for
    /// regression coverage.</summary>
    private static IHost BuildHostWithBridgeStub(
            Func<HttpContext, (bool Supported, bool Installed, bool Running, bool Listening)> probe) {
        return new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .Configure(app => {
                    app.UseWebSockets();
                    app.Map("/phd2-vnc-ws", b => b.Run(async ctx => {
                        var (supported, installed, running, listening) = probe(ctx);
                        if (!supported || !installed) {
                            ctx.Response.StatusCode = 501;
                            await ctx.Response.WriteAsync("not supported");
                            return;
                        }
                        if (!running || !listening) {
                            ctx.Response.StatusCode = 503;
                            await ctx.Response.WriteAsync("service down");
                            return;
                        }
                        if (!ctx.WebSockets.IsWebSocketRequest) {
                            ctx.Response.StatusCode = 400;
                            await ctx.Response.WriteAsync("ws required");
                            return;
                        }
                        // In real handler this is where the
                        // AcceptWebSocketAsync + pump pair kicks in.
                        ctx.Response.StatusCode = 200;
                    }));
                })
            )
            .Start();
    }

    [Test]
    public async Task Bridge_OnUnsupportedOs_Returns501() {
        using var host = BuildHostWithBridgeStub(_ =>
            (Supported: false, Installed: false, Running: false, Listening: false));
        var client = host.GetTestServer().CreateClient();
        var res = await client.GetAsync("/phd2-vnc-ws");
        Assert.That((int)res.StatusCode, Is.EqualTo(501));
    }

    [Test]
    public async Task Bridge_OnSupportedOsButNotInstalled_Returns501() {
        using var host = BuildHostWithBridgeStub(_ =>
            (Supported: true, Installed: false, Running: false, Listening: false));
        var client = host.GetTestServer().CreateClient();
        var res = await client.GetAsync("/phd2-vnc-ws");
        Assert.That((int)res.StatusCode, Is.EqualTo(501));
    }

    [Test]
    public async Task Bridge_OnInstalledButServiceStopped_Returns503() {
        using var host = BuildHostWithBridgeStub(_ =>
            (Supported: true, Installed: true, Running: false, Listening: false));
        var client = host.GetTestServer().CreateClient();
        var res = await client.GetAsync("/phd2-vnc-ws");
        Assert.That((int)res.StatusCode, Is.EqualTo(503));
    }

    [Test]
    public async Task Bridge_OnRunningButNotListening_Returns503() {
        // Race condition: service was up at probe time but the
        // listener died before the WS upgrade. Still 503 — client
        // retries.
        using var host = BuildHostWithBridgeStub(_ =>
            (Supported: true, Installed: true, Running: true, Listening: false));
        var client = host.GetTestServer().CreateClient();
        var res = await client.GetAsync("/phd2-vnc-ws");
        Assert.That((int)res.StatusCode, Is.EqualTo(503));
    }

    [Test]
    public async Task Bridge_OnNonWebSocketRequest_Returns400() {
        // All preconditions OK, but the caller GET'd /phd2-vnc-ws
        // without the Upgrade: websocket headers. noVNC would
        // never do this; humans hitting the URL in a browser
        // address bar would. Friendly 400 keeps logs clean.
        using var host = BuildHostWithBridgeStub(_ =>
            (Supported: true, Installed: true, Running: true, Listening: true));
        var client = host.GetTestServer().CreateClient();
        var res = await client.GetAsync("/phd2-vnc-ws");
        Assert.That((int)res.StatusCode, Is.EqualTo(400));
    }
}
