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
using NINA.INDI.Client;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Sky;

namespace NINA.Polaris.Test;

[TestFixture]
public class SkyBrightnessTests {

    [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)]
    [TestCase(6)] [TestCase(7)] [TestCase(8)] [TestCase(9)]
    public void Bortle_RoundTripsThroughSqm(int bortle) {
        Assert.That(SkyBrightness.BortleForSqm(SkyBrightness.SqmForBortle(bortle)), Is.EqualTo(bortle));
    }

    [TestCase(21.5, 4)]
    [TestCase(19.3, 6)]
    [TestCase(18.0, 8)]
    [TestCase(17.0, 9)]
    public void BortleForSqm_UsesTheClassRanges(double sqm, int expected) {
        Assert.That(SkyBrightness.BortleForSqm(sqm), Is.EqualTo(expected));
    }

    [Test]
    public void NoMoon_AtZenith_IsTheSiteSky() {
        Assert.That(SkyBrightness.AtTarget(21.0, 90, -20, 0, 90), Is.EqualTo(21.0).Within(0.01));
    }

    [Test]
    public void NoMoon_LowerTarget_IsBrighter() {
        double zen = SkyBrightness.AtTarget(21.0, 90, -20, 0, 90);
        double low = SkyBrightness.AtTarget(21.0, 30, -20, 0, 90);
        Assert.That(low, Is.LessThan(zen - 0.3));
    }

    [Test]
    public void FullMoon_BrightensADarkSkyByMagnitudes() {
        // A full Moon high up turns a 21.8 site into an ~19 sky, the figure
        // the K&S model is known for.
        double s = SkyBrightness.AtTarget(21.8, 70, 50, 0, 60);
        Assert.That(s, Is.InRange(18.0, 19.8));
    }

    [Test]
    public void Moon_MattersLessThinAndFarAway() {
        double full = SkyBrightness.AtTarget(21.8, 70, 40, 0, 40);
        double crescent = SkyBrightness.AtTarget(21.8, 70, 40, 130, 40);
        double farFull = SkyBrightness.AtTarget(21.8, 70, 40, 0, 120);
        Assert.That(crescent, Is.GreaterThan(full + 1));
        Assert.That(farFull, Is.GreaterThan(full));
    }

    [Test]
    public void SurfaceBrightness_OfM33_IsAboutTwentyThree() {
        // M33: V 5.7, 70.8' x 41.7', quoted mean SB ~23 mag/arcsec².
        Assert.That(SkyBrightness.SurfaceBrightness(5.7, 70.8, 41.7), Is.EqualTo(23.0).Within(0.2));
        Assert.That(SkyBrightness.SurfaceBrightness(null, 10, null), Is.Null);
        Assert.That(SkyBrightness.SurfaceBrightness(8, null, null), Is.Null);
    }

    [Test]
    public void Rate_GalaxyLosesItsRatingAsTheSkyBrightens() {
        var dark = SkyBrightness.Rate(23.0, 21.8, SkyBrightness.Kind.Galaxy, false);
        var city = SkyBrightness.Rate(23.0, 18.1, SkyBrightness.Kind.Galaxy, false);
        var faintCity = SkyBrightness.Rate(24.5, 17.5, SkyBrightness.Kind.Galaxy, false);
        Assert.That(dark.Rating, Is.EqualTo("great"));
        Assert.That(city.Rating, Is.EqualTo("good"));
        Assert.That(faintCity.Rating, Is.EqualTo("hard"));
        Assert.That(city.Penalty, Is.GreaterThan(dark.Penalty));
        Assert.That(faintCity.Penalty, Is.GreaterThan(city.Penalty));
    }

    // Field calibration (Daniel, 2026-11): from Mossoró, Bortle 8 to 9, M31,
    // M33, M42 and the Sculptor Galaxy all come out well, "good", and
    // nothing is "great" under a sky that bright.
    [TestCase(3.4, 190, 0.6, SkyBrightness.Kind.Galaxy, TestName = "Calibration_M31_GoodFromBortle8And9")]
    [TestCase(5.7, 70.8, 41.7 / 70.8, SkyBrightness.Kind.Galaxy, TestName = "Calibration_M33_GoodFromBortle8And9")]
    [TestCase(4.0, 85, 1.0, SkyBrightness.Kind.Emission, TestName = "Calibration_M42_GoodFromBortle8And9")]
    [TestCase(7.94, 26.79, 0.6, SkyBrightness.Kind.Galaxy, TestName = "Calibration_NGC253_GoodFromBortle8And9")]
    [TestCase(7.21, 13.61, 0.6, SkyBrightness.Kind.Galaxy, TestName = "Calibration_M83_GoodFromBortle8And9")]
    [TestCase(7.4, 7.0, 1.0, SkyBrightness.Kind.Planetary, TestName = "Calibration_M27_GoodFromBortle8And9")]
    public void Calibration_BrightTargetsAreGoodFromACity(double mag, double major, double ratio, SkyBrightness.Kind kind) {
        double sb = SkyBrightness.SurfaceBrightness(mag, major, major * ratio)!.Value;
        foreach (var b in new[] { 8, 9 }) {
            var r = SkyBrightness.Rate(sb, SkyBrightness.AtTarget(SkyBrightness.SqmForBortle(b), 60, -90, 180, 90), kind, false);
            Assert.That(r.Rating, Is.EqualTo("good"), $"Bortle {b}");
        }
    }

    // Field calibration (Daniel, 2026-11): from Florânia, Bortle 4, the
    // faint Orion nebulae show in minutes and anything high up is easy;
    // from Mossoró, Bortle 8 to 9, none of them can be done.
    [Test]
    public void Calibration_FaintNebulaEasyAtBortle4_OutOfReachAtBortle8And9() {
        double sb = SkyBrightness.FaintNebulaSb;
        var florania = SkyBrightness.Rate(sb, SkyBrightness.AtTarget(SkyBrightness.SqmForBortle(4), 85, -90, 180, 90),
            SkyBrightness.Kind.Emission, false);
        Assert.That(florania.Rating, Is.EqualTo("great"));
        Assert.That(florania.Penalty, Is.Zero);
        foreach (var b in new[] { 8, 9 }) {
            var city = SkyBrightness.Rate(sb, SkyBrightness.AtTarget(SkyBrightness.SqmForBortle(b), 85, -90, 180, 90),
                SkyBrightness.Kind.Emission, false);
            Assert.That(city.Rating, Is.EqualTo("hard"), $"Bortle {b}");
            Assert.That(city.Penalty, Is.GreaterThanOrEqualTo(20), $"Bortle {b}");
        }
    }

    [Test]
    public void Calibration_LowTargetFromADarkSite_IsNoLongerEasy() {
        // Sky brightening plus the object's own extinction at 25 degrees.
        double alt = 25;
        double sb = SkyBrightness.FaintNebulaSb + SkyBrightness.Extinction * (SkyBrightness.Airmass(alt) - 1);
        var low = SkyBrightness.Rate(sb, SkyBrightness.AtTarget(SkyBrightness.SqmForBortle(4), alt, -90, 180, 90),
            SkyBrightness.Kind.Emission, false);
        Assert.That(low.Rating, Is.EqualTo("good"));
    }

    [Test]
    public void Calibration_CarinaNebula_IsGoodFromBortle9EvenLow() {
        // V 3.0 over 120', peaking at 36 degrees from Mossoró: bright enough
        // to come out well from a Bortle 8 to 9 city.
        double alt = 36;
        double sb = SkyBrightness.SurfaceBrightness(3.0, 120, null)!.Value
                  + SkyBrightness.Extinction * (SkyBrightness.Airmass(alt) - 1);
        foreach (var b in new[] { 8, 9 }) {
            var r = SkyBrightness.Rate(sb, SkyBrightness.AtTarget(SkyBrightness.SqmForBortle(b), alt, -90, 180, 90),
                SkyBrightness.Kind.Emission, narrowband: false, brightNebula: true);
            Assert.That(r.Rating, Is.EqualTo("good"), $"Bortle {b}");
        }
    }

    [Test]
    public void Calibration_FaintNebulaThroughNarrowband_IsGoodFromBortle8And9() {
        // The Veil and the Crescent in Ha from Mossoró.
        foreach (var b in new[] { 8, 9 }) {
            var r = SkyBrightness.Rate(SkyBrightness.FaintNebulaSb,
                SkyBrightness.AtTarget(SkyBrightness.SqmForBortle(b), 55, -90, 180, 90),
                SkyBrightness.Kind.Emission, narrowband: true);
            Assert.That(r.Rating, Is.EqualTo("good"), $"Bortle {b}");
        }
    }

    [Test]
    public void Rate_NarrowbandRescuesEmissionNebulae_NotGalaxies() {
        var broad = SkyBrightness.Rate(24.0, 18.1, SkyBrightness.Kind.Emission, false);
        var nb = SkyBrightness.Rate(24.0, 18.1, SkyBrightness.Kind.Emission, true);
        var galaxy = SkyBrightness.Rate(24.0, 18.1, SkyBrightness.Kind.Galaxy, false);
        var galaxyNb = SkyBrightness.Rate(24.0, 18.1, SkyBrightness.Kind.Galaxy, true);
        Assert.That(nb.Penalty, Is.LessThan(broad.Penalty));
        Assert.That(galaxyNb.Penalty, Is.EqualTo(galaxy.Penalty));
    }

    [Test]
    public void Rate_ObjectBrighterThanTheSky_HasNoPenalty() {
        var r = SkyBrightness.Rate(17.8, 18.1, SkyBrightness.Kind.Planetary, false);
        Assert.That(r.Penalty, Is.Zero);
        // Never "great" under a city sky, however bright the object.
        Assert.That(r.Rating, Is.EqualTo("good"));
        Assert.That(SkyBrightness.Rate(17.8, 21.0, SkyBrightness.Kind.Planetary, false).Rating, Is.EqualTo("great"));
    }

    [TestCase("Emission Nebula", SkyBrightness.Kind.Emission)]
    [TestCase("HII Region", SkyBrightness.Kind.Emission)]
    [TestCase("Supernova Remnant", SkyBrightness.Kind.Emission)]
    [TestCase("Planetary Nebula", SkyBrightness.Kind.Planetary)]
    [TestCase("Reflection Nebula", SkyBrightness.Kind.Reflection)]
    [TestCase("Spiral Galaxy", SkyBrightness.Kind.Galaxy)]
    [TestCase("Globular Cluster", SkyBrightness.Kind.Cluster)]
    [TestCase("Double Star", SkyBrightness.Kind.Other)]
    public void KindOf_MapsCatalogueTypes(string type, SkyBrightness.Kind kind) {
        Assert.That(SkyBrightness.KindOf(type), Is.EqualTo(kind));
    }

    [TestCase("Ha", true)]
    [TestCase("Ha-7nm", true)]
    [TestCase("H-alpha", true)]
    [TestCase("OIII", true)]
    [TestCase("SII 3nm", true)]
    [TestCase("L-eXtreme", true)]
    [TestCase("L-eNhance", true)]
    [TestCase("SV220-HO-7nm", true)]
    [TestCase("ALP-T", true)]
    [TestCase("D1", true)]
    [TestCase("Dual band", true)]
    [TestCase("Altair-TriBand", true)]
    [TestCase("Triband-RGB", false)]
    [TestCase("L", false)]
    [TestCase("Red", false)]
    [TestCase("L-Pro", false)]
    [TestCase("UV/IR Cut", false)]
    [TestCase("CLS", false)]
    [TestCase("Filter 1", false)]
    [TestCase("", false)]
    public void IsNarrowbandFilter_RecognisesLineFilters(string name, bool expected) {
        Assert.That(SkyBrightness.IsNarrowbandFilter(name), Is.EqualTo(expected));
    }

    // ---- through Tonight's Best ----

    private static TonightsBestService MakeService(ProfileService profile) {
        // The shipped dso.db: the fallback list carries no sizes.
        var catalog = new SkyCatalogService(DsoCatalogTests.TestEnv.OpenBundled());
        var altitude = new AltitudeService(profile);
        var equip = new EquipmentManager(new IndiClient("localhost", 7624), NullLogger<EquipmentManager>.Instance,
            new NINA.Polaris.Services.Alpaca.AlpacaDiscoveryCache(),
            new NINA.Polaris.Services.Simulator.Gear.SimGearService());
        return new TonightsBestService(catalog, altitude, equip, profile, NullLogger<TonightsBestService>.Instance);
    }

    private static ProfileService Profile(double? sqm) {
        var p = new ProfileService(new ConfigurationBuilder().Build(), NullLogger<ProfileService>.Instance);
        p.Active.Latitude = -5.18;
        p.Active.Longitude = -37.36;
        p.Active.SkySqm = sqm;
        return p;
    }

    [Test]
    public void Compute_WithoutSiteSky_LeavesScoresAlone() {
        var r = MakeService(Profile(null)).Compute(100, narrowband: false);
        Assert.That(r.Sky, Is.Not.Null);
        Assert.That(r.Sky!.Sqm, Is.Null);
        Assert.That(r.Items.All(i => i.SkyRating == null && i.SkyPenalty == null), Is.True);
    }

    [Test]
    public void Compute_NebulaWithANominalMagnitude_IsCappedAtTheFaintNebulaValue() {
        // IC 434 carries "mag 11" over 90', which would read 29.4 mag/arcsec²
        // and rate the Horsehead hard even from Bortle 4, where it is imaged
        // in a few hours. It must come out no worse than a faint nebula.
        var r = MakeService(Profile(21.1)).Compute(100, narrowband: false);
        foreach (var i in r.Items.Where(i => i.SurfaceBrightness != null
                     && SkyBrightness.KindOf(i.Type) is SkyBrightness.Kind.Emission
                        or SkyBrightness.Kind.Reflection or SkyBrightness.Kind.Dark))
            Assert.That(i.SurfaceBrightness, Is.LessThanOrEqualTo(SkyBrightness.FaintNebulaSb), i.Name);
    }

    [Test]
    public void Compute_CitySky_PenalisesDsosMoreThanADarkSite() {
        var dark = MakeService(Profile(21.9)).Compute(100, narrowband: false);
        var city = MakeService(Profile(18.0)).Compute(100, narrowband: false);
        Assert.That(city.Sky!.Bortle, Is.EqualTo(8));
        Assert.That(dark.Sky!.Bortle, Is.LessThanOrEqualTo(2));

        var cityRated = city.Items.Where(i => i.SkyPenalty != null).ToList();
        Assume.That(cityRated, Is.Not.Empty, "no DSO with a size is up tonight from this site");
        Assert.That(cityRated.Sum(i => i.SkyPenalty!.Value),
            Is.GreaterThan(dark.Items.Where(i => i.SkyPenalty != null).Sum(i => i.SkyPenalty!.Value)));
        Assert.That(cityRated.All(i => i.SkyRating is "good" or "fair" or "hard"), Is.True, "nothing is great under Bortle 8");
        Assert.That(city.Items.Where(i => i.Category is "Planet" or "Moon").All(i => i.SkyPenalty == null), Is.True);
    }
}
