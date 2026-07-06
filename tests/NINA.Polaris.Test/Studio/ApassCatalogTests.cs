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

using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Sky;

namespace NINA.Polaris.Test.Studio;

/// <summary>
/// CCALB-3a: pins the APASS catalog service. Tests build a tiny
/// in-memory SQLite with a handful of synthetic stars + the
/// expected schema (stars + stars_idx R*tree), then exercise the
/// cone search math without needing the real ~80 MB bundled
/// catalog.
/// </summary>
[TestFixture]
public class ApassCatalogTests {

    private string _tmpRoot = null!;
    private string _dbPath = null!;
    private FakeWebHostEnvironment _env = null!;
    private ProfileService _profiles = null!;
    private ApassCatalog _cat = null!;

    // The catalog's writable DB path lives under ProfileService.DataDir
    // (= Profiles:Directory); point it at _tmpRoot so it resolves to the same
    // apass.db the tests seed.
    private ProfileService MakeProfiles() {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Profiles:Directory"] = _tmpRoot
            })
            .Build();
        return new ProfileService(cfg, NullLogger<ProfileService>.Instance);
    }

    [SetUp]
    public void Setup() {
        _tmpRoot = Path.Combine(Path.GetTempPath(),
            "polaris-apass-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tmpRoot, "catalogs", "apass"));
        _dbPath = Path.Combine(_tmpRoot, "catalogs", "apass", "apass.db");
        _env = new FakeWebHostEnvironment(_tmpRoot);
        _profiles = MakeProfiles();
        _cat = new ApassCatalog(_env, _profiles, NullLogger<ApassCatalog>.Instance);
    }

    [TearDown]
    public void Teardown() {
        try { Directory.Delete(_tmpRoot, recursive: true); } catch { }
    }

    [Test]
    public void IsAvailable_NoDb_ReturnsFalse() {
        Assert.That(_cat.IsAvailable, Is.False);
    }

    [Test]
    public void QueryRegionAsync_NoDb_ThrowsActionable() {
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _cat.QueryRegionAsync(0, 0, 1));
        Assert.That(ex!.Message, Does.Contain("APASS").And.Contain("Download"));
    }

    [Test]
    public async Task QueryRegionAsync_WithinCone_ReturnsMatchingStars() {
        // Build a 5-star synthetic catalog: 3 close to center (RA=10,
        // Dec=20), 2 well outside. Cone search at 0.5° from the
        // center should return 3, not 5.
        SeedDb(new[] {
            (Ra: 10.0,  Dec: 20.0,  V: 8.0, B: 8.5),    // exactly center, should match
            (Ra: 10.05, Dec: 20.1,  V: 9.0, B: 9.6),    // ~0.11° away, matches
            (Ra: 10.3,  Dec: 19.9,  V: 10.0, B: 10.7),  // ~0.31° away, matches
            (Ra: 12.0,  Dec: 20.0,  V: 11.0, B: 11.5),  // 2.0° away (RA), no match
            (Ra: 10.0,  Dec: 25.0,  V: 11.5, B: 12.1),  // 5.0° away (Dec), no match
        });
        // Force the star-count cache to invalidate.
        _cat = new ApassCatalog(_env, _profiles, NullLogger<ApassCatalog>.Instance);

        var stars = await _cat.QueryRegionAsync(raDeg: 10.0, decDeg: 20.0,
            radiusDeg: 0.5);

        Assert.That(stars.Count, Is.EqualTo(3),
            $"Expected 3 stars within 0.5° of (10, 20), got {stars.Count}.");
        // All returned stars must actually be within 0.5°. Belt and
        // braces: the bounding-box filter alone would let through the
        // 0.5°-square corners but the angular-distance check should
        // shave those off.
        foreach (var s in stars) {
            double drA = (s.Ra - 10.0) * Math.PI / 180.0;
            double sinDec = Math.Sin(s.Dec * Math.PI / 180.0);
            double cosDec = Math.Cos(s.Dec * Math.PI / 180.0);
            double sin20 = Math.Sin(20.0 * Math.PI / 180.0);
            double cos20 = Math.Cos(20.0 * Math.PI / 180.0);
            double cosTheta = sin20 * sinDec + cos20 * cosDec * Math.Cos(drA);
            double thetaDeg = Math.Acos(Math.Clamp(cosTheta, -1, 1)) * 180.0 / Math.PI;
            Assert.That(thetaDeg, Is.LessThanOrEqualTo(0.5).Within(1e-6));
        }
    }

    [Test]
    public async Task QueryRegionAsync_MagLimit_FiltersDimStars() {
        SeedDb(new[] {
            (10.0, 20.0,  8.0, 8.5),
            (10.05, 20.05, 12.0, 12.5),   // dim, should be filtered
            (10.1, 20.1, 14.0, 14.6),     // also dim
        });
        _cat = new ApassCatalog(_env, _profiles, NullLogger<ApassCatalog>.Instance);

        var bright = await _cat.QueryRegionAsync(10.0, 20.0, 0.5, magLimit: 10.0);
        Assert.That(bright.Count, Is.EqualTo(1),
            "Only the V=8 star should survive the mag-limit filter.");

        var all = await _cat.QueryRegionAsync(10.0, 20.0, 0.5);
        Assert.That(all.Count, Is.EqualTo(3),
            "Without a mag limit, all three stars should be returned.");
    }

    [Test]
    public async Task QueryRegionAsync_NearRaSeam_HandlesWraparound() {
        // RA wrap-around at 0/360. A query centered at RA=0.5° with
        // radius 1° should pick up stars at RA=0.3° (no wrap) AND
        // RA=359.7° (which is 0.7° west of the center across the
        // seam). Without the wrap-around handling, the RA=359.7°
        // star would be skipped.
        SeedDb(new[] {
            (Ra:   0.3, Dec: 20.0, V: 9.0, B: 9.5),
            (Ra: 359.7, Dec: 20.0, V: 9.0, B: 9.5),
            (Ra: 358.0, Dec: 20.0, V: 9.0, B: 9.5),    // too far west, no match
        });
        _cat = new ApassCatalog(_env, _profiles, NullLogger<ApassCatalog>.Instance);

        var stars = await _cat.QueryRegionAsync(raDeg: 0.5, decDeg: 20.0,
            radiusDeg: 1.0);
        Assert.That(stars.Count, Is.EqualTo(2),
            "Two stars should match (0.3° east + 359.7° wrapping).");
    }

    [Test]
    public void StarCount_AfterSeed_ReportsCorrectTotal() {
        SeedDb(new[] {
            (1.0, 1.0, 8.0, 8.5),
            (2.0, 2.0, 9.0, 9.5),
            (3.0, 3.0, 10.0, 10.5),
        });
        _cat = new ApassCatalog(_env, _profiles, NullLogger<ApassCatalog>.Instance);
        Assert.That(_cat.StarCount, Is.EqualTo(3));
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private void SeedDb(IEnumerable<(double Ra, double Dec, double V, double B)> rows) {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using (var cmd = conn.CreateCommand()) {
            cmd.CommandText = @"
                CREATE TABLE stars (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ra REAL NOT NULL, dec REAL NOT NULL,
                    mag_v REAL, mag_b REAL, b_v REAL, source TEXT NOT NULL
                );
                CREATE VIRTUAL TABLE stars_idx USING rtree(
                    id, min_ra, max_ra, min_dec, max_dec
                );";
            cmd.ExecuteNonQuery();
        }
        foreach (var r in rows) {
            using var ins = conn.CreateCommand();
            ins.CommandText = @"
                INSERT INTO stars(ra, dec, mag_v, mag_b, b_v, source)
                VALUES ($ra, $dec, $v, $b, $bv, 'APASS');
                INSERT INTO stars_idx(id, min_ra, max_ra, min_dec, max_dec)
                VALUES (last_insert_rowid(), $ra, $ra, $dec, $dec);";
            ins.Parameters.AddWithValue("$ra", r.Ra);
            ins.Parameters.AddWithValue("$dec", r.Dec);
            ins.Parameters.AddWithValue("$v", r.V);
            ins.Parameters.AddWithValue("$b", r.B);
            ins.Parameters.AddWithValue("$bv", r.B - r.V);
            ins.ExecuteNonQuery();
        }
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment {
        public FakeWebHostEnvironment(string root) {
            ContentRootPath = root;
            WebRootPath = root;
            ContentRootFileProvider = new NullFileProvider();
            WebRootFileProvider = new NullFileProvider();
        }
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "NINA.Polaris.Test";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
    }
}