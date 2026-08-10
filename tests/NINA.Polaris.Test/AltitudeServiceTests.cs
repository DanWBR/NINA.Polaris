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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

[TestFixture]
public class AltitudeServiceTests {

    // ---- RaDecToAltAz ----

    [Test]
    public void RaDecToAltAz_PolarisFromMidLatitude_HasAltitudeNearLatitude() {
        // Polaris: RA ≈ 2.53h, Dec ≈ +89.26°. From any northern site,
        // altitude of Polaris ≈ site latitude.
        var lat = 45.0;
        var lon = 0.0;
        var utc = new DateTime(2024, 6, 15, 22, 0, 0, DateTimeKind.Utc);
        var (alt, _) = AltitudeService.RaDecToAltAz(2.53, 89.26, utc, lat, lon);
        Assert.That(alt, Is.EqualTo(lat).Within(1.0));
    }

    [Test]
    public void RaDecToAltAz_OnMeridian_AzimuthIsZeroOrOneEighty() {
        // Construct an instant where LST == RA (target on meridian).
        var utc = new DateTime(2024, 3, 21, 18, 0, 0, DateTimeKind.Utc);
        var lon = 0.0;
        var lst = MeridianFlipService.ComputeLstHours(utc, lon);
        var raHours = lst;
        // For a northern site looking south, on-meridian = azimuth 180°.
        // For a Dec north of latitude, target transits north → azimuth 0°.
        var lat = 40.0;
        var dec = 20.0; // south of zenith for lat=40
        var (_, az) = AltitudeService.RaDecToAltAz(raHours, dec, utc, lat, lon);
        Assert.That(az, Is.EqualTo(180).Within(2));
    }

    [Test]
    public void RaDecToAltAz_TargetBelowHorizon_HasNegativeAltitude() {
        var lat = 45.0;
        var lon = 0.0;
        var utc = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        // Target at RA 0h Dec -60° (deep southern) from northern lat 45 → always below horizon
        var (alt, _) = AltitudeService.RaDecToAltAz(0, -60, utc, lat, lon);
        Assert.That(alt, Is.LessThan(0));
    }

    [Test]
    public void RaDecToAltAz_ReturnsAltInValidRange() {
        // Sanity: every test point's altitude in [-90, 90] and az in [0, 360)
        var rng = new Random(1);
        for (int i = 0; i < 30; i++) {
            var ra = rng.NextDouble() * 24;
            var dec = (rng.NextDouble() - 0.5) * 180;
            var lat = (rng.NextDouble() - 0.5) * 180;
            var lon = (rng.NextDouble() - 0.5) * 360;
            var utc = new DateTime(2024, 1, 1).AddHours(rng.NextDouble() * 24 * 365);
            var (alt, az) = AltitudeService.RaDecToAltAz(ra, dec, utc, lat, lon);
            Assert.That(alt, Is.InRange(-90.0, 90.0));
            Assert.That(az, Is.InRange(0.0, 360.0));
        }
    }

    // ---- Sun position (low precision) ----

    [Test]
    public void SunPosition_AtVernalEquinox_RaNearZero() {
        // Vernal equinox ~ Mar 20 03:00 UTC 2024 → sun RA ≈ 0h, Dec ≈ 0°
        var utc = new DateTime(2024, 3, 20, 3, 6, 0, DateTimeKind.Utc);
        var (ra, dec) = AltitudeService.SunPosition(utc);
        Assert.That(ra, Is.EqualTo(0).Within(0.2).Or.EqualTo(24).Within(0.2));
        Assert.That(dec, Is.EqualTo(0).Within(0.5));
    }

    [Test]
    public void SunPosition_AtJuneSolstice_DecNearPlus23() {
        // June solstice ~ Jun 20 2024 → sun dec ≈ +23.4°
        var utc = new DateTime(2024, 6, 20, 20, 51, 0, DateTimeKind.Utc);
        var (_, dec) = AltitudeService.SunPosition(utc);
        Assert.That(dec, Is.EqualTo(23.4).Within(0.5));
    }

