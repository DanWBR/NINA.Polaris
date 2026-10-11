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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.Forwarder;

namespace NINA.Polaris.Services;

/// <summary>Shared plumbing for the same-origin reverse proxies that put a
/// local helper's web UI behind Polaris's own origin and auth: the xpra HTML5
/// client (/phd2-gui), indi-web (/indi-web) and the Canopus agent (/canopus).
///
/// The upstreams have something in common that the defaults get wrong. They are
/// small single-threaded servers on loopback, and they serve one request at a
/// time behind a shallow accept queue: xpra's socket listener is a flat
/// <c>listener.socket.listen(5)</c>. A browser loading the xpra HTML5 client
/// asks for roughly forty scripts and stylesheets, and over HTTP/2 it asks for
/// all of them at once. Unbounded, the forwarder answers that by opening forty
/// TCP connections: the accept queue overflows, the server resets what it
/// cannot take, and those requests come back as
/// <see cref="ForwarderError.Request"/> ("connection reset by peer") which the
/// route turns into a 502.
///
/// The result is a page that loads with a quarter of its scripts missing. In
/// the PHD2 panel that showed up as a plain blue rectangle: the HTML5 client
/// booted far enough to paint the xpra desktop background and never far enough
/// to open its WebSocket, so no PHD2 window ever arrived. Nothing in the UI
/// said so, because every individual failure was a 502 on a script tag.
///
/// So requests are gated down to what the upstream answers cleanly, measured
/// rather than guessed, and one that still loses its connection before any
/// response is retried on a fresh one. Both halves had to be right: gating
/// alone still left a few per cent of requests failing, and the first version
/// of the retry handed YARP a response it had already written a 502 into,
/// which made it throw and turned a recoverable reset into a 500.</summary>
internal static class LoopbackProxy {

    /// <summary>How many ordinary requests may be in flight to one upstream.
    ///
    /// Measured on an Orange Pi 5 Pro, fetching the twenty largest scripts of
    /// the xpra HTML5 client through this proxy five times over at each level
    /// of concurrency: 0 failures in 100 at one request in flight, 0 in 100 at
    /// two, 2 in 100 at three, 5 in 100 at four, and about one in four lost
    /// when the browser's whole fan-out went through unbounded. xpra's HTTP
    /// server is single-threaded behind a <c>listen(5)</c>: past two requests
    /// at once it resets what it cannot take, and with a wide fan-out the
    /// accept queue overflows as well.
    ///
    /// So two, which is the widest setting the board answers cleanly. Forty
    /// files two at a time still load in a fraction of a second over
    /// loopback.</summary>
    internal const int MaxConcurrentRequests = 2;

    /// <summary>The connection cap on the handler. Deliberately well above
    /// <see cref="MaxConcurrentRequests"/>: the gate is what protects the
    /// upstream, and this only stops a runaway. It must not be the limit,
    /// because it also counts the connection each upgraded WebSocket holds
    /// for as long as its panel stays open, and a gate slot spent waiting
    /// behind those would deadlock the page.</summary>
    internal const int MaxUpstreamConnections = 16;

    /// <summary>Two retries, not one: a reset is a coin toss at this level of
    /// concurrency, and a script that 502s breaks the page it belongs to.
    /// Past that a failing upstream should be reported, not hammered.</summary>
    internal const int MaxAttempts = 3;

    /// <summary>Waited before a retry, multiplied by the attempt number. Long
    /// enough for a single-threaded upstream to finish what it was doing,
    /// short enough to be invisible on a page load.</summary>
    internal static readonly TimeSpan RetryBackoff = TimeSpan.FromMilliseconds(25);

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

    /// <summary>The gate a route holds for its upstream, created once at
    /// startup and shared by every request to it. A WebSocket upgrade does not
    /// take a slot: it would hold one for the life of the panel, and it is one
    /// connection either way.</summary>
    public static SemaphoreSlim NewGate() => new(MaxConcurrentRequests, MaxConcurrentRequests);

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

    /// <summary>A response YARP will accept for a forward. It refuses a
    /// response that has been touched at all, not just one that has started:
    /// a status other than 200, any header, or a content length is enough. A
    /// failed forward leaves exactly that behind, because YARP sets 502
    /// itself, so a retry has to hand it a pristine response first.
    ///
    /// Skipping this was a bug with a worse symptom than the one it was
    /// fixing: the second SendAsync threw "the request cannot be forwarded,
    /// the response has already started" and the asset came back 500 instead
    /// of retrying.</summary>
    private static void ResetResponse(HttpResponse response) {
        response.StatusCode = StatusCodes.Status200OK;
        response.Headers.Clear();
        response.ContentLength = null;
    }

    /// <summary>Forward the request, retrying when the upstream dropped the
    /// connection before answering, and write a 502 naming the proxy when it
    /// still failed and nothing has been sent yet.</summary>
    public static async Task<ForwarderError> ForwardAsync(
            IHttpForwarder forwarder, HttpContext ctx, string target,
            HttpMessageInvoker client, string label, SemaphoreSlim? gate = null) {
        // An upgrade is exempt: the forward does not return until the socket
        // closes, so a slot spent here never comes back.
        var gated = gate is not null && !ctx.WebSockets.IsWebSocketRequest;
        if (gated) await gate!.WaitAsync(ctx.RequestAborted);
        try {
            return await ForwardCoreAsync(forwarder, ctx, target, client, label);
        } finally {
            if (gated) gate!.Release();
        }
    }

    private static async Task<ForwarderError> ForwardCoreAsync(
            IHttpForwarder forwarder, HttpContext ctx, string target,
            HttpMessageInvoker client, string label) {
        var err = ForwarderError.None;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++) {
            if (attempt > 1) {
                // A reset here means the upstream was momentarily over its
                // head, so an instant retry can walk into the same wall. The
                // wait only ever applies to a request that already failed.
                await Task.Delay(RetryBackoff * (attempt - 1), ctx.RequestAborted);
                ResetResponse(ctx.Response);
            }
            try {
                err = await forwarder.SendAsync(ctx, target, client,
                    ForwarderRequestConfig.Empty, HttpTransformer.Default);
            } catch (InvalidOperationException) {
                // YARP would not take the response. Nothing useful is left to
                // try, and it must not surface as an unhandled 500.
                err = ForwarderError.Request;
                break;
            }
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
