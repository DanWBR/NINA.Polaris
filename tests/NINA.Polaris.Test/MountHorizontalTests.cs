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

using Moq;
using NINA.Core.Enum;
using NINA.Image.Interfaces;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>A Star Adventurer GTi over INDI, at latitude 59.94° N, pointed at
/// the zenith (RA 10h37m31.7s, Dec +60°04′43″, correct) while its driver
/// published Alt −42.32°, Az 181.59°. A star at Dec +60° never gets below +30°
/// there, so no site or clock gives that pair; the mount card, the FITS header
/// and the safety guard all showed it.</summary>
[TestFixture]
public class MountHorizontalTests {
    private const double Lat = 59.94, Lon = 11.0;
    private static readonly DateTime When = new(2026, 10, 17, 8, 28, 31, DateTimeKind.Utc);

    private static Mock<ITelescope> Scope(double driverAlt, double driverAz, PierSide pier) {
        var m = new Mock<ITelescope>();
        m.SetupGet(s => s.RightAscension).Returns(10 + 37 / 60.0 + 31.7 / 3600);
        m.SetupGet(s => s.Declination).Returns(60 + 4 / 60.0 + 43 / 3600.0);
        m.SetupGet(s => s.Altitude).Returns(driverAlt);
        m.SetupGet(s => s.Azimuth).Returns(driverAz);
        m.SetupGet(s => s.SideOfPier).Returns(pier);
        return m;
    }

    [Test]
    public void AnEquatorialMountGetsTheAltitudeItsPointingImplies() {
        var (alt, _) = MountHorizontal.Of(Scope(-42.32, 181.59, PierSide.pierEast).Object, Lat, Lon, When);
        Assert.That(alt, Is.InRange(86.0, 89.5), "the scope points about 2° from the zenith");
    }

    [Test]
    public void AnAltAzMountKeepsItsEncoderAltitude() {
        // Seestar-style: altitude is measured, RA/Dec derived. No pier side.
        var (alt, az) = MountHorizontal.Of(Scope(35.2, 140.0, PierSide.pierUnknown).Object, Lat, Lon, When);
        Assert.That(alt, Is.EqualTo(35.2));
        Assert.That(az, Is.EqualTo(140.0));
    }

    [Test]
    public void ADriverWithoutHorizontalCoordinatesIsComputedAsBefore() {
        var (alt, _) = MountHorizontal.Of(Scope(double.NaN, double.NaN, PierSide.pierUnknown).Object, Lat, Lon, When);
        Assert.That(alt, Is.InRange(86.0, 89.5));
    }

    [Test]
    public void WithoutASiteTheDriverValueStays() {
        // 0°, 0° is the profile default, not where the rig is.
        var (alt, _) = MountHorizontal.Of(Scope(-42.32, 181.59, PierSide.pierWest).Object, 0, 0, When);
        Assert.That(alt, Is.EqualTo(-42.32));
    }

    [TestCase(PierSide.pierEast, true)]
    [TestCase(PierSide.pierWest, true)]
    [TestCase(PierSide.pierUnknown, false)]
    public void PierSideTellsAnEquatorialMount(PierSide pier, bool equatorial) {
        Assert.That(MountHorizontal.IsEquatorial(Scope(0, 0, pier).Object), Is.EqualTo(equatorial));
    }
}
