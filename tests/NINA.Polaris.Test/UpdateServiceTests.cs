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
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;
using NINA.Polaris.Services.External;

namespace NINA.Polaris.Test;

/// <summary>
/// UpdateService is the SBC .deb self-updater. These tests pin the
/// platform/arch logic and the safe behaviour off a .deb install — the
/// network check + privileged install path are exercised manually on a
/// real Pi, since they touch GitHub and systemd.
/// </summary>
[TestFixture]
public class UpdateServiceTests {

    private static UpdateService Make() {
        // No HttpClient is needed for the supported/version/arch checks; pass a
        // factory that throws if actually used so the tests stay offline. The
        // ProfileService is an empty in-memory config: these checks don't read
        // the update channel off it.
        var profiles = new ProfileService(new ConfigurationBuilder().Build(),
            NullLogger<ProfileService>.Instance);
        return new UpdateService(NullLogger<UpdateService>.Instance,
            new ThrowingHttpClientFactory(), profiles);
    }

    [Test]
    public void CurrentVersion_is_non_null() {
        Assert.That(UpdateService.CurrentVersion, Is.Not.Null);
    }

    [Test]
    public void DpkgArch_maps_known_architectures() {
        // Whatever this test host is, the result must be a non-empty lowercase
        // token and one of the dpkg names for the common arches.
        var arch = UpdateService.DpkgArch;
        Assert.That(arch, Is.Not.Null.And.Not.Empty);
        Assert.That(arch, Is.EqualTo(arch.ToLowerInvariant()));

        var expected = RuntimeInformation.ProcessArchitecture switch {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "amd64",
            Architecture.Arm => "armhf",
            Architecture.X86 => "i386",
            _ => arch
        };
        Assert.That(arch, Is.EqualTo(expected));
    }

    [Test]
    public void CandidateBaseTags_includes_3part_tag_for_4part_assembly_version() {
        // Regression: release tags are 3-part (v0.84.8) but the assembly
        // version is normalised to 4 parts (0.84.8.0). "v"+version → v0.84.8.0
        // doesn't exist → compare 404 → "changelog unavailable". The candidate
        // list must include the 3-part spelling so the changelog resolves.
        var cands = UpdateService.CandidateBaseTags("v", new Version(0, 84, 8, 0)).ToList();
        Assert.That(cands, Does.Contain("v0.84.8.0"));  // 4-part (kept for safety)
        Assert.That(cands, Does.Contain("v0.84.8"));    // 3-part (the real tag)
        Assert.That(cands, Does.Contain("v0.84"));      // 2-part fallback
        // Most-specific first, de-duplicated.
        Assert.That(cands[0], Is.EqualTo("v0.84.8.0"));
        Assert.That(cands, Is.Unique);
    }

    [Test]
    public void CandidateBaseTags_honours_empty_prefix() {
        var cands = UpdateService.CandidateBaseTags("", new Version(1, 2, 3, 0)).ToList();
        Assert.That(cands, Does.Contain("1.2.3"));
        Assert.That(cands, Has.None.StartWith("v"));
    }

    [Test]
    public void IsSupported_is_false_off_a_deb_install() {
        // The CI / dev host is not a /opt/polaris .deb layout, so the feature
        // must report unsupported (Windows always; Linux dev boxes too).
        var svc = Make();
        Assert.That(svc.IsSupported, Is.False);
    }

