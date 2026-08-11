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
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.INDI.Client;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Sky;

namespace NINA.Polaris.Test;

/// <summary>
/// Which name Tonight's Best puts on an object that has several.
///
/// Reported from the field: Andromeda's companion listed as "Arp 168" instead
/// of "M32". Two causes, both pinned here. The rank was parsed out of the
/// display name and only recognised four catalogues, so Arp, UGC, HCG and the
/// rest all tied at the bottom and the winner fell out of input order. And the
/// position match bucketed coordinates into two-arcsecond cells, which puts two
/// rows for one object in different cells whenever they straddle a boundary or
/// disagree by more than the cell: the catalogue's Arp 168 and M32 are three
/// arcseconds apart, so both survived.
/// </summary>
[TestFixture]
public class TonightsBestDedupTests {

    private static TonightCandidate Dso(string name, double raHours, double decDeg,
            string type = "Galaxy", int score = 50)
        => new("Dso", name, null, type, raHours, decDeg, 8.0, null, null, null,
               45, 180, 60, DateTime.UtcNow, score, null, null, null);

    private static List<string> Names(IEnumerable<TonightCandidate> items)
        => items.Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>THE REPORT, with the catalogue's real coordinates. Three
    /// arcseconds apart, which the old two-arcsecond bucket could not see
    /// across.</summary>
    [Test]
    public void ArpAndMessier_ThreeArcsecApart_CollapseToTheMessier() {
        var items = new List<TonightCandidate> {
            Dso("Arp 168", 0.7116111333333334, 40.864444, "Peculiar Galaxy"),
            Dso("M32",     0.7116194444444444, 40.86527777777778, "Galaxy"),
        };

        var kept = TonightsBestService.DedupDsoByPosition(items);

        Assert.That(Names(kept), Is.EqualTo(new[] { "M32" }));
    }

    /// <summary>Caldwell, NGC and Arp all on Centaurus A. The most familiar
    /// name wins regardless of the order they arrive in.</summary>
    [Test]
    public void AmongThreeDesignations_TheMostFamiliarWins() {
        var items = new List<TonightCandidate> {
            Dso("Arp 153",  13.424333333333333, -43.019167, "Peculiar Galaxy"),
            Dso("NGC 5128", 13.424338888888888, -43.01911111111111),
            Dso("C77",      13.424338888888888, -43.01911111111111),
        };

        Assert.That(Names(TonightsBestService.DedupDsoByPosition(items)),
            Is.EqualTo(new[] { "C77" }), "Caldwell beats NGC beats Arp");
        // …and the same three shuffled.
        items.Reverse();
        Assert.That(Names(TonightsBestService.DedupDsoByPosition(items)),
            Is.EqualTo(new[] { "C77" }), "the answer must not depend on input order");
    }

    /// <summary>A high score does not buy a bad name. Score only breaks ties
    /// between equally familiar catalogues.</summary>
    [Test]
    public void ScoreDoesNotOverrideFamiliarity() {
        var items = new List<TonightCandidate> {
            Dso("UGC 454", 0.7123, 41.269, score: 999),
            Dso("M31",     0.7123, 41.269, score: 1),
        };
        Assert.That(Names(TonightsBestService.DedupDsoByPosition(items)),
            Is.EqualTo(new[] { "M31" }));
    }

    /// <summary>Two genuinely different objects a few arcminutes apart are two
    /// entries. M31 and M32 are 24 arcmin apart and must both survive.</summary>
    [Test]
    public void NearbyButDistinctObjects_BothSurvive() {
        var items = new List<TonightCandidate> {
            Dso("M31", 0.7123194444444444, 41.26905555555555),
            Dso("M32", 0.7116194444444444, 40.86527777777778),
        };
        Assert.That(Names(TonightsBestService.DedupDsoByPosition(items)).Count, Is.EqualTo(2));
    }

    /// <summary>The tolerance is a real angular separation, so it has to hold
    /// at high declination where a degree of RA is a small arc, and it must not
    /// swallow anything just outside it.</summary>
    [Test]
    public void TheToleranceIsAngular_NotCoordinateDifference() {
        const double tol = TonightsBestService.DedupToleranceArcsec;
        var justInside = new List<TonightCandidate> {
            Dso("NGC 1", 6.0, 85.0),
            Dso("M1",    6.0, 85.0 + (tol - 5) / 3600.0),
        };
        var justOutside = new List<TonightCandidate> {
            Dso("NGC 1", 6.0, 85.0),
            Dso("M1",    6.0, 85.0 + (tol + 15) / 3600.0),
        };

        Assert.That(TonightsBestService.DedupDsoByPosition(justInside).Count, Is.EqualTo(1));
        Assert.That(TonightsBestService.DedupDsoByPosition(justOutside).Count, Is.EqualTo(2));
    }

