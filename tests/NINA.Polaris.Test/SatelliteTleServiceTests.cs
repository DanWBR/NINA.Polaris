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
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// The satellite TLE set behind the SKY map: CelesTrak text in, the sky
/// engine's jsonl.gz out, with the ISS always present and searchable.
/// </summary>
[TestFixture]
public class SatelliteTleServiceTests {

    private const string Iss1 = "1 25544U 98067A   26261.14280998  .00005718  00000+0  11125-3 0  9991";
    private const string Iss2 = "2 25544  51.6307 200.0361 0004822 152.4527 207.6718 15.49160218586162";
    private const string Poisk1 = "1 36086U 09060A   26261.14280998  .00005718  00000+0  11125-3 0  9999";
    private const string Poisk2 = "2 36086  51.6307 200.0361 0004822 152.4527 207.6718 15.49160218586468";
    private const string Hst1 = "1 20580U 90037B   26261.01803921  .00005838  00000+0  17793-3 0  9998";
    private const string Hst2 = "2 20580  28.4729 165.6473 0001953  95.0051 265.0768 15.31682701802816";

    private static readonly string Stations = string.Join("\r\n", new[] {
        "ISS (ZARYA)             ", Iss1, Iss2, "POISK                   ", Poisk1, Poisk2, ""
    });
    private static readonly string Visual = string.Join("\n", new[] {
        "HST", Hst1, Hst2, "ISS (ZARYA)", Iss1, Iss2, ""
    });