    [Test]
    public async System.Threading.Tasks.Task CheckAsync_returns_unsupported_without_touching_network() {
        var svc = Make();
        var r = await svc.CheckAsync(force: true, CancellationToken.None);
        Assert.That(r.Supported, Is.False);
        Assert.That(r.UpdateAvailable, Is.False);
        Assert.That(r.CurrentVersion, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async System.Threading.Tasks.Task InstallAsync_refuses_off_a_deb_install() {
        var svc = Make();
        var (ok, error) = await svc.InstallAsync(CancellationToken.None);
        Assert.That(ok, Is.False);
        Assert.That(error, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async System.Threading.Tasks.Task ListReleasesAsync_returns_empty_off_a_deb_install() {
        // Off a .deb install the rollback list short-circuits to empty before
        // any network call (the ThrowingHttpClientFactory would blow up if hit).
        var svc = Make();
        var list = await svc.ListReleasesAsync(15, force: true, CancellationToken.None);
        Assert.That(list, Is.Not.Null.And.Empty);
    }

    [Test]
    public async System.Threading.Tasks.Task InstallVersionAsync_refuses_off_a_deb_install() {
        var svc = Make();
        var (ok, error) = await svc.InstallVersionAsync("0.84.5", CancellationToken.None);
        Assert.That(ok, Is.False);
        Assert.That(error, Is.Not.Null.And.Not.Empty);
    }

    // ---- GitHub answering slowly or not at all ------------------------------

    private static UpdateService MakeSupported(ScriptedHandler handler) {
        var profiles = new ProfileService(new ConfigurationBuilder().Build(),
            NullLogger<ProfileService>.Instance);
        return new UpdateService(NullLogger<UpdateService>.Instance,
            new HandlerFactory(handler), profiles) { SupportedOverride = true };
    }

    private static string LatestReleaseJson() =>
        "{\"tag_name\":\"v99.0.0\",\"name\":\"v99.0.0\",\"prerelease\":false,"
        + "\"html_url\":\"https://example.test/rel\",\"published_at\":\"2026-09-18T00:00:00Z\",\"body\":\"\","
        + "\"assets\":[{\"name\":\"polaris_99.0.0_" + UpdateService.DpkgArch + ".deb\","
        + "\"browser_download_url\":\"https://example.test/polaris.deb\",\"size\":123}]}";

    [Test]
    public async System.Threading.Tasks.Task CheckAsync_reports_a_timeout_instead_of_throwing() {
        // HttpClient.Timeout surfaces as TaskCanceledException, which used to
        // escape CheckAsync as if the caller had cancelled and turned the
        // install POST into an HTTP 500.
        var handler = new ScriptedHandler(_ => throw new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 15 seconds elapsing."));
        var svc = MakeSupported(handler);

        var r = await svc.CheckAsync(force: true, CancellationToken.None);

        Assert.That(r.UpdateAvailable, Is.False);
        Assert.That(r.Error, Does.Contain("did not answer"));
    }

    [Test]
    public void CheckAsync_still_propagates_the_callers_cancellation() {
        using var cts = new CancellationTokenSource();
        var handler = new ScriptedHandler(_ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });
        var svc = MakeSupported(handler);

        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await svc.CheckAsync(force: true, cts.Token));
    }

    [Test]
    public async System.Threading.Tasks.Task InstallAsync_uses_the_last_offered_update_when_the_fresh_check_fails() {
        // The badge came from a good check; GitHub then times out on the check
        // the install repeats. The install must go on to the download (which
        // here answers 404, proving the check was passed) rather than stop.
        var calls = 0;
        var handler = new ScriptedHandler(req => {
            calls++;
            if (req.RequestUri!.Host == "api.github.com") {
                if (calls == 1) return Json(LatestReleaseJson());
                throw new TaskCanceledException("HttpClient.Timeout elapsed");
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });
        var svc = MakeSupported(handler);

        var first = await svc.CheckAsync(force: true, CancellationToken.None);
        Assert.That(first.UpdateAvailable, Is.True, first.Error);

        var (ok, error) = await svc.InstallAsync(CancellationToken.None);

        Assert.That(ok, Is.False);
        Assert.That(error, Is.EqualTo("Download failed: HTTP 404"));
        Assert.That(handler.Requested.Any(u => u.Contains("polaris.deb")), Is.True,
            "the download was never attempted: " + string.Join(", ", handler.Requested));
    }

    private static HttpResponseMessage Json(string body) => new(System.Net.HttpStatusCode.OK) {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class ScriptedHandler : HttpMessageHandler {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _script;
        public List<string> Requested { get; } = new();
        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> script) { _script = script; }
        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) {
            Requested.Add(request.RequestUri!.ToString());
            return System.Threading.Tasks.Task.FromResult(_script(request));
        }
    }

    private sealed class HandlerFactory : IHttpClientFactory {
        private readonly HttpMessageHandler _handler;
        public HandlerFactory(HttpMessageHandler handler) { _handler = handler; }
        public System.Net.Http.HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory {
        public System.Net.Http.HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("network must not be used in this test");
    }
}
