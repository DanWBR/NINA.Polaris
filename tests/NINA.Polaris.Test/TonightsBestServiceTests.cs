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

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;
using NINA.INDI.Client;

namespace NINA.Polaris.Test;

/// <summary>
/// Smoke tests for the Tonight's Best ranking service. These exercise
/// the full Compute() path end-to-end against the real catalog and
/// AstronomyEngine (no network calls), so they catch broad regressions
/// like "service can't even start", "catalog can't be enumerated",
/// "AstronomyEngine throws on a particular planet". The deeper
/// astronomical correctness is the responsibility of AstronomyEngine
/// itself, which has its own test suite.
/// </summary>
[TestFixture]
public class TonightsBestServiceTests {

    private TonightsBestService MakeService(double lat, double lng) {
        var emptyConfig = new ConfigurationBuilder().Build();
        var profile = new ProfileService(emptyConfig, NullLogger<ProfileService>.Instance);
        profile.Active.Latitude  = lat;
        profile.Active.Longitude = lng;

        var catalog  = new SkyCatalogService();
        var altitude = new AltitudeService(profile);
        var indi     = new IndiClient("localhost", 7624);
        var equip    = new EquipmentManager(indi, NullLogger<EquipmentManager>.Instance,
            new NINA.Polaris.Services.Alpaca.AlpacaDiscoveryCache(),
            new NINA.Polaris.Services.Simulator.Gear.SimGearService());
        return new TonightsBestService(catalog, altitude, equip, profile,
            NullLogger<TonightsBestService>.Instance);
    }

    [Test]
    public void Compute_AtModerateLatitude_ReturnsRankedList() {
        // -5° lat (northeast Brazil). Plenty of southern sky DSOs are
        // always above 30° somewhere in the night.
        var sut = MakeService(lat: -5.18, lng: -37.36);
        var result = sut.Compute(limit: 30);

        Assert.That(result.Items, Is.Not.Empty, "Should find at least some visible objects");
        // NOTE: no upper-bound assert on Count — `limit` caps the score-ranked
        // core list, but the per-DSO-type top-ups (galaxies/nebulae/clusters
        // guarantees) intentionally exceed it so no category tab comes up
        // near-empty.

        // Scores monotonic decreasing.
        for (var i = 1; i < result.Items.Count; i++) {
            Assert.That(result.Items[i].Score, Is.LessThanOrEqualTo(result.Items[i - 1].Score),
                "Items must be sorted by score descending");
        }

        // The Moon is only a candidate when it peaks above 10° INSIDE
        // tonight's night window — near new moon it tracks the Sun and
        // sits below the horizon all night, so "always present" is
        // astronomically wrong on some dates (this assert used to be
        // unconditional and flaked whenever the test ran near new
        // moon). Mirror the service's gate and only assert when the
        // real sky is clearly on one side of the threshold.
        var moonPeak = MoonPeakAltitude(
            lat: -5.18, lng: -37.36, result.NightStartUtc, result.NightEndUtc);
        var hasMoon = result.Items.Any(i => i.Category == "Moon");
        if (moonPeak >= 12) {
            Assert.That(hasMoon, Is.True,
                $"Moon peaks at {moonPeak:F1}° tonight, should be a candidate");
        } else if (moonPeak < 8) {
            Assert.That(hasMoon, Is.False,
                $"Moon peaks at only {moonPeak:F1}° tonight, should be excluded");
        }
        // 8–12°: too close to the service's 10° gate to assert either
        // way (the two computations sample the window at slightly
        // different wall-clock instants).
    }

    /// <summary>Mirror of TonightsBestService.PeakAltitudeBody for the
    /// Moon: max altitude over the night window, 30-minute steps.</summary>
    private static double MoonPeakAltitude(double lat, double lng,
                                           DateTime from, DateTime to) {
        var observer = new CosineKitty.Observer(lat, lng, 0);
        double peak = -90;
        for (var t = from; t <= to; t = t.AddMinutes(30)) {
            var time = new CosineKitty.AstroTime(t);
            var eq = CosineKitty.Astronomy.Equator(CosineKitty.Body.Moon, time, observer,
                CosineKitty.EquatorEpoch.OfDate, CosineKitty.Aberration.Corrected);
            var horiz = CosineKitty.Astronomy.Horizon(time, observer, eq.ra, eq.dec,
                CosineKitty.Refraction.Normal);
            if (horiz.altitude > peak) peak = horiz.altitude;
        }
        return peak;
    }

    [Test]
    public void Compute_HasNightWindow_NonZeroDuration() {
        var sut = MakeService(lat: 0, lng: 0);
        var result = sut.Compute(limit: 5);
        Assert.That(result.NightEndUtc, Is.GreaterThan(result.NightStartUtc));
    }

    [Test]
    public void Compute_LimitClampedToList() {
        // `limit` caps the score-ranked core BEFORE the per-type top-ups, so
        // the absolute count exceeds it by design. What must hold: a smaller
        // limit can never produce MORE items than a bigger one.
        var sut = MakeService(lat: -5, lng: -37);
        var small = sut.Compute(limit: 3);
        var large = sut.Compute(limit: 500);
        Assert.That(small.Items.Count, Is.LessThanOrEqualTo(large.Items.Count));
    }

    [Test]
    public void Compute_CapsLbnAndLdnCataloguesAtTen() {
        // The Lynds bright/dark nebula catalogues are huge and magnitude-less;
        // Tonight's Best must show at most 10 of each so they don't crowd out
        // everything else. Use a generous limit so the cap (not the global
        // cutoff) is what bounds them.
        var sut = MakeService(lat: -5.18, lng: -37.36);
        var result = sut.Compute(limit: 500);

        static bool IsCatalogue(string? name, string prefix) =>
            name != null
            && name.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)
            && (name.Length == prefix.Length || !char.IsLetter(name[prefix.Length]));

        var lbn = result.Items.Count(i => IsCatalogue(i.Name, "LBN"));
        var ldn = result.Items.Count(i => IsCatalogue(i.Name, "LDN"));
        Assert.That(lbn, Is.LessThanOrEqualTo(10), "At most 10 LBN objects");
        Assert.That(ldn, Is.LessThanOrEqualTo(10), "At most 10 LDN objects");
    }

    [Test]
    public void Compute_IncludesPlanetCategoryWhenVisible() {
        // At least one planet is virtually always above the horizon for
        // some part of any given night anywhere on Earth.
        var sut = MakeService(lat: -5.18, lng: -37.36);
        var result = sut.Compute(limit: 50);
        Assert.That(result.Items.Any(i => i.Category == "Planet"),
            "At least one planet should be visible tonight from a moderate latitude");
    }

    [Test]
    public void Compute_DsoEntriesHaveScoresInPlausibleRange() {
        var sut = MakeService(lat: -5.18, lng: -37.36);
        var result = sut.Compute(limit: 30);
        foreach (var item in result.Items.Where(i => i.Category == "Dso")) {
            // Faint-end DSOs legitimately score negative: the brightness
            // term (6 - mag) * 8 goes below zero past mag 6 (the gate admits
            // up to mag 10 → -32) and the altitude bonus tops out at +20.
            // Ranking only needs relative order, so the floor is ~-40.
            Assert.That(item.Score, Is.GreaterThan(-60).And.LessThan(200),
                $"DSO {item.Name} has implausible score {item.Score}");
            Assert.That(item.PeakAltDeg, Is.GreaterThanOrEqualTo(30),
                $"DSO {item.Name} below the 30° filter threshold");
        }
    }
}