    /// <summary>
    /// Position alone decides here, even across types, and that is on purpose:
    /// this list answers "where do I point tonight", and a dark nebula
    /// catalogued at a cluster's centre is the same place to point. The
    /// annotation overlay draws the opposite conclusion from the same data
    /// (see AnnotationSynonymsTests) because hiding an object from a picture of
    /// the sky costs more than a crowded label.
    /// </summary>
    [Test]
    public void ObjectsAtOnePosition_AreOnePlaceToPoint() {
        var items = new List<TonightCandidate> {
            Dso("LDN 1272", 3.79, 24.11, "Dark Nebula"),
            Dso("M45",      3.79, 24.11, "Open Cluster"),
        };

        var kept = TonightsBestService.DedupDsoByPosition(items);

        Assert.That(Names(kept), Is.EqualTo(new[] { "M45" }),
            "one entry, under the name the observer knows");
    }

    /// <summary>Planets, the Moon and comets share the list and have no
    /// catalogue designation. They pass through untouched, including two that
    /// would look co-located.</summary>
    [Test]
    public void NonDsoEntriesAreNeverTouched() {
        var items = new List<TonightCandidate> {
            new("Planet", "Mars",  null, null, 5.0, 20.0, -1, null, null, null,
                40, 120, 50, DateTime.UtcNow, 90, null, null, null),
            new("Moon",   "Moon",  null, null, 5.0, 20.0, -12, null, null, null,
                40, 120, 50, DateTime.UtcNow, 99, null, null, null),
            Dso("M31", 0.7123, 41.269),
        };
        Assert.That(TonightsBestService.DedupDsoByPosition(items).Count, Is.EqualTo(3));
    }

    [Test]
    public void AnEmptyListStaysEmpty() {
        Assert.That(TonightsBestService.DedupDsoByPosition(new List<TonightCandidate>()), Is.Empty);
    }
}

/// <summary>
/// Survey catalogues must not eat the list.
///
/// LBN and Sh2 are nebula surveys with no photometry, so the scorer ranks them
/// by angular size, and a 20-degree molecular cloud beats every Messier on that
/// scale. Counting what clears the pool gate in the shipped catalogue: 916 LBN,
/// 186 Sh2, and not one of those 1102 rows carries a common name. LBN was
/// capped when this first bit; Sh2 was not, and the field host came back with
/// 58 Sh2 entries out of 128 DSOs, with 17 named objects in the whole list.
/// </summary>
[TestFixture]
public class TonightsBestCatalogueCapTests {

    /// <summary>
    /// Built on the REAL dso.db, not the legacy fallback.
    ///
    /// This matters more than it looks. A SkyCatalogService constructed with no
    /// DsoCatalog quietly serves a built-in list of about 200 Messier, Caldwell
    /// and NGC objects, which contains no Sh2 and no LBN at all. A cap test
    /// written against that passes by counting zero, proves nothing, and would
    /// go on passing if the cap were deleted.
    /// </summary>
    private static TonightsBestService MakeService(double lat, double lng) {
        var cfg = new ConfigurationBuilder().Build();
        var profile = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        profile.Active.Latitude = lat;
        profile.Active.Longitude = lng;
        var indi = new IndiClient("localhost", 7624);
        var equip = new EquipmentManager(indi, NullLogger<EquipmentManager>.Instance,
            new NINA.Polaris.Services.Alpaca.AlpacaDiscoveryCache(),
            new NINA.Polaris.Services.Simulator.Gear.SimGearService());
        var dso = new DsoCatalog(new CatalogTestEnv(), NullLogger<DsoCatalog>.Instance);
        if (!dso.IsAvailable) Assert.Ignore("dso.db not present; the survey catalogues cannot be tested.");
        return new TonightsBestService(new SkyCatalogService(dso), new AltitudeService(profile),
            equip, profile, NullLogger<TonightsBestService>.Instance);
    }

