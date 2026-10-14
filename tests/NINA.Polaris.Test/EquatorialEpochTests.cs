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

using System.Collections.Concurrent;
using NUnit.Framework;
using NINA.Core.Utility;
using NINA.INDI.Client;
using NINA.INDI.Devices;
using NINA.INDI.Protocol;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// J2000 ↔ JNow, and the mount boundary that uses it (issue #39): Polaris
/// works in J2000 everywhere above the mount backends, and a mount that points
/// of date (INDI's EQUATORIAL_EOD_COORD) is converted where it is read and
/// where it is commanded.
/// </summary>
[TestFixture]
public class EquatorialEpochTests {
    private static double Sep(double ra1H, double dec1, double ra2H, double dec2) {
        double r = Math.PI / 180;
        double a1 = ra1H * 15 * r, a2 = ra2H * 15 * r, d1 = dec1 * r, d2 = dec2 * r;
        double c = Math.Sin(d1) * Math.Sin(d2) + Math.Cos(d1) * Math.Cos(d2) * Math.Cos(a1 - a2);
        return Math.Acos(Math.Clamp(c, -1, 1)) / r;
    }

    private static DateTime FromJd(double jd) => DateTime.FromOADate(jd - 2415018.5);

    /// <summary>Meeus, "Astronomical Algorithms" 2nd ed., example 21.b:
    /// θ Persei (with its proper motion applied) precessed to 2028 Nov 13.19.</summary>
    [Test]
    public void MatchesMeeusExample21b() {
        var (ra, dec) = EquatorialEpoch.J2000ToJNow(41.054063 / 15.0, 49.227750, FromJd(2462088.69));
        Assert.Multiple(() => {
            Assert.That(ra * 15.0, Is.EqualTo(41.547214).Within(2e-5));
            Assert.That(dec, Is.EqualTo(49.348483).Within(2e-5));
        });
    }

    [Test]
    public void AgreesWithThePolarAlignmentPrecession() {
        var t = new DateTime(2026, 10, 14, 3, 0, 0, DateTimeKind.Utc);
        foreach (var (ra, dec) in new[] { (20.69, 45.28), (5.92, 7.41), (0.05, -60.0), (12.0, 0.0) }) {
            var a = EquatorialEpoch.J2000ToJNow(ra, dec, t);
            var b = PolarAlignmentMath.PrecessJ2000ToDate(ra, dec, t);
            Assert.That(Sep(a.RaHours, a.DecDeg, b.raHours, b.decDeg), Is.LessThan(1e-9));
        }
    }

    [TestCase(20.690532, 45.280339)]   // Deneb
    [TestCase(0.001, 30.0)]            // RA wrap
    [TestCase(23.999, -30.0)]
    [TestCase(2.53, 89.26)]            // Polaris, next to the pole
    [TestCase(6.0, -89.9)]
    public void JNowToJ2000_UndoesJ2000ToJNow(double ra, double dec) {
        var t = new DateTime(2026, 10, 14, 3, 0, 0, DateTimeKind.Utc);
        var now = EquatorialEpoch.J2000ToJNow(ra, dec, t);
        var back = EquatorialEpoch.JNowToJ2000(now.RaHours, now.DecDeg, t);
        Assert.That(Sep(ra, dec, back.RaHours, back.DecDeg), Is.LessThan(1e-9));
        Assert.That(back.RaHours, Is.GreaterThanOrEqualTo(0).And.LessThan(24));
    }