    private string _dir = "";

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "polaris-sat-test-" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown() {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private SatelliteTleService MakeService() {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Sky:SatelliteDir"] = _dir
        }).Build();
        return new SatelliteTleService(new TestEnv(), config, NullLogger<SatelliteTleService>.Instance);
    }

    [Test]
    public void Parse_ReadsThreeLineSets_AndBareTwoLinePairs() {
        var sats = SatelliteTleService.Parse(Stations);
        Assert.That(sats.Select(s => s.Name), Is.EqualTo(new[] { "ISS (ZARYA)", "POISK" }));
        Assert.That(sats[0].Norad, Is.EqualTo(25544));

        var bare = SatelliteTleService.Parse(Hst1 + "\n" + Hst2 + "\n");
        Assert.That(bare, Has.Count.EqualTo(1));
        Assert.That(bare[0].Name, Is.EqualTo("20580"), "a pair without a name line is named by its catalogue number");
    }

    [Test]
    public void EpochOf_DecodesYearAndDayOfYear() {
        // 26261.14280998 = 2026, day 261.1428 = Sep 18 03:25:38 UTC
        var e = SatelliteTleService.EpochOf(Iss1);
        Assert.That(e, Is.EqualTo(new DateTime(2026, 9, 18, 3, 25, 38, DateTimeKind.Utc)).Within(TimeSpan.FromSeconds(1)));
        Assert.That(SatelliteTleService.EpochOf("1 00005U 58002B   98179.78495062  .00000023  00000-0  28098-4 0  4753").Year,
            Is.EqualTo(1998), "two-digit years from 57 on are the 1900s");
    }

    [Test]
    public void Select_KeepsIssFromStations_DropsDockedModules_AndDedupes() {
        var sel = SatelliteTleService.Select(SatelliteTleService.Parse(Visual), SatelliteTleService.Parse(Stations));
        Assert.That(sel.Select(s => s.Norad), Is.EqualTo(new[] { 20580, 25544 }));
        Assert.That(sel.Any(s => s.Name == "POISK"), Is.False, "a module docked to the ISS shares its orbit and would draw on top of it");
    }

    [Test]
    public void ToJsonl_ProducesTheEngineRecord_WithSearchAliases() {
        var svc = MakeService();
        var lines = svc.ToJsonl(SatelliteTleService.Parse(Visual)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines, Has.Length.EqualTo(2));
        using var iss = JsonDocument.Parse(lines[1]);
        var r = iss.RootElement;
        Assert.That(r.GetProperty("model").GetString(), Is.EqualTo("tle_satellite"));
        Assert.That(r.GetProperty("types")[0].GetString(), Is.EqualTo("Asa"));
        var md = r.GetProperty("model_data");
        Assert.That(md.GetProperty("norad_number").GetInt32(), Is.EqualTo(25544));
        Assert.That(md.GetProperty("tle")[0].GetString(), Is.EqualTo(Iss1));
        Assert.That(md.GetProperty("tle")[1].GetString(), Is.EqualTo(Iss2));
        Assert.That(lines[1], Does.Contain("00000+0"), "the drag term's plus sign stays literal for the engine's JSON reader");
        Assert.That(md.GetProperty("mag").GetDouble(), Is.LessThan(1), "the ISS carries a standard magnitude from the bundled table");
        var names = r.GetProperty("names").EnumerateArray().Select(n => n.GetString()).ToList();
        Assert.That(names, Does.Contain("NAME ISS"), "searching 'ISS' must resolve");
        Assert.That(names, Does.Contain("NAME ISS (ZARYA)"));
        Assert.That(names, Does.Contain("NORAD 25544"));
        Assert.That(r.GetProperty("short_name").GetString(), Is.EqualTo("ISS"));
    }

    [Test]
    public void Replace_WritesTheFileAndStatus_AndReloadsOnRestart() {
        var svc = MakeService();
        Assert.That(svc.Source, Is.EqualTo("bundled"));
        Assert.That(svc.Count, Is.GreaterThan(50), "the bundled snapshot ships the visual group");
        Assert.That(svc.CurrentPath, Does.EndWith(SatelliteTleService.FileName));

        var when = new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);
        svc.Replace(SatelliteTleService.Parse(Visual), "celestrak", when);
        Assert.That(svc.Source, Is.EqualTo("celestrak"));
        Assert.That(svc.Count, Is.EqualTo(2));
        Assert.That(svc.FetchedAtUtc, Is.EqualTo(when));
        Assert.That(svc.LatestEpochUtc!.Value.Date, Is.EqualTo(new DateTime(2026, 9, 18)));
        Assert.That(svc.CurrentPath, Is.EqualTo(Path.Combine(_dir, SatelliteTleService.FileName)));

        var (count, latest) = SatelliteTleService.Inspect(svc.CurrentPath);
        Assert.That(count, Is.EqualTo(2));
        Assert.That(latest!.Value.Date, Is.EqualTo(new DateTime(2026, 9, 18)));

        var again = MakeService();
        Assert.That(again.Source, Is.EqualTo("celestrak"));
        Assert.That(again.Count, Is.EqualTo(2));
        Assert.That(again.FetchedAtUtc, Is.EqualTo(when));
    }

    [Test]
    public void Gzip_WritesTheHeaderTheEngineAccepts_AndRoundTrips() {
        var text = "{\"a\":1}\n{\"b\":\"00000+0\"}\n";
        var gz = SatelliteTleService.Gzip(text);
        Assert.That(gz[0], Is.EqualTo(0x1f));
        Assert.That(gz[1], Is.EqualTo(0x8b));
        Assert.That(gz[2], Is.EqualTo(8), "deflate");
        Assert.That(gz[3], Is.EqualTo(8), "FNAME is the only flag the engine's reader takes");
        Assert.That(Encoding.ASCII.GetString(gz, 10, "tle_satellite.jsonl".Length), Is.EqualTo("tle_satellite.jsonl"));
        Assert.That(gz[10 + "tle_satellite.jsonl".Length], Is.EqualTo(0), "name is NUL terminated");
        using var ms = new MemoryStream(gz);
        using var un = new GZipStream(ms, CompressionMode.Decompress);
        using var reader = new StreamReader(un, Encoding.UTF8);
        Assert.That(reader.ReadToEnd(), Is.EqualTo(text), "a standard reader (which checks the CRC) reads it back");
        Assert.That(BitConverter.ToUInt32(gz, gz.Length - 4), Is.EqualTo((uint)Encoding.UTF8.GetByteCount(text)), "ISIZE trailer");
    }

    [Test]
    public void Replace_RefusesASetWithoutTheIss() {
        var svc = MakeService();
        var hstOnly = SatelliteTleService.Parse("HST\n" + Hst1 + "\n" + Hst2 + "\n");
        Assert.Throws<ArgumentException>(() => svc.Replace(hstOnly, "celestrak", DateTime.UtcNow));
        Assert.Throws<ArgumentException>(() => svc.Replace(new List<SatelliteTle>(), "celestrak", DateTime.UtcNow));
        Assert.That(svc.Source, Is.EqualTo("bundled"));
    }

    [Test]
    public void BundledSnapshot_HasTheIss_AndIsReadableGzip() {
        var svc = MakeService();
        using var fs = File.OpenRead(svc.CurrentPath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        var text = reader.ReadToEnd();
        Assert.That(text, Does.Contain("\"NAME ISS\""));
        Assert.That(text, Does.Contain("\"NAME Tiangong\""));
    }

    private class TestEnv : IWebHostEnvironment {
        public string WebRootPath { get; set; } = LocateWwwroot();
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";

        private static string LocateWwwroot() {
            var dir = SourceDir();
            for (var i = 0; i < 8; i++) {
                var candidate = Path.Combine(dir, "src", "NINA.Polaris", "wwwroot");
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir) ?? "";
                if (string.IsNullOrEmpty(dir)) break;
            }
            return "wwwroot";
        }

        private static string SourceDir(
            [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
            => Path.GetDirectoryName(sourceFile) ?? "";
    }
}
