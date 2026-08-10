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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NUnit.Framework;
using NINA.Polaris.Services.PlateSolving;
using Advisor = NINA.Polaris.Services.PlateSolving.SolverDatabaseAdvisor;

namespace NINA.Polaris.Test;

/// <summary>
/// How a set of index bands becomes a list of files.
///
/// The install said "404 (Not Found)" on the field rig and named nothing. Four
/// separate things were wrong at once, and each of them is pinned below:
/// the 5200 base URL was dead, the tile counts did not match the mirrors, one
/// scale sat in two series' ranges, and the job carried no byte total, so the
/// recommended selection would have quietly pulled 18 GB onto an SBC.
/// </summary>
[TestFixture]
public class AstrometryDownloadPlanTests {

    private static Advisor.AstrometrySeries Tiled(params Advisor.AstrometryBand[] bands)
        => new("5200", "Gaia", "index-52", "https://example.invalid/lite/", null, bands);

    private static Advisor.AstrometrySeries Plain(params Advisor.AstrometryBand[] bands)
        => new("4200", "Tycho", "index-42", "http://example.invalid/4200/", null, bands);

    [Test]
    public void FilesFor_ATiledBand_NamesEveryTile() {
        var s = Tiled(new Advisor.AstrometryBand(5, 48, 100));
        var files = Advisor.FilesFor(s, s.Bands[0]);

        Assert.That(files.Count, Is.EqualTo(48));
        Assert.That(files[0], Is.EqualTo("https://example.invalid/lite/index-5205-00.fits"));
        Assert.That(files[^1], Is.EqualTo("https://example.invalid/lite/index-5205-47.fits"));
    }

    /// <summary>THE 404. Band 7 of the Tycho series is twelve tiles, but it was
    /// described as untiled, so the installer asked for index-4207.fits, which
    /// the mirror has never had.</summary>
    [Test]
    public void FilesFor_TilesAndSingleFiles_AreNamedDifferently() {
        var tiled = Plain(new Advisor.AstrometryBand(7, 12, 100));
        var single = Plain(new Advisor.AstrometryBand(9, 0, 100));

        Assert.That(Advisor.FilesFor(tiled, tiled.Bands[0]).Count, Is.EqualTo(12));
        Assert.That(Advisor.FilesFor(tiled, tiled.Bands[0])[0],
            Does.EndWith("index-4207-00.fits"));
        Assert.That(Advisor.FilesFor(single, single.Bands[0]),
            Is.EqualTo(new[] { "http://example.invalid/4200/index-4209.fits" }));
    }

    [Test]
    public void FilesFor_ABaseUrlWithoutASlash_StillJoins() {
        var s = new Advisor.AstrometrySeries("4200", "Tycho", "index-42",
            "http://example.invalid/4200", null, new[] { new Advisor.AstrometryBand(9, 0, 1) });
        Assert.That(Advisor.FilesFor(s, s.Bands[0])[0],
            Is.EqualTo("http://example.invalid/4200/index-4209.fits"));
    }

    /// <summary>The two series used to declare overlapping scale RANGES, and
    /// the installer looped over both, so a scale in the overlap was queued
    /// from each of them: the same sky, downloaded twice, and counted twice in
    /// the size.</summary>
    [Test]
    public void Plan_AScaleTwoSeriesCouldServe_IsFetchedOnce() {
        var series = new List<Advisor.AstrometrySeries> {
            Tiled(new Advisor.AstrometryBand(6, 2, 10)),
            Plain(new Advisor.AstrometryBand(6, 0, 99), new Advisor.AstrometryBand(7, 0, 5))
        };
        var (urls, bytes) = Advisor.PlanAstrometryDownload(series, new[] { 6, 7 });

        Assert.That(urls.Count, Is.EqualTo(3), "two tiles of band 6 plus one file for band 7");
        Assert.That(urls.Count(u => u.Contains("06")), Is.EqualTo(2));
        Assert.That(bytes, Is.EqualTo(15), "the first series to own the band wins, and only it counts");
    }

    [Test]
    public void Plan_RepeatsAndDisorder_DoNotChangeTheResult() {
        var series = new List<Advisor.AstrometrySeries> {
            Plain(new Advisor.AstrometryBand(8, 0, 3), new Advisor.AstrometryBand(9, 0, 4))
        };
        var a = Advisor.PlanAstrometryDownload(series, new[] { 9, 8, 9 });
        var b = Advisor.PlanAstrometryDownload(series, new[] { 8, 9 });

        Assert.That(a.Urls, Is.EqualTo(b.Urls));
        Assert.That(a.Bytes, Is.EqualTo(7));
    }

