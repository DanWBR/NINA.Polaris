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

using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services.Sky;

namespace NINA.Polaris.Test;

/// <summary>
/// CAT-2 tests for <see cref="DsoCatalog"/>. They run against the
/// real bundled <c>wwwroot/catalogs/dso/dso.db</c> built once by
/// <c>scripts/build-dso-catalog.py</c> + committed to the repo, so
/// the assertions pin the data shape (presence of NGC 7331, Arp 273,
/// HCG 92, IC 5146, etc) in addition to the query mechanics.
/// </summary>
[TestFixture]
public class DsoCatalogTests {

    private DsoCatalog _catalog = null!;

    [OneTimeSetUp]
    public void SetUp() {
        _catalog = new DsoCatalog(new TestEnv(), NullLogger<DsoCatalog>.Instance);
        if (!_catalog.IsAvailable) {
            Assert.Ignore(
                $"dso.db not present at {_catalog.DbPath}; run " +
                "`python scripts/build-dso-catalog.py` to populate it. " +
                "Skipping DsoCatalog assertions.");
        }
    }

    /// <summary>
    /// Every Messier number has to resolve, because a catalogue that is
    /// missing one is missing it everywhere: the SKY search, the target the
    /// file namer derives from where the mount points, Tonight's Best and
    /// the broadcast object card all read this one table.
    ///
    /// <para>This exists because M45 was absent from the shipped database
    /// for months and nothing noticed. OpenNGC keeps the objects with no NGC
    /// or IC number in a separate addendum file, the build only read the
    /// main one, and the Pleiades has no NGC number.</para>
    ///
    /// <para>M102 is the single allowed exception: it is disputed, OpenNGC
    /// records it as a duplicate of M101, and the usual alternative
    /// identification (NGC 5866) is contested enough that inventing a row
    /// would be taking a side.</para>
    /// </summary>
    [Test]
    public async Task EveryMessierNumberResolves() {
        var missing = new List<string>();
        for (var i = 1; i <= 110; i++) {
            var name = "M" + i;
            if (name == "M102") continue;
            if (await _catalog.GetByNameAsync(name) == null) missing.Add(name);
        }
        Assert.That(missing, Is.Empty, "Messier numbers absent from the catalogue");
    }

    /// <summary>
    /// The famous objects that carry no NGC or IC number, which is the class
    /// the addendum exists for and the class that went missing with it.
    /// </summary>
    [TestCase("M45", "Pleiades")]
    [TestCase("Mel 22", "Pleiades")]
    [TestCase("C41", "Hyades")]
    [TestCase("C99", "Coalsack")]
    [TestCase("Cl 399", "Coathanger")]
    [TestCase("B 33", "Horsehead")]
    [TestCase("ESO 056-115", "Large Magellanic Cloud")]
    public async Task ObjectsWithNoNgcNumberAreInTheCatalogue(string designation, string expectedName) {
        var hit = await _catalog.GetByNameAsync(designation);
        Assert.That(hit, Is.Not.Null, designation);
        Assert.That(hit!.CommonName ?? "", Does.Contain(expectedName).IgnoreCase);
    }

    /// <summary>
    /// Curated common names survive a rebuild.
    ///
    /// <para>Thirteen of these were once written straight into dso.db with no
    /// change to the generator, and the next rebuild silently dropped every
    /// one. They now live in EXTRA_COMMON_NAMES in the build script, and this
    /// is what says so if anyone puts them back in the database by hand.</para>
    /// </summary>
    [TestCase("Deer Lick", "NGC 7331")]
    [TestCase("Hamburger Galaxy", "NGC 3628")]
    [TestCase("Stephan", "HCG 92")]
    [TestCase("Seven Sisters", "M45")]
    public async Task CuratedCommonNamesAreSearchable(string query, string expectedDesignation) {
        var hits = await _catalog.SearchAsync(query, 8);
        Assert.That(hits.Select(h => h.Name), Does.Contain(expectedDesignation), query);
    }

