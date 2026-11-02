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
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;
using NINA.Polaris.Services.External;

namespace NINA.Polaris.Test;

/// <summary>The portable Windows self-update (UpdateService.Windows.cs): asset
/// choice, the settings merge, and every refusal that has to happen before the
/// worker is started.</summary>
[TestFixture]
public class UpdateServiceWindowsTests {
    private string _root = null!;

    [SetUp]
    public void SetUp() {
        _root = Path.Combine(Path.GetTempPath(), "polaris-winupd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_root, true); } catch { }
    }

    private UpdateService Make(Func<HttpRequestMessage, HttpResponseMessage>? script = null) {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Profiles:Directory"] = Path.Combine(_root, "profiles")
        }).Build();
        var profiles = new ProfileService(config, NullLogger<ProfileService>.Instance);
        return new UpdateService(NullLogger<UpdateService>.Instance,
            new Factory(script ?? (_ => throw new InvalidOperationException("no network in this test"))),
            profiles) { SupportedOverride = true, WindowsOverride = true };
    }

    private static string Release(bool withDigest, string sha = "ab12") =>
        "{\"tag_name\":\"v99.0.0\",\"name\":\"v99.0.0\",\"prerelease\":false,\"body\":\"\","
        + "\"html_url\":\"https://example.test/rel\",\"published_at\":\"2026-11-02T00:00:00Z\",\"assets\":["
        + "{\"name\":\"polaris_99.0.0_amd64.deb\",\"browser_download_url\":\"https://example.test/p.deb\",\"size\":5},"
        + "{\"name\":\"polaris-win-arm64.zip\",\"browser_download_url\":\"https://example.test/arm.zip\",\"size\":6},"
        + "{\"name\":\"polaris-win-x64.zip\",\"browser_download_url\":\"https://example.test/x64.zip\",\"size\":7"
        + (withDigest ? ",\"digest\":\"sha256:" + sha.ToUpperInvariant() + "\"" : "") + "}]}";

    [Test]
    public void PickArchAsset_OnWindows_TakesThisArchitecturesZipAndItsDigest() {
        var svc = Make();
        using var doc = JsonDocument.Parse(Release(withDigest: true));
        var (name, url, size, sha) = svc.PickArchAsset(doc.RootElement);
        Assert.That(name, Is.EqualTo($"polaris-{UpdateService.WinRid}.zip"));
        Assert.That(url, Does.EndWith(UpdateService.WinRid == "win-arm64" ? "arm.zip" : "x64.zip"));
        if (UpdateService.WinRid == "win-x64") {
            Assert.That(size, Is.EqualTo(7));
            Assert.That(sha, Is.EqualTo("ab12"), "lowercased, without the sha256: prefix");
        }
    }

    [Test]
    public void PickArchAsset_OnADebInstall_StillTakesTheDeb() {
        var svc = Make();
        svc.WindowsOverride = false;
        using var doc = JsonDocument.Parse(Release(withDigest: true).Replace("amd64", UpdateService.DpkgArch));
        var (name, _, _, _) = svc.PickArchAsset(doc.RootElement);
        Assert.That(name, Does.EndWith(".deb"));
    }

    [Test]
    public async Task Check_OnWindows_ReportsThePlatformAndTheZip() {
        var svc = Make(_ => Ok(Release(withDigest: true)));
        var r = await svc.CheckAsync(force: true, CancellationToken.None);
        Assert.That(r.Platform, Is.EqualTo("windows"));
        Assert.That(r.Arch, Is.EqualTo(UpdateService.WinRid));
        Assert.That(r.AssetName, Does.EndWith(".zip"));
        Assert.That(r.UpdateAvailable, Is.True, r.Error);
    }

    [Test]
    public async Task Install_WithoutAGitHubDigest_RefusesBeforeDownloading() {
        var requested = new List<string>();
        var svc = Make(req => {
            requested.Add(req.RequestUri!.ToString());
            return Ok(Release(withDigest: false));
        });
        var (ok, error) = await svc.InstallAsync(CancellationToken.None);
        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("SHA-256"));
        Assert.That(requested.Any(u => u.EndsWith(".zip")), Is.False, "nothing may be downloaded");
    }

    [Test]
    public async Task UploadedDeb_IsRefusedOnWindows() {
        var svc = Make();
        var (ok, error) = await svc.InstallFromUploadAsync(new MemoryStream(new byte[10]), 10,
            new string('a', 64), CancellationToken.None);
        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain(".deb"));
        Assert.That(svc.LocalInfo().Supported, Is.False, "the browser relay sideloads a .deb");
    }

    [Test]
    public void Progress_IsNullBeforeAnyUpdate() {
        Assert.That(Make().WindowsProgress(), Is.Null);
    }

    // ---- appsettings merge ---------------------------------------------

    [Test]
    public void Merge_KeepsTheOperatorsValuesAndAddsNewSettings() {
        const string shipped = """
            {
              // shipped with the new release
              "Server": { "Https": { "Port": 5000 }, "Http": { "Port": 5080 }, "NewFlag": true },
              "PlateSolve": { "AstrometryApiKey": "" },
              "Brand": "new"
            }
            """;
        const string current = """
            {
              "Server": { "Https": { "Port": 5443 } },
              "PlateSolve": { "AstrometryApiKey": "secret", },
              "Custom": [1, 2]
            }
            """;
        var merged = JsonNode.Parse(UpdateService.MergeAppSettings(shipped, current))!;
        Assert.That((int)merged["Server"]!["Https"]!["Port"]!, Is.EqualTo(5443), "the operator's port");
        Assert.That((int)merged["Server"]!["Http"]!["Port"]!, Is.EqualTo(5080), "untouched default");
        Assert.That((bool)merged["Server"]!["NewFlag"]!, Is.True, "a new setting arrives");
        Assert.That((string)merged["PlateSolve"]!["AstrometryApiKey"]!, Is.EqualTo("secret"));
        Assert.That((string)merged["Brand"]!, Is.EqualTo("new"));
        Assert.That(merged["Custom"]!.AsArray().Count, Is.EqualTo(2), "a key only the operator has stays");
    }

    [Test]
    public void Merge_KeepsAFileThatDoesNotParseVerbatim() {
        const string broken = "{ \"Server\": { ";
        Assert.That(UpdateService.MergeAppSettings("{\"A\":1}", broken), Is.EqualTo(broken));
    }

    private static HttpResponseMessage Ok(string json) => new(System.Net.HttpStatusCode.OK) {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class Factory : IHttpClientFactory {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _script;
        public Factory(Func<HttpRequestMessage, HttpResponseMessage> script) { _script = script; }
        public HttpClient CreateClient(string name) => new(new Handler(_script), disposeHandler: true);
    }

    private sealed class Handler : HttpMessageHandler {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _script;
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> script) { _script = script; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_script(request));
    }
}