    [Test]
    public void SunPosition_AtDecemberSolstice_DecNearMinus23() {
        var utc = new DateTime(2024, 12, 21, 9, 21, 0, DateTimeKind.Utc);
        var (_, dec) = AltitudeService.SunPosition(utc);
        Assert.That(dec, Is.EqualTo(-23.4).Within(0.5));
    }

    // ---- ComputeNightWindow: which night ----

    private static AltitudeService MakeService(double lat, double lng) {
        var profile = new ProfileService(new ConfigurationBuilder().Build(),
                                         NullLogger<ProfileService>.Instance);
        profile.Active.Latitude = lat;
        profile.Active.Longitude = lng;
        return new AltitudeService(profile);
    }

    /// <summary>
    /// The window must follow the SITE's date, not UTC's.
    ///
    /// The observer is at UTC-3 (lon -36.8). At 23:20 local on 9 August it is
    /// already 02:20 UTC on the 10th, and taking the date off the UTC instant
    /// made the service return the night that BEGINS on the 10th: sunset
    /// eighteen hours in the future, while the observer was outside under the
    /// current one. Every plan built after 21:00 local was charted against the
    /// wrong night (field, Radxa Q6A, 2026-08-09).
    /// </summary>
    [Test]
    public void ComputeNightWindow_LateEveningPastUtcMidnight_ReturnsTheNightInProgress() {
        var svc = MakeService(-6.1269, -36.8183);
        // 23:20 local on the 9th.
        var anchor = new DateTime(2026, 8, 10, 2, 20, 0, DateTimeKind.Utc);

        var w = svc.ComputeNightWindow(anchor);

        Assert.That(w.Sunset, Is.LessThan(anchor),
            $"sunset {w.Sunset:u} must already have happened at {anchor:u}; "
            + "a window starting in the future means the UTC date was used");
        Assert.That(w.Sunrise, Is.GreaterThan(anchor),
            "the observer is mid-night, so sunrise is still ahead");
        Assert.That(w.AstronomicalDuskUtc, Is.LessThan(anchor));
        Assert.That(w.AstronomicalDawnUtc, Is.GreaterThan(anchor));
        // Sunset belongs to the evening of the 9th local, which is the 9th UTC
        // too (20:29 UTC).
        Assert.That(w.Sunset.Date, Is.EqualTo(new DateTime(2026, 8, 9)));
    }

    /// <summary>Same night, asked for before UTC midnight: the answer must not
    /// move. This is the control that shows the fix did not simply shift
    /// everything back by a day.</summary>
    [Test]
    public void ComputeNightWindow_EarlyEvening_AgreesWithTheLateEveningAnswer() {
        var svc = MakeService(-6.1269, -36.8183);
        var early = svc.ComputeNightWindow(new DateTime(2026, 8, 9, 22, 0, 0, DateTimeKind.Utc)); // 19:00 local
        var late  = svc.ComputeNightWindow(new DateTime(2026, 8, 10, 2, 20, 0, DateTimeKind.Utc)); // 23:20 local

        Assert.That(late.Sunset, Is.EqualTo(early.Sunset).Within(TimeSpan.FromMinutes(1)),
            "the same night must be reported either side of UTC midnight");
        Assert.That(late.Sunrise, Is.EqualTo(early.Sunrise).Within(TimeSpan.FromMinutes(1)));
    }

    /// <summary>East of Greenwich the sign flips, so pin that too: at UTC+10,
    /// 22:00 local on the 9th is 12:00 UTC on the 9th, still the same night.</summary>
    [Test]
    public void ComputeNightWindow_EastOfGreenwich_ReturnsTheNightInProgress() {
        var svc = MakeService(-33.87, 151.21);   // Sydney
        var anchor = new DateTime(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc); // ~22:00 local
        var w = svc.ComputeNightWindow(anchor);

        Assert.That(w.Sunset, Is.LessThan(anchor), $"sunset {w.Sunset:u} should be behind us");
        Assert.That(w.Sunrise, Is.GreaterThan(anchor));
    }
}