    /// <summary>
    /// One row per designation. The addendum overlaps the Hickson catalogue
    /// Vizier supplies, and taking both put two HCG 92 rows in the search.
    /// </summary>
    [TestCase("HCG 92")]
    [TestCase("HCG 79")]
    [TestCase("M45")]
    [TestCase("C41")]
    public async Task ADesignationNamesExactlyOneRow(string designation) {
        var hits = await _catalog.SearchAsync(designation, 10);
        Assert.That(hits.Count(h => h.Name == designation), Is.EqualTo(1), designation);
    }

    [Test]
    public void IsAvailable_WhenDbPresent_IsTrue() {
        Assert.That(_catalog.IsAvailable, Is.True);
    }

    [Test]
    public void ObjectCount_IsAtLeastTenThousand() {
        Assert.That(_catalog.ObjectCount, Is.GreaterThan(10_000),
            "Bundled DB should hold OpenNGC + Vizier + Caldwell rows " +
            "(currently ~14.5k).");
    }

    [Test]
    public async Task GetByNameAsync_KnownObjects_AllResolve() {
        foreach (var name in new[] { "NGC 7331", "IC 5146", "M31", "C14",
                                      "Arp 273", "Sh2 279", "HCG 92" }) {
            var obj = await _catalog.GetByNameAsync(name);
            Assert.That(obj, Is.Not.Null, $"{name} should be in the DB");
            // M / C entries resolve through aliases to their canonical
            // NGC/IC primary name (e.g. M31 -> NGC 224), so accept a
            // match against either the primary Name or the alias list.
            var matches = string.Equals(obj!.Name, name,
                    System.StringComparison.OrdinalIgnoreCase)
                || (obj.Aliases != null && obj.Aliases.Any(a =>
                    string.Equals(a, name, System.StringComparison.OrdinalIgnoreCase)));
            Assert.That(matches, Is.True,
                $"Resolved row '{obj.Name}' should match '{name}' on Name or Aliases");
            Assert.That(obj.RaHours, Is.InRange(0, 24));
            Assert.That(obj.DecDeg, Is.InRange(-90, 90));
            Assert.That(obj.Type, Is.Not.Empty);
        }
    }

    [Test]
    public async Task GetByNameAsync_Miss_ReturnsNull() {
        var obj = await _catalog.GetByNameAsync("NGC 99999");
        Assert.That(obj, Is.Null);
    }

    [Test]
    public async Task SearchAsync_PrefixMatch_ReturnsResults() {
        var results = await _catalog.SearchAsync("NGC 733", limit: 20);
        Assert.That(results, Is.Not.Empty,
            "NGC 7331 / 7339 / etc all start with NGC 733");
        // NGC 7331 is the brightest hit (mag 9.4), should rank first.
        Assert.That(results[0].Name, Does.StartWith("NGC 733"));
    }

    [Test]
    public async Task FilterAsync_ByCatalog_ReturnsThatCatalogOnly() {
        var results = await _catalog.FilterAsync(
            new DsoCatalog.DsoFilter(Catalog: "Arp", Limit: 500));
        Assert.That(results, Is.Not.Empty);
        Assert.That(results.All(r => r.Catalog == "Arp"), Is.True,
            "Catalog filter must be respected");
    }

    [Test]
    public async Task FilterAsync_ByTypeAndMagnitude_NarrowsResults() {
        var bright = await _catalog.FilterAsync(
            new DsoCatalog.DsoFilter(Type: "Galaxy", MaxMagnitude: 9.0,
                                     Limit: 50));
        Assert.That(bright, Is.Not.Empty);
        Assert.That(bright.All(r => r.Type == "Galaxy"
                                    && (r.Magnitude ?? double.MaxValue) <= 9.0),
            Is.True);
    }

    [Test]
    public async Task GetCatalogsAsync_IncludesAllSources() {
        var cats = await _catalog.GetCatalogsAsync();
        // Subset (full set may grow); at minimum what build-dso-catalog.py produces.
        foreach (var expected in new[] { "NGC", "IC", "M", "C", "Arp",
                                          "Sh2", "HCG", "AGC" }) {
            Assert.That(cats, Contains.Item(expected),
                $"Catalog '{expected}' should be present");
        }
    }