    [Test]
    public void Plan_AnUnpublishedScale_IsSkippedRatherThanGuessed() {
        var series = new List<Advisor.AstrometrySeries> { Plain(new Advisor.AstrometryBand(8, 0, 3)) };
        var (urls, bytes) = Advisor.PlanAstrometryDownload(series, new[] { 0, 8 });

        Assert.That(urls.Count, Is.EqualTo(1));
        Assert.That(bytes, Is.EqualTo(3));
    }

    [Test]
    public void DefaultPicks_TakeTheCoarsestUsableBands() {
        var usable = Enumerable.Range(0, 8)
            .Select(i => new Advisor.AstrometryScale(i, i, i + 1)).ToList();

        Assert.That(Advisor.DefaultAstrometryPicks(usable), Is.EqualTo(new[] { 6, 7 }));
        Assert.That(Advisor.DefaultAstrometryPicks(usable, 1), Is.EqualTo(new[] { 7 }));
    }

    [Test]
    public void DefaultPicks_WithNothingUsable_PickNothing() {
        Assert.That(Advisor.DefaultAstrometryPicks(new List<Advisor.AstrometryScale>()), Is.Empty);
    }
}

/// <summary>
/// The shipped catalogue against the mirrors it points at.
///
/// Every number here was measured on 2026-08-10 by listing
/// data.astrometry.net/4200/ and the NERSC index-5200/LITE directory from the
/// field host. The file is data, so nothing but a test stops the next edit from
/// reintroducing a name the mirror does not serve.
/// </summary>
[TestFixture]
public class ShippedSolverCatalogueTests {

    private static string CataloguePath([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..",
            "src", "NINA.Polaris", "wwwroot", "data", "platesolve-databases.json"));

    private static JsonDocument _doc = null!;
    private static List<Advisor.AstrometrySeries> _series = null!;
    private static List<Advisor.AstrometryScale> _scales = null!;

    [OneTimeSetUp]
    public void Load() {
        _doc = JsonDocument.Parse(File.ReadAllText(CataloguePath()));
        var root = _doc.RootElement;
        _series = root.GetProperty("astrometrySeries").EnumerateArray().Select(e =>
            new Advisor.AstrometrySeries(
                e.GetProperty("id").GetString()!,
                e.GetProperty("name").GetString()!,
                e.GetProperty("prefix").GetString()!,
                e.GetProperty("baseUrl").GetString()!,
                null,
                e.GetProperty("bands").EnumerateArray().Select(b => new Advisor.AstrometryBand(
                    b.GetProperty("scale").GetInt32(),
                    b.GetProperty("tiles").GetInt32(),
                    b.GetProperty("bytes").GetInt64())).ToList())).ToList();
        _scales = root.GetProperty("astrometryScales").EnumerateArray().Select(e =>
            new Advisor.AstrometryScale(e.GetProperty("scale").GetInt32(),
                e.GetProperty("minArcmin").GetDouble(),
                e.GetProperty("maxArcmin").GetDouble())).ToList();
    }

    [OneTimeTearDown]
    public void Dispose() => _doc?.Dispose();

    /// <summary>data.astrometry.net stopped carrying the 5200 series and now
    /// answers 404 for the whole directory; it links out to NERSC instead. That
    /// dead URL is what the field install actually hit.</summary>
    [Test]
    public void TheGaiaSeries_DoesNotPointAtTheDeadPath() {
        var gaia = _series.Single(s => s.Id == "5200");
        Assert.That(gaia.BaseUrl, Does.Not.Contain("data.astrometry.net"),
            "data.astrometry.net/5200/ is gone");
        Assert.That(gaia.BaseUrl, Does.Contain("index-5200"));
    }

    [Test]
    public void EveryPublishedScale_IsOwnedByExactlyOneSeries() {
        foreach (var scale in _scales.Select(s => s.Scale)) {
            var owners = _series.Where(s => s.Bands.Any(b => b.Scale == scale)).ToList();
            Assert.That(owners.Count, Is.EqualTo(1),
                $"scale {scale} is served by {owners.Count} series");
        }
    }