    private class CatalogTestEnv : IWebHostEnvironment {
        private static string RepoRoot([CallerFilePath] string thisFile = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
        public string WebRootPath { get; set; } =
            Path.Combine(RepoRoot(), "src", "NINA.Polaris", "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
    }

    private static List<TonightCandidate> Dsos(TonightsBestService svc, int limit = 120)
        => svc.Compute(limit).Items.Where(i => i.Category == "Dso").ToList();

    private static int CountFrom(IEnumerable<TonightCandidate> items, string prefix)
        => items.Count(i => i.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            && (i.Name.Length == prefix.Length
                                || !char.IsLetter(i.Name[prefix.Length])));

    private static TonightCandidate Candidate(string name, int score)
        => new("Dso", name, null, "Nebula", 5.0, 20.0, null, "60'", 60, null,
               45, 180, 60, DateTime.UtcNow, score, null, null, null);

    /// <summary>The cap itself, on a list built for the purpose, so this
    /// answers regardless of what happens to be above the horizon.</summary>
    [TestCase("Sh2")]
    [TestCase("LBN")]
    [TestCase("LDN")]
    public void OnlyTheBestTenOfASurveyCatalogueSurvive(string prefix) {
        var items = Enumerable.Range(1, 40)
            .Select(i => Candidate($"{prefix} {i}", score: i))
            .Concat(new[] { Candidate("M42", 5), Candidate("NGC 7000", 6) })
            .ToList();

        var capped = TonightsBestService.CapCatalogue(items, prefix, 10);

        Assert.That(CountFrom(capped, prefix), Is.EqualTo(10));
        Assert.That(capped.Where(c => c.Name.StartsWith(prefix)).Select(c => c.Score),
            Is.EquivalentTo(Enumerable.Range(31, 10)), "the ten highest scores, not the first ten");
        Assert.That(capped.Select(c => c.Name), Does.Contain("M42").And.Contain("NGC 7000"),
            "a low-scoring Messier must not be dropped by another catalogue's cap");
    }

    /// <summary>Capping "Sh2" must not catch a catalogue that merely starts
    /// with those characters, which is what the non-letter boundary is
    /// for.</summary>
    [Test]
    public void ThePrefixMatchStopsAtALetterBoundary() {
        var items = new List<TonightCandidate> {
            Candidate("Sh2 27", 40), Candidate("Sh2 54", 39),
            Candidate("Sh2b 1", 38), Candidate("Shk 16", 37),
        };

        var capped = TonightsBestService.CapCatalogue(items, "Sh2", 1);

        Assert.That(capped.Select(c => c.Name),
            Is.EquivalentTo(new[] { "Sh2 27", "Sh2b 1", "Shk 16" }));
    }

    /// <summary>And the caps are actually wired into Compute. This can only
    /// FAIL, never pass vacuously: if nothing from a survey is above the
    /// horizon in this window the case says so instead of claiming a pass.
    /// </summary>
    [TestCase("Sh2")]
    [TestCase("LBN")]
    public void ComputeAppliesTheCap(string prefix) {
        var dsos = Dsos(MakeService(-5.18, -37.36));
        var n = CountFrom(dsos, prefix);
        if (n == 0)
            Assert.Ignore($"no {prefix} object is above 30 deg in tonight's window here, "
                          + "so this run does not exercise the cap");
        Assert.That(n, Is.LessThanOrEqualTo(10));
    }

    /// <summary>What the caps are FOR: the list has to be mostly things an
    /// observer would recognise, not survey rows with no name.</summary>
    [Test]
    public void TheListIsMostlyObjectsSomeoneWouldRecognise() {
        var dsos = Dsos(MakeService(-5.18, -37.36));
        if (dsos.Count < 20) Assert.Ignore("too few DSOs visible to judge the mix");

        var surveys = CountFrom(dsos, "Sh2") + CountFrom(dsos, "LBN") + CountFrom(dsos, "LDN");
        Assert.That(surveys, Is.LessThan(dsos.Count / 2),
            $"the nameless surveys hold {surveys} of {dsos.Count} slots");
    }
}

/// <summary>
/// The shared name-preference rule. It used to live twice, once parsed out of a
/// display name in Tonight's Best and once nowhere at all in the annotation
/// overlay, which is why the two lists disagreed about what to call things.
/// </summary>
[TestFixture]
public class DesignationRankTests {

    [TestCase("M", "NGC")]
    [TestCase("M", "Arp")]
    [TestCase("C", "NGC")]
    [TestCase("NGC", "IC")]
    [TestCase("IC", "Arp")]
    [TestCase("NGC", "UGC")]
    [TestCase("Arp", "PGC")]
    public void MoreFamiliarCataloguesRankLower(string better, string worse) {
        Assert.That(DesignationRank.Of(better), Is.LessThan(DesignationRank.Of(worse)));
    }

    [TestCase("M31", 0)]
    [TestCase("M 31", 0)]
    [TestCase("C77", 1)]
    [TestCase("NGC 6853", 2)]
    [TestCase("IC 434", 3)]
    [TestCase("Sh2 27", 4)]
    [TestCase("LBN 970", 4)]
    [TestCase("Arp 168", 8)]
    [TestCase("UGC 454", 9)]
    public void ANameRanksAsItsCatalogue(string name, int expected) {
        Assert.That(DesignationRank.OfName(name), Is.EqualTo(expected));
    }

    /// <summary>The trap the old parser was written around: a name starting
    /// with M or C that is not Messier or Caldwell. "Mel 25" is Melotte and
    /// "Cr 399" is Collinder, and calling either of them Messier would put the
    /// wrong name on the object.</summary>
    [TestCase("Mel 25")]
    [TestCase("Cr 399")]
    [TestCase("MCG 1-2-3")]
    public void CatalogueNamesThatMerelyStartWithMOrC_AreNotMessier(string name) {
        Assert.That(DesignationRank.OfName(name),
            Is.GreaterThan(DesignationRank.Of("IC")));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    [TestCase("42")]
    public void RubbishRanksLast(string? name) {
        Assert.That(DesignationRank.OfName(name), Is.EqualTo(99));
    }
}