    [Test]
    public async Task GetTypesAsync_IncludesCommonDsoTypes() {
        var types = await _catalog.GetTypesAsync();
        Assert.That(types, Is.Not.Empty);
        // Spot-check a few that any non-trivial catalog will surface.
        Assert.That(types.Any(t => t.Contains("Galaxy",
            StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(types.Any(t => t.Contains("Nebula",
            StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public async Task QueryRegionAsync_NearNGC7331_FindsHCG92() {
        // NGC 7331 ≈ RA 22h 37m, Dec +34.4°. HCG 92 (Stephan's Quintet)
        // sits ~30 arcmin to the south of it. A 2° cone search should
        // pick up both NGC 7331 itself + the HCG group nearby.
        var hits = await _catalog.QueryRegionAsync(
            raHours: 22.617, decDeg: 34.4, radiusDeg: 2.0,
            magLimit: 14.0, limit: 100);
        Assert.That(hits, Is.Not.Empty);
        var names = hits.Select(h => h.Name).ToList();
        Assert.That(names.Any(n => n == "NGC 7331"), Is.True,
            "NGC 7331 should be inside its own 2° cone");
        Assert.That(names.Any(n => n == "HCG 92"), Is.True,
            "Stephan's Quintet (HCG 92) is ~30' away from NGC 7331");
    }

    [Test]
    public async Task LoadAllAsync_WithMagCap_FiltersDimRows() {
        var bright = await _catalog.LoadAllAsync(magCap: 8.0);
        // Should be enough Messier + a handful of NGC/IC, but not the
        // dim Abell clusters or faint NGC galaxies.
        Assert.That(bright.Count, Is.GreaterThan(50));
        // The hand-curated 'Notable' rows ride along whatever the cap.
        Assert.That(bright.Where(o => o.Catalog != "Notable")
                          .All(o => (o.Magnitude ?? double.MaxValue) <= 8.0),
            Is.True);
    }

    /// <summary>Mirrors the pattern from CometEphemerisServiceTests:
    /// resolve the real wwwroot under src/NINA.Polaris/ so the bundled
    /// dso.db is found at test time.</summary>
    internal class TestEnv : IWebHostEnvironment {
        public string WebRootPath { get; set; } = LocateWwwroot();
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";

        private static string LocateWwwroot() {
            // The source tree, from this file's own path: the build output can
            // live anywhere (a scratch directory outside the repo), and walking
            // up from it then finds nothing and every test here is skipped.
            var fromSource = Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", "src", "NINA.Polaris", "wwwroot");
            if (Directory.Exists(fromSource)) return Path.GetFullPath(fromSource);
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 8; i++) {
                var candidate = Path.Combine(dir, "src", "NINA.Polaris", "wwwroot");
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir) ?? "";
                if (string.IsNullOrEmpty(dir)) break;
            }
            return "wwwroot";
        }

        public static NINA.Polaris.Services.Sky.DsoCatalog OpenBundled()
            => new(new TestEnv(), NullLogger<NINA.Polaris.Services.Sky.DsoCatalog>.Instance);

        private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
    }

    // ----- Stars (scripts/build-star-catalog.py) -----
    //
    // Asked for from the field: "V1764 Cygni / WR 134" found nothing, because
    // the search knew deep-sky objects and little else. The Bright Star
    // Catalogue, the IAU proper names and the galactic Wolf-Rayet list now
    // sit in the same table, findable by every designation people use.

    [TestCase("WR 134", "WR 134")]
    [TestCase("WR134", "WR 134")]
    [TestCase("V1769 Cyg", "WR 134")]
    [TestCase("V1769 Cygni", "WR 134")]
    [TestCase("HD 191765", "WR 134")]
    [TestCase("Betelgeuse", "Betelgeuse")]
    [TestCase("alpha Ori", "Betelgeuse")]
    [TestCase("alf Ori", "Betelgeuse")]
    [TestCase("58 Ori", "Betelgeuse")]
    [TestCase("HR 2061", "Betelgeuse")]
    [TestCase("La Superba", "La Superba")]
    [TestCase("Y CVn", "La Superba")]
    [TestCase("Hind's Crimson Star", "Hind's Crimson Star")]
    [TestCase("R Lep", "Hind's Crimson Star")]
    [TestCase("Proxima", "Proxima Centauri")]
    public async Task SearchAsync_Stars_FindTheStarFirst(string query, string expectedName) {
        var hits = await _catalog.SearchAsync(query, 5);
        Assert.That(hits, Is.Not.Empty, query);
        Assert.That(hits[0].Name, Is.EqualTo(expectedName), query);
    }

    // Famous objects with no NGC / IC / Messier number: "Phoenix A" was
    // nowhere, on the map or in the search. scripts/build-named-objects.py
    // carries a hand-curated list under catalog 'Notable'.
    [TestCase("Phoenix A", "Phoenix A")]
    [TestCase("Phoenix Cluster", "Phoenix A")]
    [TestCase("SPT-CL J2344-4243", "Phoenix A")]
    [TestCase("TON 618", "TON 618")]
    [TestCase("ton618", "TON 618")]
    [TestCase("3C 273", "3C 273")]
    [TestCase("Hoag's Object", "Hoag's Object")]
    [TestCase("Einstein Cross", "Einstein Cross")]
    [TestCase("Cygnus X-1", "Cygnus X-1")]
    [TestCase("Sgr A*", "Sagittarius A*")]
    [TestCase("Markarian's Chain", "Markarian's Chain")]
    [TestCase("Cas A", "Cassiopeia A")]
    [TestCase("Tabby's Star", "Tabby's Star")]
    [TestCase("Vela Pulsar", "Vela Pulsar")]
    [TestCase("PSR B1919+21", "PSR B1919+21")]
    [TestCase("Cygnus A", "Cygnus A")]
    [TestCase("BL Lac", "BL Lacertae")]
    [TestCase("Coma Cluster", "Coma Cluster")]
    [TestCase("Leo Triplet", "Leo Triplet")]
    [TestCase("LMC", "Large Magellanic Cloud")]
    [TestCase("Wolf 359", "Wolf 359")]
    [TestCase("TRAPPIST-1", "TRAPPIST-1")]
    [TestCase("Hubble Ultra Deep Field", "Hubble Ultra Deep Field")]
    public async Task SearchAsync_NamedObjects_AreFound(string query, string expectedName) {
        var hits = await _catalog.SearchAsync(query, 5);
        Assert.That(hits, Is.Not.Empty, query);
        Assert.That(hits[0].Name, Is.EqualTo(expectedName), query);
    }

    /// <summary>Unlike the star tables, the named objects are deep-sky targets
    /// and belong on the map and in the planning pools.</summary>
    [Test]
    public async Task LoadAllAsync_IncludesTheNamedObjects() {
        var all = await _catalog.LoadAllAsync();
        Assert.That(all.Any(o => o.Name == "Phoenix A"), Is.True);
    }

    /// <summary>"P Cyg" is also a substring of "Alp Cyg" (Deneb), and Deneb is
    /// brighter. The exact alias token has to win over the substring hit.</summary>
    [Test]
    public async Task SearchAsync_ExactAliasToken_BeatsABrighterSubstringHit() {
        var hits = await _catalog.SearchAsync("P Cyg", 5);
        Assert.That(hits.Select(h => h.Name).First(), Is.EqualTo("P Cygni"));
    }

    [Test]
    public async Task SearchAsync_WolfRayet_HaveTheirOwnType() {
        var hits = await _catalog.SearchAsync("WR 6", 3);
        Assert.That(hits[0].Type, Is.EqualTo("Wolf-Rayet Star"));
        Assert.That(hits[0].CommonName, Does.Contain("EZ CMa"));
    }

    /// <summary>The star tables are for searching, not for the bulk pools:
    /// Tonight's Best iterates AllPlanningObjects and would rank nine
    /// thousand naked-eye stars above every galaxy.</summary>
    [Test]
    public async Task LoadAllAsync_LeavesTheStarTablesOut() {
        var all = await _catalog.LoadAllAsync(magCap: 14.0, minSizeNoMag: 10.0);
        Assert.That(all.Any(o => o.Catalog is "HR" or "Star" or "WR"), Is.False);
        Assert.That(all.Count, Is.GreaterThan(5_000), "the deep-sky pool itself is intact");
    }

    [Test]
    public async Task GetTypesAsync_IncludesTheStarTypes() {
        var types = await _catalog.GetTypesAsync();
        Assert.That(types, Does.Contain("Wolf-Rayet Star").And.Contain("Carbon Star").And.Contain("Variable Star"));
    }
}
