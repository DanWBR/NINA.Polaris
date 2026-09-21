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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using NUnit.Framework;
using NINA.Polaris.Services;
using Yarp.ReverseProxy.Forwarder;

namespace NINA.Polaris.Test;

/// <summary>
/// The loopback reverse proxies (/phd2-gui, /indi-web, /canopus). The cap on
/// upstream connections and the one retry exist because of a real failure: the
/// xpra HTML5 client asks for around forty files at once, the forwarder used to
/// answer with forty TCP connections to a listener whose backlog is five, and
/// the connections the accept queue could not hold came back reset. A quarter
/// of the client's scripts 502'd and the PHD2 panel showed the xpra desktop
/// background and nothing else: a blue rectangle.
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
    public void Handler_CapsConnectionsToWhatASmallUpstreamCanAccept() {
        Assert.That(LoopbackProxy.NewHandler().MaxConnectionsPerServer,
            Is.EqualTo(LoopbackProxy.MaxUpstreamConnections));
        Assert.That(LoopbackProxy.MaxUpstreamConnections, Is.InRange(2, 6),
            "measured on the board: 6 parallel requests overflowed xpra's "
            + "listen(5) queue zero times, 8 overflowed it");
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
    public void Retry_HappensAtMostOnce() {
        Assert.That(LoopbackProxy.MaxAttempts, Is.EqualTo(2),
            "one retry, so a failing upstream is reported rather than hammered");
    }
}
