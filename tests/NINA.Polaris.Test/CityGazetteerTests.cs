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

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The bundled town index behind the Observatory search when there is no
/// internet. Run against the real embedded table, because the table is the
/// product: a build that ships an empty or truncated resource would pass any
/// test written over a fixture.
/// </summary>
[TestFixture]
public class CityGazetteerTests {

    private static readonly CityGazetteer G = new();

    [Test]
    public void TheTableIsBundledAndPopulated() {
        Assert.That(G.Count, Is.GreaterThan(60_000), "cities5000 has about 70,000 rows");
    }

    /// <summary>The case from the field: a tablet with no GPS at a site with no
    /// internet, and the operator knows the nearest town.</summary>
    [Test]
    public void FindsATownWithoutAccents() {
        var hit = G.Search("mossoro").First();
        Assert.Multiple(() => {
            Assert.That(hit.Name, Is.EqualTo("Mossoró"));
            Assert.That(hit.Country, Is.EqualTo("Brazil"));
            Assert.That(hit.Latitude, Is.EqualTo(-5.19).Within(0.02));
            Assert.That(hit.Longitude, Is.EqualTo(-37.34).Within(0.02));
            Assert.That(hit.DisplayName, Is.EqualTo("Mossoró, Rio Grande do Norte, Brazil"));
        });
    }

    [Test]
    public void AccentsInTheQueryAreFineToo() {
        Assert.That(G.Search("Mossoró").First().Name, Is.EqualTo("Mossoró"));
        Assert.That(G.Search("SÃO PAULO").First().Name, Is.EqualTo("São Paulo"));
    }

    /// <summary>A state abbreviation pins a common name down.</summary>
    [Test]
    public void StateInitialsNarrowTheSearch() {
        var natalRn = G.Search("natal rn").First();
        Assert.That(natalRn.Admin1, Is.EqualTo("Rio Grande do Norte"));
        Assert.That(natalRn.Country, Is.EqualTo("Brazil"));
    }

    [Test]
    public void CountryNarrowsTheSearch() {
        Assert.That(G.Search("santiago chile").First().Country, Is.EqualTo("Chile"));
        Assert.That(G.Search("santiago", 20).Select(c => c.Country).Distinct().Count(), Is.GreaterThan(1),
            "without a qualifier there are Santiagos in several countries");
    }

    /// <summary>Ties go to the bigger place: "san jose" is the Californian city
    /// and the Costa Rican capital before any of the dozens of small ones.</summary>
    [Test]
    public void BiggerTownsComeFirst() {
        var top = G.Search("san jose", 3).ToList();
        Assert.That(top.Select(c => c.Population), Is.Ordered.Descending);
        Assert.That(top[0].Population, Is.GreaterThan(500_000));
    }

    [Test]
    public void ExactNameBeatsPrefix() {
        // "Rio" alone: plenty of towns start with Rio; the one CALLED Rio de
        // Janeiro should still lead on population, but a town named exactly
        // "Paris" must beat "Parisburg"-style prefixes regardless of size.
        var paris = G.Search("paris").First();
        Assert.That(paris.Name, Is.EqualTo("Paris"));
        Assert.That(paris.Country, Is.EqualTo("France"));
    }

    [Test]
    public void NoMatchIsAnEmptyList() {
        Assert.That(G.Search("xqzvwk"), Is.Empty);
        Assert.That(G.Search("   "), Is.Empty);
        Assert.That(G.Search(""), Is.Empty);
    }

    [Test]
    public void ASearchIsFastEnoughToRunPerKeystroke() {
        G.Search("a");                         // warm the lazy load
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) G.Search("san");
        sw.Stop();
        Assert.That(sw.ElapsedMilliseconds / 20.0, Is.LessThan(150),
            "one query over 70,000 pre-folded rows");
    }

    /// <summary>The map picker's labels: everything in a window, biggest first,
    /// and a window across the antimeridian still works.</summary>
    [Test]
    public void InBoxReturnsTownsInsideTheWindowBiggestFirst() {
        // Rio Grande do Norte, roughly.
        var hits = G.InBox(south: -6.9, north: -4.8, west: -38.6, east: -34.9, limit: 50);
        Assert.That(hits.Select(c => c.Name), Does.Contain("Natal").And.Contain("Mossoró"));
        Assert.That(hits.Select(c => c.Population), Is.Ordered.Descending);
        Assert.That(hits.All(c => c.Latitude is >= -6.9 and <= -4.8), Is.True);
        Assert.That(hits.All(c => c.Longitude is >= -38.6 and <= -34.9), Is.True);
    }

    [Test]
    public void InBoxAcrossTheAntimeridian() {
        var hits = G.InBox(south: -50, north: -30, west: 170, east: -170, limit: 20);
        Assert.That(hits, Is.Not.Empty, "New Zealand sits on both sides of 180");
        Assert.That(hits.All(c => c.Longitude >= 170 || c.Longitude <= -170), Is.True);
    }

    [Test]
    public void FoldStripsAccentsCaseAndPunctuation() {
        Assert.That(CityGazetteer.Fold("São José dos Campos"), Is.EqualTo("sao jose dos campos"));
        Assert.That(CityGazetteer.Fold("  Saint-Étienne "), Is.EqualTo("saint etienne"));
        Assert.That(CityGazetteer.Fold("Köln"), Is.EqualTo("koln"));
    }

    /// <summary>Offline, the geocoding service answers from the table and does
    /// not report an error for the network it cannot reach.</summary>
    [Test]
    public void GeocodingFallsBackToTheTable() {
        var geo = new GeocodingService(NullLogger<GeocodingService>.Instance, G);
        var local = geo.SearchLocal("mossoro", 5);
        Assert.That(local, Is.Not.Empty);
        Assert.That(local[0].Source, Is.EqualTo("bundled"));
        Assert.That(local[0].DisplayName, Does.StartWith("Mossoró"));
    }

    [Test]
    public void MergeKeepsLocalFirstAndDropsTheSamePlaceFromRemote() {
        var local = new List<GeocodingResult> {
            new() { DisplayName = "Mossoró, RN, Brazil", Latitude = -5.1875, Longitude = -37.3442, Source = "bundled" }
        };
        var remote = new List<GeocodingResult> {
            new() { DisplayName = "Mossoró, Rio Grande do Norte, Brasil", Latitude = -5.1893, Longitude = -37.3415, Source = "nominatim" },
            new() { DisplayName = "Rua Mossoró, Natal", Latitude = -5.79, Longitude = -35.21, Source = "nominatim" },
        };
        var merged = GeocodingService.Merge(local, remote, 5);
        Assert.That(merged.Select(m => m.DisplayName), Is.EqualTo(new[] {
            "Mossoró, RN, Brazil", "Rua Mossoró, Natal" }));
    }
}
