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
using NINA.Core.Enum;
using NINA.Mount.SynScanWifi;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The SynScan Wi-Fi driver against an AZ-GTi in EQ mode (firmware 3.40.A5)
/// at latitude -5.18°, longitude -37.36°, 2026-10-09. The replies below are
/// that mount's own; the sky coordinates are what the SynScan App showed for
/// the same pose at 16:32:00 UTC.
/// </summary>
[TestFixture]
public class SynScanMotorProtocolTests {

    private const int Cpr = 2073600;   // :a1 -> =00A41F
    private const double Lat = -5.1789, Lon = -37.3584;

    // ---- Codec ---------------------------------------------------------

    [Test]
    public void TheMountsOwnReplies_Decode() {
        Assert.Multiple(() => {
            Assert.That(SkyWatcherMotorCodec.DecodeUInt("0328A5"), Is.EqualTo(0xA52803), "firmware");
            Assert.That(SkyWatcherMotorCodec.DecodeUInt("00A41F"), Is.EqualTo(Cpr), "counts per rev");
            Assert.That(SkyWatcherMotorCodec.DecodeUInt("0024F4"), Is.EqualTo(16_000_000), "timer");
            Assert.That(SkyWatcherMotorCodec.DecodeUInt("01"), Is.EqualTo(1), "high speed ratio");
        });
    }

    [Test]
    public void Encode_IsTheInverseOfDecode() {
        foreach (var v in new[] { 0, 1, 0x800000, 0xA52803, 664846, 0xFFFFFF })
            Assert.That(SkyWatcherMotorCodec.DecodeUInt(SkyWatcherMotorCodec.EncodeUInt24(v)), Is.EqualTo(v));
        Assert.That(SkyWatcherMotorCodec.EncodeUInt24(0xA52803), Is.EqualTo("0328A5"));
    }

    [Test]
    public void Commands_AreColonLetterAxisDataReturn() {
        Assert.That(SkyWatcherMotorCodec.Command('j', 1), Is.EqualTo(":j1\r"));
        Assert.That(SkyWatcherMotorCodec.Command('G', 2, "01"), Is.EqualTo(":G201\r"));
    }

    [Test]
    public void Replies_SuccessCarriesData_ErrorThrowsWithCode() {
        Assert.That(SkyWatcherMotorCodec.ParseReply("=EC1B78\r"), Is.EqualTo("EC1B78"));
        Assert.That(SkyWatcherMotorCodec.ParseReply("=\r"), Is.EqualTo(""));
        var ex = Assert.Throws<SkyWatcherMotorException>(() => SkyWatcherMotorCodec.ParseReply("!0\r"));
        Assert.That(ex!.Code, Is.EqualTo("0"));
        Assert.Throws<FormatException>(() => SkyWatcherMotorCodec.ParseReply("12:34:56#"));
    }

    [Test]
    public void Status_101_IsStoppedAndInitialised() {
        var s = SkyWatcherMotorCodec.ParseStatus("101");
        Assert.Multiple(() => {
            Assert.That(s.Running, Is.False);
            Assert.That(s.ConstantSpeed, Is.True);
            Assert.That(s.Initialized, Is.True);
        });
        var goTo = SkyWatcherMotorCodec.ParseStatus("011");
        Assert.That(goTo.Running && !goTo.ConstantSpeed, Is.True, "running, not constant speed: a GoTo");
    }

    // ---- Geometry, against the SynScan App -------------------------------

    [Test]
    public void AxisAngles_MatchTheAppsAxisReadout() {
        // :j1 -> =EC1B78, :j2 -> =3A1888; the app showed -89°46'53" / +92°05'56".
        double ra = EqAxisGeometry.StepsToDegrees(SkyWatcherMotorCodec.DecodeUInt("EC1B78"), Cpr);
        double dec = EqAxisGeometry.StepsToDegrees(SkyWatcherMotorCodec.DecodeUInt("3A1888"), Cpr);
        Assert.That(ra, Is.EqualTo(-(89 + 46 / 60.0 + 53 / 3600.0)).Within(1.0 / 3600));
        Assert.That(dec, Is.EqualTo(92 + 5 / 60.0 + 56 / 3600.0).Within(1.0 / 3600));
    }

    [Test]
    public void SouthernPose_MatchesTheAppsHourAngleAndDeclination() {
        var (ha, dec, side) = EqAxisGeometry.AxesToSky(-89.78139, 92.09889, south: true);
        // App: HA 17h59m08s, Dec -87°54'04".
        double appHa = EqAxisGeometry.WrapHours(17 + 59 / 60.0 + 8 / 3600.0);
        Assert.Multiple(() => {
            Assert.That(ha, Is.EqualTo(appHa).Within(1.0 / 3600), "hour angle");
            Assert.That(dec, Is.EqualTo(-(87 + 54 / 60.0 + 4 / 3600.0)).Within(2.0 / 3600), "declination");
            Assert.That(side, Is.EqualTo(PierSide.pierWest),
                "counterweight down with the tube on the east-target side");
        });
    }

