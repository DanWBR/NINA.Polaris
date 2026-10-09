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

using NINA.Core.Enum;

namespace NINA.Mount.SynScanWifi;

/// <summary>
/// Axis angles of an equatorial SynScan mount to and from hour angle,
/// declination and pier side.
///
/// <para>Axis angles are what the SynScan App shows as "Axis":
/// <c>(position - 0x800000) / CPR * 360</c>. Home, counterweight down and
/// pointing at the pole, is RA axis +90° / Dec axis +90° in the north and
/// -90° / +90° in the south, so a mount the app initialised reads the same
/// here. Checked against the app on an AZ-GTi in EQ mode at latitude -5°:
/// axes -89°46'53" / +92°05'56" with the app showing HA 17h59m08s and Dec
/// -87°54'04". The northern case is the same mount mirrored through the
/// equator (both signs flip) and has not been checked on a mount.</para>
///
/// <para>The arithmetic runs in a "north frame" and negates hour angle and
/// declination for the south. A Dec axis within 90° of home is the
/// counterweight-down side for targets west of the meridian (pier east);
/// beyond it the tube is past the pole and the hour angle is 12h away
/// (pier west).</para>
/// </summary>
public static class EqAxisGeometry {

    public static double StepsToDegrees(int position, int countsPerRev)
        => (position - SkyWatcherMotorCodec.PositionOffset) * 360.0 / countsPerRev;

    public static int DegreesToSteps(double degrees, int countsPerRev)
        => SkyWatcherMotorCodec.PositionOffset + (int)Math.Round(degrees / 360.0 * countsPerRev);

    public static (double RaAxis, double DecAxis) Home(bool south) => (south ? -90.0 : 90.0, 90.0);

    /// <summary>Hour angle (hours, -12..12), declination (degrees) and pier
    /// side for a pair of axis angles.</summary>
    public static (double HaHours, double DecDeg, PierSide Side) AxesToSky(
            double raAxis, double decAxis, bool south) {
        double d = Wrap180(decAxis);
        bool pastPole = d > 90 || d < -90;
        double dec = pastPole ? (d > 0 ? 180 - d : -180 - d) : d;
        double ha = raAxis / 15.0 + (pastPole ? 12 : 0);
        if (south) { dec = -dec; ha = -ha; }
        return (WrapHours(ha), dec, pastPole ? PierSide.pierWest : PierSide.pierEast);
    }

    /// <summary>The axis angles that point at (<paramref name="haHours"/>,
    /// <paramref name="decDeg"/>) from <paramref name="side"/>, with the RA
    /// axis kept within 180° of home so the move never winds the mount round
    /// the long way.</summary>
    public static (double RaAxis, double DecAxis) SkyToAxes(
            double haHours, double decDeg, bool south, PierSide side) {
        double ha = south ? -haHours : haHours;
        double dec = south ? -decDeg : decDeg;
        bool pastPole = side == PierSide.pierWest;
        double decAxis = pastPole ? 180 - dec : dec;
        double raAxis = 15.0 * (pastPole ? ha - 12 : ha);
        double home = Home(south).RaAxis;
        raAxis = home + Wrap180(raAxis - home);
        return (raAxis, decAxis);
    }

    /// <summary>The usual side for a target: counterweight down, east of the
    /// pier for a target west of the meridian and west of it otherwise.</summary>
    public static PierSide SideFor(double haHours)
        => WrapHours(haHours) >= 0 ? PierSide.pierEast : PierSide.pierWest;

    /// <summary>Whether sidereal motion turns the RA axis forward (counts
    /// increasing). It does in the north and not in the south.</summary>
    public static bool TrackingForward(bool south) => !south;

    /// <summary>Whether moving the sky position west (hour angle increasing)
    /// turns the RA axis forward.</summary>
    public static bool WestIsForward(bool south) => !south;

    /// <summary>Whether moving north (declination increasing) turns the Dec
    /// axis forward, which depends on the side the tube is on.</summary>
    public static bool NorthIsForward(bool south, PierSide side)
        => (side == PierSide.pierWest) == south;

    /// <summary>Altitude in degrees of a point at this hour angle and
    /// declination.</summary>
    public static double Altitude(double haHours, double decDeg, double latitudeDeg) {
        double h = haHours * 15 * Math.PI / 180, d = decDeg * Math.PI / 180, l = latitudeDeg * Math.PI / 180;
        return Math.Asin(Math.Sin(d) * Math.Sin(l) + Math.Cos(d) * Math.Cos(l) * Math.Cos(h)) * 180 / Math.PI;
    }

    /// <summary>Azimuth in degrees, from north through east, of a point at
    /// this hour angle and declination.</summary>
    public static double Azimuth(double haHours, double decDeg, double latitudeDeg) {
        double h = haHours * 15 * Math.PI / 180, d = decDeg * Math.PI / 180, l = latitudeDeg * Math.PI / 180;
        double az = Math.Atan2(-Math.Cos(d) * Math.Sin(h),
                               Math.Sin(d) * Math.Cos(l) - Math.Cos(d) * Math.Sin(l) * Math.Cos(h)) * 180 / Math.PI;
        return az < 0 ? az + 360 : az;
    }

    /// <summary>Local sidereal time in hours for a UTC instant and longitude
    /// (degrees, east positive). Meeus 12.4; seconds-level accuracy.</summary>
    public static double LocalSiderealHours(DateTime utc, double longitudeDeg) {
        double jd = utc.ToOADate() + 2415018.5;
        double t = (jd - 2451545.0) / 36525.0;
        double gmst = 280.46061837 + 360.98564736629 * (jd - 2451545.0)
                      + t * t * 0.000387933 - t * t * t / 38710000.0;
        double lst = (gmst + longitudeDeg) % 360;
        if (lst < 0) lst += 360;
        return lst / 15.0;
    }

    public static double WrapHours(double h) {
        h %= 24;
        if (h > 12) h -= 24;
        if (h <= -12) h += 24;
        return h;
    }

    public static double Wrap180(double deg) {
        deg %= 360;
        if (deg > 180) deg -= 360;
        if (deg <= -180) deg += 360;
        return deg;
    }
}