    /// <summary>Precession turns the sky about the ecliptic pole, so a point
    /// on the ecliptic moves the full ~50.3"/yr: × 26.8 yr ≈ 0.37° by late
    /// 2026. Deneb, 60° from the ecliptic, moves half that.</summary>
    [Test]
    public void In2026_APointOnTheEcliptic_MovesAboutAThirdOfADegree() {
        var t = new DateTime(2026, 10, 14, 3, 0, 0, DateTimeKind.Utc);
        var now = EquatorialEpoch.J2000ToJNow(6.0, 23.4393, t);
        Assert.That(Sep(6.0, 23.4393, now.RaHours, now.DecDeg), Is.EqualTo(0.374).Within(0.01));
        var deneb = EquatorialEpoch.J2000ToJNow(20.690532, 45.280339, t);
        Assert.That(Sep(20.690532, 45.280339, deneb.RaHours, deneb.DecDeg), Is.EqualTo(0.19).Within(0.02));
    }

    [Test]
    public void AtJ2000_TheFramesAgree() {
        var j2000 = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var now = EquatorialEpoch.J2000ToJNow(5.5, 22.0, j2000);
        Assert.That(Sep(5.5, 22.0, now.RaHours, now.DecDeg), Is.LessThan(1e-4));
    }

    [Test]
    public void NaN_StaysNaN() {
        var (ra, dec) = EquatorialEpoch.J2000ToJNow(double.NaN, 10, DateTime.UtcNow);
        Assert.That(double.IsNaN(ra) && double.IsNaN(dec), Is.True);
    }

    [TestCase(1, true)]    // topocentric
    [TestCase(0, true)]    // other
    [TestCase(2, false)]   // J2000
    [TestCase(4, true)]    // B1950, treated as JNow
    public void AscomEquatorialSystem(int system, bool isJNow) {
        Assert.That(EquatorialEpoch.AscomSystemIsJNow(system), Is.EqualTo(isJNow));
    }

    [Test]
    public void MountFrameHelpers_LeaveAJ2000MountAlone() {
        Assert.That(EquatorialEpoch.ToMount(3.0, 40.0, mountIsJNow: false), Is.EqualTo((3.0, 40.0)));
        Assert.That(EquatorialEpoch.FromMount(3.0, 40.0, mountIsJNow: false), Is.EqualTo((3.0, 40.0)));
    }

    // ---- The INDI boundary ----

    private static void SetEod(IndiClient client, string device, double ra, double dec) {
        var props = client.Devices.GetOrAdd(device, _ => new ConcurrentDictionary<string, IndiProperty>());
        props["EQUATORIAL_EOD_COORD"] = new IndiNumberProperty {
            Device = device,
            Name = "EQUATORIAL_EOD_COORD",
            Values = new() {
                ["RA"] = new IndiNumberElement { Value = ra },
                ["DEC"] = new IndiNumberElement { Value = dec },
            }
        };
    }

    /// <summary>The reproduction in the issue: Ekos sets the mount's JNow
    /// position in EQUATORIAL_EOD_COORD, and Polaris must report the J2000
    /// position that corresponds to it, which is what SKY draws.</summary>
    [Test]
    public void Indi_AJNowEodPositionReadsBackAsItsJ2000Position() {
        using var client = new IndiClient();
        var now = EquatorialEpoch.J2000ToJNow(20.690532, 45.280339, DateTime.UtcNow);
        SetEod(client, "Telescope Simulator", now.RaHours, now.DecDeg);

        var scope = new IndiTelescope(client, "Telescope Simulator");

        Assert.That(Sep(scope.RightAscension, scope.Declination, 20.690532, 45.280339), Is.LessThan(1e-6));
    }

    /// <summary>0h/0° is what a driver publishes before it has read the
    /// mount; converted it would look like a real position.</summary>
    [Test]
    public void Indi_TheUnreadZeroPositionIsNotConverted() {
        using var client = new IndiClient();
        SetEod(client, "Mount", 0, 0);
        var scope = new IndiTelescope(client, "Mount");
        Assert.That((scope.RightAscension, scope.Declination), Is.EqualTo((0.0, 0.0)));
    }

    [Test]
    public void Indi_NoPositionYetIsNaN() {
        using var client = new IndiClient();
        var scope = new IndiTelescope(client, "Mount");
        Assert.That(double.IsNaN(scope.RightAscension), Is.True);
    }
}