    [Test]
    public void SiderealTime_MatchesTheApp() {
        // App: LST 15h15m48s at 16:32:00 UTC.
        double lst = EqAxisGeometry.LocalSiderealHours(new DateTime(2026, 10, 9, 16, 32, 0, DateTimeKind.Utc), Lon);
        Assert.That(lst, Is.EqualTo(15 + 15 / 60.0 + 48 / 3600.0).Within(2.0 / 3600));
    }

    [Test]
    public void Home_IsThePole_CounterweightDown([Values(true, false)] bool south) {
        var (ra, dec) = EqAxisGeometry.Home(south);
        var sky = EqAxisGeometry.AxesToSky(ra, dec, south);
        Assert.That(sky.DecDeg, Is.EqualTo(south ? -90 : 90).Within(1e-9));
        Assert.That(Math.Abs(sky.HaHours), Is.EqualTo(6).Within(1e-9), "counterweight down puts the tube on the 6h circle");
    }

    [Test]
    public void SkyToAxes_RoundTrips_BothSides_BothHemispheres(
            [Values(true, false)] bool south,
            [Values(-11.5, -6, -0.2, 0.2, 3, 11.5)] double ha,
            [Values(-80, -30, 0, 30, 80)] double dec) {
        foreach (var side in new[] { PierSide.pierEast, PierSide.pierWest }) {
            var (ra, de) = EqAxisGeometry.SkyToAxes(ha, dec, south, side);
            var back = EqAxisGeometry.AxesToSky(ra, de, south);
            Assert.That(back.HaHours, Is.EqualTo(ha).Within(1e-9), $"{side} HA");
            Assert.That(back.DecDeg, Is.EqualTo(dec).Within(1e-9), $"{side} Dec");
            Assert.That(back.Side, Is.EqualTo(side));
        }
    }

    /// <summary>On the usual side the counterweight stays below the mount:
    /// the RA axis never leaves the half turn either side of home.</summary>
    [Test]
    public void UsualSide_KeepsTheCounterweightDown(
            [Values(true, false)] bool south,
            [Values(-11.9, -6, -0.1, 0.1, 6, 11.9)] double ha) {
        var side = EqAxisGeometry.SideFor(ha);
        var (ra, _) = EqAxisGeometry.SkyToAxes(ha, 10, south, side);
        double home = EqAxisGeometry.Home(south).RaAxis;
        Assert.That(Math.Abs(ra - home), Is.LessThan(90), $"RA axis {ra:F1}° vs home {home}°");
    }

    /// <summary>Tracking, jog and GoTo directions all follow from one rule:
    /// the direction a small step in the sky moves the axis.</summary>
    [Test]
    public void Directions_AgreeWithTheGeometry(
            [Values(true, false)] bool south,
            [Values(-3.0, 3.0)] double ha) {
        var side = EqAxisGeometry.SideFor(ha);
        var (ra0, de0) = EqAxisGeometry.SkyToAxes(ha, 20, south, side);
        var (raW, _) = EqAxisGeometry.SkyToAxes(ha + 0.01, 20, south, side);   // a little west
        var (_, deN) = EqAxisGeometry.SkyToAxes(ha, 20.1, south, side);        // a little north
        Assert.Multiple(() => {
            Assert.That(raW > ra0, Is.EqualTo(EqAxisGeometry.WestIsForward(south)), "west");
            Assert.That(raW > ra0, Is.EqualTo(EqAxisGeometry.TrackingForward(south)), "tracking follows the sky west");
            Assert.That(deN > de0, Is.EqualTo(EqAxisGeometry.NorthIsForward(south, side)), "north");
        });
    }

    [Test]
    public void AltAz_MatchTheApp() {
        // Same pose: app showed Az 177°53'33", Alt +5°10'02".
        double ha = EqAxisGeometry.WrapHours(17 + 59 / 60.0 + 8 / 3600.0);
        double dec = -(87 + 54 / 60.0 + 4 / 3600.0);
        Assert.That(EqAxisGeometry.Altitude(ha, dec, Lat), Is.EqualTo(5 + 10 / 60.0 + 2 / 3600.0).Within(10.0 / 3600));
        Assert.That(EqAxisGeometry.Azimuth(ha, dec, Lat), Is.EqualTo(177 + 53 / 60.0 + 33 / 3600.0).Within(10.0 / 3600));
    }

    [Test]
    public void Altitude_OfThePoleIsTheLatitude() {
        Assert.That(EqAxisGeometry.Altitude(0, -90, Lat), Is.EqualTo(-Lat).Within(1e-9));
        Assert.That(EqAxisGeometry.Altitude(0, Lat, Lat), Is.EqualTo(90).Within(1e-9), "zenith");
    }
}
