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
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using NUnit.Framework;
using NINA.Polaris.Services;
using Yarp.ReverseProxy.Forwarder;

namespace NINA.Polaris.Test;

/// <summary>
/// The loopback reverse proxies (/phd2-gui, /indi-web, /canopus). The cap on
/// upstream connections and the retry exist because of a real failure: the xpra
/// HTML5 client asks for around forty files at once, the forwarder used to
/// answer with forty TCP connections to a single-threaded server behind a
/// listen(5), and the ones it could not take came back reset. A quarter of the
/// client's scripts 502'd and the PHD2 panel showed the xpra desktop background
/// and nothing else: a blue rectangle.
///
/// The retry then had to be fixed in turn, which is what
/// Forward_RetriesAResetOntoAPristineResponse pins down: YARP refuses a
/// response it has already written a 502 into, so retrying without resetting
/// it turned a recoverable reset into a 500.
/// </summary>
[TestFixture]
public class LoopbackProxyTests {

    private static HttpContext Ctx(string method = "GET", bool responseStarted = false) {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = "/js/Client.js";
        if (responseStarted) {
            ctx.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        }
        return ctx;
    }

    /// <summary>DefaultHttpContext always reports HasStarted false, so a
    /// response that has already begun has to be faked at the feature level.</summary>
    private sealed class StartedResponseFeature : HttpResponseFeature {
        public override bool HasStarted => true;
    }

    // ----- the connection cap -----

    [Test]
    public void Gate_AllowsWhatTheUpstreamAnswersCleanly() {
        Assert.That(LoopbackProxy.MaxConcurrentRequests, Is.EqualTo(2),
            "measured on the board: 0 failures in 100 at one and at two "
            + "requests in flight, 2 in 100 at three, 5 in 100 at four");
        using var gate = LoopbackProxy.NewGate();
        Assert.That(gate.CurrentCount, Is.EqualTo(LoopbackProxy.MaxConcurrentRequests));
    }

    /// <summary>The connection cap is a runaway stop, not the limit. It has to
    /// stay clear of the gate, because it also counts the connection every
    /// upgraded WebSocket holds open for the life of its panel.</summary>
    [Test]
    public void Handler_ConnectionCapLeavesRoomAboveTheGate() {
        Assert.That(LoopbackProxy.NewHandler().MaxConnectionsPerServer,
            Is.EqualTo(LoopbackProxy.MaxUpstreamConnections));
        Assert.That(LoopbackProxy.MaxUpstreamConnections,
            Is.GreaterThan(LoopbackProxy.MaxConcurrentRequests * 4));
    }

    [Test]
    public void Handler_CapIsOverridableForAnUpstreamThatAcceptsProperly() {
        Assert.That(LoopbackProxy.NewHandler(int.MaxValue).MaxConnectionsPerServer,
            Is.EqualTo(int.MaxValue));
    }

    [Test]
    public void Handler_IsAPassThrough() {
        var h = LoopbackProxy.NewHandler();
        Assert.Multiple(() => {
            Assert.That(h.UseCookies, Is.False, "the proxy must not eat Set-Cookie");
            Assert.That(h.AllowAutoRedirect, Is.False, "redirects belong to the client");
            Assert.That(h.AutomaticDecompression, Is.EqualTo(System.Net.DecompressionMethods.None));
            Assert.That(h.UseProxy, Is.False);
            Assert.That(h.ConnectTimeout, Is.EqualTo(TimeSpan.FromSeconds(5)));
        });
    }

    // ----- the retry -----

    /// <summary>The failure that started this: the upstream reset the
    /// connection before answering, which YARP reports as
    /// <see cref="ForwarderError.Request"/>.</summary>
    [Test]
    public void Retry_AGetThatLostItsConnectionBeforeAnyResponse() {
        Assert.That(LoopbackProxy.CanRetry(Ctx(), ForwarderError.Request), Is.True);
        Assert.That(LoopbackProxy.CanRetry(Ctx(), ForwarderError.RequestTimedOut), Is.True);
        Assert.That(LoopbackProxy.CanRetry(Ctx("HEAD"), ForwarderError.Request), Is.True);
        Assert.That(LoopbackProxy.CanRetry(Ctx("OPTIONS"), ForwarderError.Request), Is.True);
    }

    [Test]
    public void Retry_NotWhenTheForwardSucceeded() {
        Assert.That(LoopbackProxy.CanRetry(Ctx(), ForwarderError.None), Is.False);
    }

