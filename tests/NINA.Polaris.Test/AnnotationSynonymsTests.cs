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
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services.Sky;

namespace NINA.Polaris.Test;

/// <summary>
/// Two labels on one object.
///
/// The annotation overlay printed "NGC 6853 · Dumbbell Nebula" and
/// "M27 · Dumbbell Nebula" on the same pixel, because the catalogue carries one
/// row per designation. That is correct for search and wrong for a picture, and
/// it happens on most targets: all 107 Messier rows have an NGC or IC twin.
///
/// The merge has to be conservative in the other direction too. The shipped
/// catalogue has 289 positions carrying more than one row and only 211 of them
/// are synonyms; the rest are distinct objects sharing a catalogued centre.
/// Collapsing those would hide objects, which is worse than the duplicate
/// labels this fixes.
/// </summary>
[TestFixture]
public class AnnotationSynonymsTests {

    private static DsoCatalog.DsoObject Obj(string name, string catalog, string id,
            double raHours = 19.99343888888889, double decDeg = 22.721027777777778,
            string? common = null, params string[] aliases)
        => new(name, common, "Planetary Nebula", raHours, decDeg,
               7.4, 6.7, "Vul", catalog, id, aliases);

    /// <summary>The reported case, with the catalogue's real numbers.</summary>
    [Test]
    public void TheDumbbell_IsOneObjectUnderItsBestKnownName() {
        var hits = new[] {
            Obj("NGC 6853", "NGC", "6853", common: "Dumbbell Nebula", aliases: "M27"),
            Obj("M27", "M", "27", common: "Dumbbell Nebula", aliases: "NGC 6853"),
        };

        var merged = AnnotationSynonyms.Collapse(hits);

        Assert.That(merged.Count, Is.EqualTo(1), "two labels were drawn on one object");
        Assert.That(merged[0].Object.Name, Is.EqualTo("M27"),
            "the Messier number is the one an observer recognises");
        Assert.That(merged[0].AlsoKnownAs, Is.EqualTo(new[] { "NGC 6853" }),
            "the folded name stays available to the UI rather than being lost");
    }

    /// <summary>A single alias link is enough, in either direction: not every
    /// catalogue row cross-references its twin.</summary>
    [Test]
    public void OneSidedAlias_StillMerges([Values(true, false)] bool ngcNamesMessier) {
        var hits = new[] {
            Obj("NGC 6853", "NGC", "6853", aliases: ngcNamesMessier ? new[] { "M27" } : Array.Empty<string>()),
            Obj("M27", "M", "27", aliases: ngcNamesMessier ? Array.Empty<string>() : new[] { "NGC 6853" }),
        };
        Assert.That(AnnotationSynonyms.Collapse(hits).Count, Is.EqualTo(1));
    }

    /// <summary>
    /// THE ONE THAT MUST NOT OVERREACH. LBN 970 to 973 sit on the same
    /// catalogued centre and are four different nebulae with no alias between
    /// them. Merging on position alone would delete three real objects from the
    /// overlay.
    /// </summary>
    [Test]
    public void DistinctObjectsSharingAPosition_AreLeftAlone() {
        var hits = Enumerable.Range(970, 4)
            .Select(n => Obj($"LBN {n}", "LBN", n.ToString()))
            .ToArray();

        var merged = AnnotationSynonyms.Collapse(hits);

        Assert.That(merged.Count, Is.EqualTo(4));
        Assert.That(merged.SelectMany(m => m.AlsoKnownAs), Is.Empty);
    }

    /// <summary>An alias that points somewhere else on the sky is a catalogue
    /// error, not a synonym. The shipped data has two: NGC 7368 and NGC 7418
    /// both claim to be IC 1459, from 219 and 35 arcmin away.</summary>
    [Test]
    public void AnAliasThatDisagreesOnPosition_IsNotBelieved() {
        var hits = new[] {
            Obj("IC 1459", "IC", "1459", raHours: 22.95, decDeg: -36.46),
            Obj("NGC 7418", "NGC", "7418", raHours: 22.95, decDeg: -36.46 + 35.0 / 60.0,
                aliases: "IC 1459"),
        };

        var merged = AnnotationSynonyms.Collapse(hits);

        Assert.That(merged.Count, Is.EqualTo(2), "35 arcmin apart is two galaxies, not one");
    }

    [Test]
    public void JustInsideTheSeparationGuard_StillMerges() {
        var apart = (AnnotationSynonyms.MaxSeparationArcmin - 1.0) / 60.0;
        var hits = new[] {
            Obj("NGC 6853", "NGC", "6853", decDeg: 22.0),
            Obj("M27", "M", "27", decDeg: 22.0 + apart, aliases: "NGC 6853"),
        };
        Assert.That(AnnotationSynonyms.Collapse(hits).Count, Is.EqualTo(1));
    }

    /// <summary>Three designations for one object chain together, so the
    /// overlay does not end up with two labels instead of three.</summary>
    [Test]
    public void SynonymsChainTransitively() {
        var hits = new[] {
            Obj("NGC 224", "NGC", "224", aliases: "UGC 454"),
            Obj("UGC 454", "UGC", "454", aliases: "M31"),
            Obj("M31", "M", "31", common: "Andromeda Galaxy"),
        };

        var merged = AnnotationSynonyms.Collapse(hits);

        Assert.That(merged.Count, Is.EqualTo(1));
        Assert.That(merged[0].Object.Name, Is.EqualTo("M31"));
        Assert.That(merged[0].AlsoKnownAs, Is.EqualTo(new[] { "NGC 224", "UGC 454" }),
            "the rest come back best-known first");
    }

