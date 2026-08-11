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
using NUnit.Framework;
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