    /// <summary>Once bytes are on the wire there is nothing to retry into, and
    /// touching the status code would throw.</summary>
    [Test]
    public void Retry_NotOnceTheResponseHasStarted() {
        Assert.That(LoopbackProxy.CanRetry(Ctx(responseStarted: true), ForwarderError.Request),
            Is.False);
    }

    /// <summary>A write may have reached the upstream, so repeating it could
    /// apply it twice; and the request body is gone after the first attempt.</summary>
    [Test]
    public void Retry_NotForAMethodThatChangesSomething() {
        foreach (var method in new[] { "POST", "PUT", "PATCH", "DELETE" }) {
            Assert.That(LoopbackProxy.CanRetry(Ctx(method), ForwarderError.Request), Is.False,
                method);
        }
    }

    /// <summary>Errors that happened after the upstream started responding, or
    /// because the client went away, are not connection failures and retrying
    /// them fixes nothing.</summary>
    [Test]
    public void Retry_NotForErrorsThatAreNotALostConnection() {
        foreach (var err in new[] {
                ForwarderError.RequestCanceled,
                ForwarderError.RequestBodyClient,
                ForwarderError.ResponseBodyDestination,
                ForwarderError.UpgradeResponseDestination,
                ForwarderError.NoAvailableDestinations }) {
            Assert.That(LoopbackProxy.CanRetry(Ctx(), err), Is.False, err.ToString());
        }
    }

    [Test]
    public void Retry_IsBounded() {
        Assert.That(LoopbackProxy.MaxAttempts, Is.InRange(2, 3),
            "retry a reset, but do not hammer a failing upstream");
    }

    // ----- forwarding, against a stand-in that behaves like YARP -----

    /// <summary>What YARP's own forwarder does, in the two ways that matter
    /// here: it sets 502 on the response when a request fails, and it REFUSES
    /// a response that has been touched at all, throwing
    /// InvalidOperationException rather than forwarding. The real one checks
    /// for a non-200 status, any header, or a content length, not merely
    /// HasStarted.</summary>
    private sealed class FakeForwarder : IHttpForwarder {
        private readonly Queue<ForwarderError> _results;
        public int Calls { get; private set; }

        public FakeForwarder(params ForwarderError[] results)
            => _results = new Queue<ForwarderError>(results);

        public ValueTask<ForwarderError> SendAsync(HttpContext context, string destinationPrefix,
                HttpMessageInvoker httpClient, ForwarderRequestConfig requestConfig,
                HttpTransformer transformer, CancellationToken cancellationToken) {
            Calls++;
            if (context.Response.StatusCode != StatusCodes.Status200OK
                    || context.Response.Headers.Count > 0
                    || context.Response.ContentLength.HasValue) {
                throw new InvalidOperationException(
                    "The request cannot be forwarded, the response has already started");
            }
            var err = _results.Count > 0 ? _results.Dequeue() : ForwarderError.None;
            if (err != ForwarderError.None) {
                // YARP's own failure path, and the thing that made a naive
                // retry throw: the response is no longer pristine.
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
            }
            return new ValueTask<ForwarderError>(err);
        }

        public ValueTask<ForwarderError> SendAsync(HttpContext context, string destinationPrefix,
                HttpMessageInvoker httpClient, ForwarderRequestConfig requestConfig,
                HttpTransformer transformer)
            => SendAsync(context, destinationPrefix, httpClient, requestConfig, transformer,
                CancellationToken.None);

        public ValueTask<ForwarderError> SendAsync(HttpContext context, string destinationPrefix,
                HttpMessageInvoker httpClient, ForwarderRequestConfig requestConfig)
            => SendAsync(context, destinationPrefix, httpClient, requestConfig,
                HttpTransformer.Default, CancellationToken.None);

        public ValueTask<ForwarderError> SendAsync(HttpContext context, string destinationPrefix,
                HttpMessageInvoker httpClient)
            => SendAsync(context, destinationPrefix, httpClient, ForwarderRequestConfig.Empty,
                HttpTransformer.Default, CancellationToken.None);
    }

    private static async Task<(ForwarderError Err, HttpContext Ctx, FakeForwarder Fwd)> Forward(
            params ForwarderError[] results) {
        var fwd = new FakeForwarder(results);
        var ctx = Ctx();
        ctx.Response.Body = new System.IO.MemoryStream();
        using var gate = LoopbackProxy.NewGate();
        var err = await LoopbackProxy.ForwardAsync(fwd, ctx, "http://127.0.0.1:14600",
            new HttpMessageInvoker(new SocketsHttpHandler()), "xpra", gate);
        Assert.That(gate.CurrentCount, Is.EqualTo(LoopbackProxy.MaxConcurrentRequests),
            "the slot has to come back, however the forward ended");
        return (err, ctx, fwd);
    }