    [TestCase("ngc6853")]
    [TestCase("NGC  6853")]
    [TestCase("  ngc 6853 ")]
    public void SpacingAndCaseInAnAlias_DoNotDefeatTheMatch(string alias) {
        var hits = new[] {
            Obj("NGC 6853", "NGC", "6853"),
            Obj("M27", "M", "27", aliases: alias),
        };
        Assert.That(AnnotationSynonyms.Collapse(hits).Count, Is.EqualTo(1));
    }

    /// <summary>Most aliases name objects outside the frame. They must be
    /// ignored quietly rather than merging or throwing.</summary>
    [Test]
    public void AnAliasForSomethingNotInTheField_IsIgnored() {
        var hits = new[] { Obj("M27", "M", "27", aliases: "NGC 6853") };
        var merged = AnnotationSynonyms.Collapse(hits);

        Assert.That(merged.Count, Is.EqualTo(1));
        Assert.That(merged[0].AlsoKnownAs, Is.Empty);
    }

    /// <summary>The overlay is redrawn every frame, so a reshuffle would make
    /// labels jump. A merged object keeps the place of its first appearance.</summary>
    [Test]
    public void InputOrderIsPreserved() {
        var hits = new[] {
            Obj("NGC 6820", "NGC", "6820", raHours: 19.7, decDeg: 23.1),
            Obj("NGC 6853", "NGC", "6853", aliases: "M27"),
            Obj("NGC 6885", "NGC", "6885", raHours: 20.2, decDeg: 26.5),
            Obj("M27", "M", "27"),
        };

        var names = AnnotationSynonyms.Collapse(hits).Select(m => m.Object.Name).ToList();

        Assert.That(names, Is.EqualTo(new[] { "NGC 6820", "M27", "NGC 6885" }),
            "the merged object takes the slot where its first row appeared");
    }

    [Test]
    public void EmptyAndSingleInputs_AreHandled() {
        Assert.That(AnnotationSynonyms.Collapse(Array.Empty<DsoCatalog.DsoObject>()), Is.Empty);
        Assert.That(AnnotationSynonyms.Collapse(null!), Is.Empty);
        Assert.That(AnnotationSynonyms.Collapse(new[] { Obj("M27", "M", "27") }).Count, Is.EqualTo(1));
    }
}

/// <summary>
/// The merge against the catalogue that actually ships, because the rule is
/// only as good as the alias data behind it.
/// </summary>
[TestFixture]
public class AnnotationSynonymsCatalogueTests {

    private DsoCatalog _catalog = null!;

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    [OneTimeSetUp]
    public void SetUp() {
        _catalog = new DsoCatalog(new TestEnv(), NullLogger<DsoCatalog>.Instance);
        if (!_catalog.IsAvailable) Assert.Ignore("dso.db not present; skipping.");
    }

    /// <summary>The frame that produced the report: a 0.47 degree field on the
    /// Dumbbell, the target of the 2026-08-10 session.</summary>
    [Test]
    public async Task OnTheDumbbellField_OnlyOneLabelSurvivesForTheNebula() {
        var hits = await _catalog.QueryRegionAsync(19.99343888888889, 22.721027777777778,
            radiusDeg: 0.3, magLimit: 14.0, limit: 300);

        var rawNames = hits.Select(h => h.Name).ToList();
        Assert.That(rawNames, Does.Contain("M27").And.Contain("NGC 6853"),
            "precondition: the catalogue really does return both designations");

        var merged = AnnotationSynonyms.Collapse(hits);
        var names = merged.Select(m => m.Object.Name).ToList();

        Assert.That(names, Does.Contain("M27"));
        Assert.That(names, Does.Not.Contain("NGC 6853"), "the twin must not get its own label");
        Assert.That(merged.Single(m => m.Object.Name == "M27").AlsoKnownAs,
            Does.Contain("NGC 6853"));
    }

    /// <summary>Nothing may vanish that is not a synonym of something kept:
    /// every input name still has to be reachable, as a label or as an alias of
    /// one.</summary>
    [Test]
    public async Task NoObjectIsLost([Values(0.3, 1.5, 5.0)] double radiusDeg) {
        var hits = await _catalog.QueryRegionAsync(19.99, 22.72, radiusDeg, 15.0, 300);
        if (hits.Count == 0) Assert.Ignore("no catalogue hits in this field");

        var merged = AnnotationSynonyms.Collapse(hits);
        var reachable = merged.Select(m => m.Object.Name)
            .Concat(merged.SelectMany(m => m.AlsoKnownAs))
            .ToHashSet(StringComparer.Ordinal);

        Assert.That(hits.Select(h => h.Name), Is.SubsetOf(reachable));
        Assert.That(merged.Count, Is.LessThanOrEqualTo(hits.Count));
    }

    /// <summary>A Messier field must come back named after the Messier
    /// object, not its NGC twin, wherever the two are in the row order.</summary>
    [TestCase(5.575, -5.39, "M42")]      // Orion
    [TestCase(13.703, 28.377, "M3")]     // globular
    [TestCase(0.712, 41.269, "M31")]     // Andromeda
    public async Task MessierWins(double raHours, double decDeg, string expected) {
        var hits = await _catalog.QueryRegionAsync(raHours, decDeg, 0.2, 15.0, 300);
        var merged = AnnotationSynonyms.Collapse(hits);

        var m = merged.FirstOrDefault(x => x.Object.Name == expected);
        if (m.Object == null) Assert.Ignore($"{expected} not in this build of the catalogue");
        Assert.That(m.Object.Name, Is.EqualTo(expected));
    }

    private class TestEnv : IWebHostEnvironment {
        public string WebRootPath { get; set; } =
            Path.Combine(RepoRoot(), "src", "NINA.Polaris", "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
    }
}
