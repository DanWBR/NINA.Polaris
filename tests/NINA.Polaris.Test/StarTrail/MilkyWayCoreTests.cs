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
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test.StarTrail;

// The Milky Way planner points AltitudeService at the galactic core and reads
// off its altitude track. This checks the core's geometry through the exact
// static transform the planner (and /api/sky/altitude) uses, with no service
// wiring: a target transits at altitude 90 - |lat - dec|.
[TestFixture]
public class MilkyWayCoreTests {
    // Galactic centre ~ RA 17h45m40s / Dec -29°00'28" (same constant the
    // /api/sky/milky-way endpoint uses).
    private const double CoreRaHours = 17.7611;
    private const double CoreDecDeg = -29.0078;

    private static double PeakAltitudeOverADay(double latDeg, double lonDeg) {
        double best = double.MinValue;
        var start = new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc);
        for (int m = 0; m < 24 * 60; m++) {
            var (alt, _) = AltitudeService.RaDecToAltAz(
                CoreRaHours, CoreDecDeg, start.AddMinutes(m), latDeg, lonDeg);
            if (alt > best) best = alt;
        }
        return best;
    }

    [Test]
    public void GalacticCore_RidesHighFromTheSouthernMidLatitudes() {
        // From a southern site (lat -23) the core transits near the zenith:
        // 90 - |-23 - (-29.0078)| = 83.99 deg.
        double peak = PeakAltitudeOverADay(-23.0, -46.0);
        Assert.That(peak, Is.EqualTo(83.99).Within(1.0));
    }

    [Test]
    public void GalacticCore_BarelyClearsTheHorizonFromTheFarNorth() {
        // From lat +60 the core skims the horizon: 90 - |60 - (-29.0078)| ~ 0.99 deg.
        double peak = PeakAltitudeOverADay(60.0, 10.0);
        Assert.That(peak, Is.LessThan(4.0));
        Assert.That(peak, Is.GreaterThan(0.0));
    }
}