    /// <summary>A third request waits for a slot instead of reaching the
    /// upstream, which is the whole point of the gate.</summary>
    [Test]
    public async Task Gate_HoldsBackAThirdRequest() {
        using var gate = LoopbackProxy.NewGate();
        var fwd = new FakeForwarder();
        await gate.WaitAsync();
        await gate.WaitAsync();   // both slots taken by requests in flight
        var ctx = Ctx();
        ctx.Response.Body = new System.IO.MemoryStream();
        var pending = LoopbackProxy.ForwardAsync(fwd, ctx, "http://127.0.0.1:14600",
            new HttpMessageInvoker(new SocketsHttpHandler()), "xpra", gate);
        await Task.Delay(50);
        Assert.That(pending.IsCompleted, Is.False, "queued, not forwarded");
        Assert.That(fwd.Calls, Is.EqualTo(0));
        gate.Release();
        Assert.That(await pending, Is.EqualTo(ForwarderError.None));
        Assert.That(fwd.Calls, Is.EqualTo(1));
    }

    /// <summary>A WebSocket upgrade does not take a slot: the forward lasts as
    /// long as the panel is open, so a slot spent on it never comes back and
    /// the page behind it would never load.</summary>
    [Test]
    public async Task Gate_DoesNotHoldBackAWebSocketUpgrade() {
        using var gate = LoopbackProxy.NewGate();
        await gate.WaitAsync();
        await gate.WaitAsync();
        var ctx = Ctx();
        ctx.Response.Body = new System.IO.MemoryStream();
        ctx.Request.Headers.Connection = "Upgrade";
        ctx.Request.Headers.Upgrade = "websocket";
        ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpWebSocketFeature>(
            new UpgradeRequestFeature());
        var fwd = new FakeForwarder();
        Assert.That(await LoopbackProxy.ForwardAsync(fwd, ctx, "http://127.0.0.1:14600",
            new HttpMessageInvoker(new SocketsHttpHandler()), "xpra", gate),
            Is.EqualTo(ForwarderError.None));
        Assert.That(fwd.Calls, Is.EqualTo(1), "forwarded with both slots taken");
    }

    private sealed class UpgradeRequestFeature : Microsoft.AspNetCore.Http.Features.IHttpWebSocketFeature {
        public bool IsWebSocketRequest => true;
        public Task<System.Net.WebSockets.WebSocket> AcceptAsync(WebSocketAcceptContext context)
            => throw new NotSupportedException();
    }

    /// <summary>The regression. A reset on the first attempt used to come back
    /// as a 500 with "the request cannot be forwarded, the response has
    /// already started", because the retry handed YARP the response YARP had
    /// just set to 502. Now the response is reset first and the retry lands.</summary>
    [Test]
    public async Task Forward_RetriesAResetOntoAPristineResponse() {
        var (err, ctx, fwd) = await Forward(ForwarderError.Request, ForwarderError.None);
        Assert.Multiple(() => {
            Assert.That(err, Is.EqualTo(ForwarderError.None), "the retry succeeded");
            Assert.That(fwd.Calls, Is.EqualTo(2));
            Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK),
                "and the 502 from the failed attempt is gone");
        });
    }

    [Test]
    public async Task Forward_DoesNotRetryASuccess() {
        var (err, _, fwd) = await Forward(ForwarderError.None);
        Assert.That(err, Is.EqualTo(ForwarderError.None));
        Assert.That(fwd.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task Forward_GivesUpWithA502AfterMaxAttempts() {
        var (err, ctx, fwd) = await Forward(
            ForwarderError.Request, ForwarderError.Request, ForwarderError.Request,
            ForwarderError.Request);
        Assert.Multiple(() => {
            Assert.That(fwd.Calls, Is.EqualTo(LoopbackProxy.MaxAttempts), "bounded");
            Assert.That(err, Is.EqualTo(ForwarderError.Request));
            Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status502BadGateway),
                "reported as a gateway error, never as a 500");
        });
    }

    [Test]
    public async Task Forward_DoesNotRetryAnErrorThatIsNotALostConnection() {
        var (err, _, fwd) = await Forward(ForwarderError.RequestCanceled, ForwarderError.None);
        Assert.That(fwd.Calls, Is.EqualTo(1));
        Assert.That(err, Is.EqualTo(ForwarderError.RequestCanceled));
    }
}
