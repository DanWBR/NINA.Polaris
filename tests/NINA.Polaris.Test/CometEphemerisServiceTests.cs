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
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// Sanity tests for the comet Keplerian propagator. We don't need
/// sub-arcsec accuracy (the planning use case forgives a few arcmin), so
/// these tests focus on:
///   - The service starts and loads the curated comets.json
///   - Position output is finite (no NaN from a borked Kepler iteration)
///   - Geocentric distance is in a plausible AU range
///   - Magnitude estimate doesn't blow up when r or Δ are near typical values
/// AstronomyEngine's own test suite covers Earth's heliocentric vector
/// accuracy that we depend on; here we just guard against regressions in
/// our orbit math and JSON loading.
/// </summary>
[TestFixture]
public class CometEphemerisServiceTests {

    private CometEphemerisService MakeService() {
        var env = new TestEnv();
        return new CometEphemerisService(env, NullLogger<CometEphemerisService>.Instance);
    }

    [Test]
    public void LoadComets_FindsCuratedList() {
        var sut = MakeService();
        Assert.That(sut.AllComets, Is.Not.Empty, "comets.json should ship with at least a few entries");
        Assert.That(sut.AllComets.Any(c => c.Name.StartsWith("1P")), "Halley should be in the curated list");
    }

    [Test]
    public void Compute_ProducesFiniteCoords_ForAllCurated() {
        var sut = MakeService();
        var when = new DateTime(2026, 5, 21, 0, 0, 0, DateTimeKind.Utc);
        foreach (var c in sut.AllComets) {
            var p = sut.Compute(c, when);
            Assert.That(double.IsFinite(p.RaHours),  $"{c.Name} RA must be finite");
            Assert.That(double.IsFinite(p.DecDeg),   $"{c.Name} Dec must be finite");
            Assert.That(p.RaHours, Is.InRange(0, 24));
            Assert.That(p.DecDeg,  Is.InRange(-90, 90));
            Assert.That(p.GeoDistanceAu, Is.GreaterThan(0).And.LessThan(50),
                $"{c.Name} geocentric distance implausible: {p.GeoDistanceAu} AU");
            Assert.That(p.HelioDistanceAu, Is.GreaterThan(0).And.LessThan(50),
                $"{c.Name} heliocentric distance implausible: {p.HelioDistanceAu} AU");
        }
    }

    [Test]
    public void Compute_MagnitudeFormula_PlausibleForKnownComet() {
        var sut = MakeService();
        var encke = sut.AllComets.First(c => c.Name == "2P/Encke");
        // Around the 2027 perihelion Encke should be bright (~mag 8).
        var atPerihelion = new DateTime(2027, 2, 10, 12, 0, 0, DateTimeKind.Utc);
        var p = sut.Compute(encke, atPerihelion);
        Assert.That(p.EstimatedMagnitude, Is.LessThan(15),
            "Encke near perihelion should be brighter than mag 15");
    }

    /// <summary>
    /// Minimal IWebHostEnvironment stub pointing at the real wwwroot in
    /// the source tree, which is where comets.json actually lives.
    /// </summary>
    private class TestEnv : IWebHostEnvironment {
        public string WebRootPath { get; set; } = LocateWwwroot();
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";

        private static string LocateWwwroot() {
            // Primary: walk up from THIS SOURCE FILE (tests/NINA.Polaris.Test/…)
            // to the repo root. Walking up from AppContext.BaseDirectory broke
            // whenever the test run used `--artifacts-path` (bin/ redirected to
            // a temp dir outside the repo), which made comets.json "missing"
            // and these tests fail for a purely environmental reason.
            var fromSource = WalkUpFor(SourceDir());
            if (fromSource != null) return fromSource;
            // Fallback: the classic bin-relative walk (works for plain builds).
            return WalkUpFor(AppContext.BaseDirectory) ?? "wwwroot";
        }

        private static string? WalkUpFor(string start) {
            var dir = start;
            for (var i = 0; i < 8; i++) {
                var candidate = Path.Combine(dir, "src", "NINA.Polaris", "wwwroot");
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir) ?? "";
                if (string.IsNullOrEmpty(dir)) break;
            }
            return null;
        }

        private static string SourceDir(
            [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
            => Path.GetDirectoryName(sourceFile) ?? "";
    }
}