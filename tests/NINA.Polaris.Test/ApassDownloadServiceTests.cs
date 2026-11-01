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
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Sky;

namespace NINA.Polaris.Test;

/// <summary>
/// Where the APASS download comes from. Issue #62: with CDS unreachable the
/// TAP query timed out and the catalog could not be installed at all; the
/// data-pack file is now tried first and TAP only when it is unavailable.
/// </summary>
[TestFixture]
public class ApassDownloadServiceTests {
    private string _root = null!;
    private ApassCatalog _catalog = null!;

    [SetUp]
    public void SetUp() {
        _root = Path.Combine(Path.GetTempPath(), "apass-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "www"));
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Profiles:Directory"] = Path.Combine(_root, "profiles")
        }).Build();
        var profiles = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        _catalog = new ApassCatalog(new Env(Path.Combine(_root, "www")), profiles,
            NullLogger<ApassCatalog>.Instance);
    }

    [TearDown]
    public void TearDown() {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private const string Rows =
        "RAJ2000\tDEJ2000\tVmag\tBmag\tB-V\n" +
        "10.5\t-20.25\t11.2\t11.9\t0.7\n" +
        "200.1\t45.0\t12.4\t\t\n" +
        "300.0\t80.0\t\t13.0\t\n";   // no V: skipped

    private static byte[] Gzip(string text) {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest))
            gz.Write(Encoding.ASCII.GetBytes(text));
        return ms.ToArray();
    }

    private async Task<ApassDownloadService.StatusDto> Run(Handler handler) {
        var svc = new ApassDownloadService(new Factory(handler), _catalog,
            NullLogger<ApassDownloadService>.Instance) { PackMinStars = 2 };
        Assert.That(svc.Start(), Is.True);
        for (int i = 0; i < 600 && svc.Status().State == "running"; i++) await Task.Delay(50);
        return svc.Status();
    }

    [Test]
    public async Task ThePackIsUsedWhenItIsThere() {
        var h = new Handler(req => req.RequestUri!.ToString() == ApassDownloadService.PackUrl
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Gzip(Rows)) }
            : throw new HttpRequestException("TAP must not be queried"));

        var s = await Run(h);

        Assert.That(s.State, Is.EqualTo("done"), s.Message);
        Assert.That(s.Stars, Is.EqualTo(2));
        Assert.That(h.TapCalls, Is.Zero);
        Assert.That(File.Exists(_catalog.WritableDbPath), Is.True);
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(_catalog.WritableDbPath)!), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task VizierIsQueriedWhenThePackIsMissing() {
        var h = new Handler(req => req.RequestUri!.ToString() == ApassDownloadService.PackUrl
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Rows) });

        var s = await Run(h);

        Assert.That(s.State, Is.EqualTo("done"), s.Message);
        Assert.That(h.TapCalls, Is.EqualTo(36));
        Assert.That(s.Stars, Is.EqualTo(2 * 36));
    }

    [Test]
    public async Task NeitherSourceAnsweringSaysWhichServerFailed() {
        var h = new Handler(req => req.RequestUri!.ToString() == ApassDownloadService.PackUrl
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : throw new HttpRequestException("Connection timed out (tapvizier.cds.unistra.fr:443)"));

        var s = await Run(h);

        Assert.That(s.State, Is.EqualTo("error"));
        Assert.That(s.Message, Does.Contain("CDS VizieR"));
        Assert.That(File.Exists(_catalog.WritableDbPath), Is.False);
    }

    private sealed class Handler : HttpMessageHandler {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public int TapCalls;
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            if (request.RequestUri!.ToString() == ApassDownloadService.TapUrl) Interlocked.Increment(ref TapCalls);
            return Task.FromResult(_respond(request));
        }
    }

    private sealed class Factory : IHttpClientFactory {
        private readonly HttpMessageHandler _handler;
        public Factory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class Env : IWebHostEnvironment {
        public Env(string root) { WebRootPath = root; ContentRootPath = root; }
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; }
        public string EnvironmentName { get; set; } = "Test";
    }
}