    [Test]
    public void EveryBand_HasAMeasuredSize() {
        foreach (var s in _series)
            foreach (var b in s.Bands)
                Assert.That(b.Bytes, Is.GreaterThan(0), $"{s.Id} band {b.Scale} has no size");
    }

    /// <summary>Tile counts as the mirrors list them. Both series split their
    /// finest bands 48 ways; Tycho's band 7 is 12; Tycho 8 and up are one file
    /// each. Getting any of these wrong is a 404 mid-download.</summary>
    [TestCase("5200", 0, 48)]
    [TestCase("5200", 6, 48)]
    [TestCase("4200", 7, 12)]
    [TestCase("4200", 8, 0)]
    [TestCase("4200", 19, 0)]
    public void TileCounts_MatchTheMirrors(string seriesId, int scale, int tiles) {
        var band = _series.Single(s => s.Id == seriesId).Bands.Single(b => b.Scale == scale);
        Assert.That(band.Tiles, Is.EqualTo(tiles));
    }

    /// <summary>The Gaia series stops at 6: there is no index-5207, and asking
    /// for one is the same 404 by another route.</summary>
    [Test]
    public void TheGaiaSeries_StopsAtSix() {
        var gaia = _series.Single(s => s.Id == "5200");
        Assert.That(gaia.Bands.Max(b => b.Scale), Is.EqualTo(6));
        Assert.That(gaia.Bands.Select(b => b.Scale), Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6 }));
    }

    /// <summary>
    /// THE FIELD CASE. ASI585MC at 1366mm: 3840 px at 0.438"/px is a 28 arcmin
    /// field, and astrometry.net's 10%-to-100% rule makes bands 1 to 7 usable.
    /// That is the exact set the card offered and the operator accepted, and it
    /// comes to 19 GB. This pins that the default is now the cheap end of it,
    /// and that the gap between the two is the two orders of magnitude that
    /// made the size warning necessary.
    /// </summary>
    [Test]
    public void TheFieldRig_DefaultsToHundredsOfMegabytes_NotTensOfGigabytes() {
        var fovDeg = 3840 * 0.438 / 3600.0;
        var usable = Advisor.RecommendAstrometryScales(_scales, fovDeg);
        Assert.That(usable.Select(u => u.Scale).ToArray(), Is.EqualTo(new[] { 1, 2, 3, 4, 5, 6, 7 }),
            "band 0 tops out at 2.8 arcmin, just under a tenth of this field");

        var everything = Advisor.BytesFor(_series, usable.Select(u => u.Scale));
        Assert.That(everything, Is.GreaterThan(15_000_000_000L),
            "the whole usable set really is tens of gigabytes");

        var picks = Advisor.DefaultAstrometryPicks(usable);
        Assert.That(picks, Is.EqualTo(new[] { 6, 7 }));
        var byDefault = Advisor.BytesFor(_series, picks);
        Assert.That(byDefault, Is.LessThan(600_000_000L),
            "the default has to be something an observatory uplink can finish");
    }

    /// <summary>A plan built from the shipped catalogue has to produce names
    /// that exist. These are the exact strings the mirrors serve.</summary>
    [Test]
    public void APlanOverTheShippedCatalogue_BuildsRealFilenames() {
        var (urls, bytes) = Advisor.PlanAstrometryDownload(_series, new[] { 6, 7, 9 });

        Assert.That(urls.Count, Is.EqualTo(48 + 12 + 1));
        Assert.That(urls, Has.Some.EndWith("index-5206-47.fits"));
        Assert.That(urls, Has.Some.EndWith("index-4207-11.fits"));
        Assert.That(urls, Has.Some.EndWith("index-4209.fits"));
        Assert.That(urls, Has.None.EndWith("index-4207.fits"), "band 7 is tiled");
        Assert.That(bytes, Is.EqualTo(307722240L + 165438720L + 41178240L));
    }

    /// <summary>The installed-index parser has to recognise what this
    /// catalogue downloads, or the card offers files that are already there.
    /// </summary>
    [Test]
    public void WhatWeDownload_IsWhatTheInstalledScanRecognises() {
        var (urls, _) = Advisor.PlanAstrometryDownload(_series, _scales.Select(s => s.Scale));
        foreach (var url in urls) {
            var name = Path.GetFileNameWithoutExtension(new System.Uri(url).AbsolutePath);
            Assert.That(SolverDatabaseService.ScaleOf(name), Is.GreaterThanOrEqualTo(0),
                $"the scan cannot read {name} back");
        }
    }
}
