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

using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.Forwarder;

namespace NINA.Polaris.Services;

/// <summary>Shared plumbing for the same-origin reverse proxies that put a
/// local helper's web UI behind Polaris's own origin and auth: the xpra HTML5
/// client (/phd2-gui), indi-web (/indi-web) and the Canopus agent (/canopus).
///
/// The upstreams have something in common that the defaults get wrong. They are
/// small single-purpose servers on loopback, and they accept connections
/// modestly: xpra's socket listener, for one, is a flat
/// <c>listener.socket.listen(5)</c>. A browser loading the xpra HTML5 client
/// asks for roughly forty scripts and stylesheets, and over HTTP/2 it asks for
/// all of them at once. With an unbounded connection pool the forwarder answers
/// that by opening forty TCP connections to a listener five deep: the accept
/// queue overflows, the kernel resets the connections it could not queue, and
/// those requests come back as
/// <see cref="ForwarderError.Request"/> ("connection reset by peer") which the
/// route turns into a 502.
///
/// The result is a page that loads with a quarter of its scripts missing. In
/// the PHD2 panel that showed up as a plain blue rectangle: the HTML5 client
/// booted far enough to paint the xpra desktop background and never far enough
/// to open its WebSocket, so no PHD2 window ever arrived. Nothing in the UI
/// said so, because every individual failure was a 502 on a script tag.
///
/// So the fan-out is capped to what these servers can accept, and a safe
/// request that still loses its connection before any response is retried once
/// on a fresh one.</summary>
internal static class LoopbackProxy {

    /// <summary>At most this many connections to one upstream at a time.
    ///
    /// Measured against xpra's backlog of five, loading the twenty largest
    /// scripts of its HTML5 client on an Orange Pi 5 Pro: four, five and six
    /// in parallel overflowed the accept queue zero times, eight overflowed it
    /// once and twelve five times. Six keeps the page loading in parallel,
    /// stays inside what the listener can queue, and leaves slots free for the
    /// long-lived WebSocket a panel holds open. Requests past the cap wait in
    /// the handler instead of becoming connections the upstream will drop.</summary>
    internal const int MaxUpstreamConnections = 6;

    /// <summary>One attempt after the first, and only one: past that, a
    /// failing upstream should be reported, not hammered.</summary>
    internal const int MaxAttempts = 2;

    /// <summary>The invoker every loopback proxy route shares the shape of.
    /// Cookies, redirects and decompression stay off so the proxy is a
    /// pass-through; the connection cap is the part that matters.</summary>
    /// <param name="maxConnections">Override the cap for an upstream that
    /// accepts connections properly, such as the uvicorn the Canopus agent
    /// runs on (backlog 2048), where capping would only get in the way.</param>
    public static HttpMessageInvoker NewInvoker(
            int maxConnections = MaxUpstreamConnections) => new(NewHandler(maxConnections));

    /// <summary>The handler behind <see cref="NewInvoker"/>, so a test can read
    /// back the cap that is the whole point of it.</summary>
    internal static SocketsHttpHandler NewHandler(
            int maxConnections = MaxUpstreamConnections) => new() {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        UseCookies = false,
        EnableMultipleHttp2Connections = true,
        MaxConnectionsPerServer = maxConnections,
        ActivityHeadersPropagator = new ReverseProxyPropagator(
            System.Diagnostics.DistributedContextPropagator.Current),
        ConnectTimeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>Whether a failed forward may be tried again.
    ///
    /// Three things have to hold. The response must not have started, or there
    /// is nothing left to retry into. The error must be one that happened
    /// before any response came back, so the upstream cannot have acted on the
    /// request twice. And the method must be one that is safe to repeat, which
    /// also spares us the question of whether the request body is still
    /// readable after the first attempt consumed it.</summary>
    internal static bool CanRetry(HttpContext ctx, ForwarderError err) {
        if (err == ForwarderError.None || ctx.Response.HasStarted) return false;
        if (err is not (ForwarderError.Request or ForwarderError.RequestTimedOut)) return false;
        return HttpMethods.IsGet(ctx.Request.Method)
            || HttpMethods.IsHead(ctx.Request.Method)
            || HttpMethods.IsOptions(ctx.Request.Method);
    }

    /// <summary>Forward the request, retrying once when the upstream dropped
    /// the connection before answering, and write a 502 naming the proxy when
    /// it still failed and nothing has been sent yet.</summary>
    public static async Task<ForwarderError> ForwardAsync(
            IHttpForwarder forwarder, HttpContext ctx, string target,
            HttpMessageInvoker client, string label) {
        var err = ForwarderError.None;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++) {
            err = await forwarder.SendAsync(ctx, target, client,
                ForwarderRequestConfig.Empty, HttpTransformer.Default);
            if (!CanRetry(ctx, err)) break;
        }
        // Only write a 502 if nothing was sent yet: a mid-stream forwarder
        // error (client aborted, upstream dropped) means the response already
        // started, and setting StatusCode then throws "response has already
        // started".
        if (err != ForwarderError.None && !ctx.Response.HasStarted) {
            ctx.Response.StatusCode = 502;
            await ctx.Response.WriteAsync($"{label} proxy error: {err}");
        }
        return err;
    }
}
