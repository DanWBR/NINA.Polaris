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
using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>
/// The mount's altitude and azimuth, as shown on the mount card, written into
/// FITS headers and tested by the safety guard.
///
/// On an equatorial mount the axes measure RA and Dec; altitude is a
/// conversion, and Polaris does it from the pointing, the site and the clock.
/// A driver's own HORIZONTAL_COORD is not reliable there: a Star Adventurer
/// GTi over INDI pointing at the zenith (RA/Dec correct, Dec 60° at latitude
/// 60°) reported Alt −42°, a value no site or time gives for that pointing,
/// and the safety guard took it as the truth. On an alt-az mount the encoders
/// do measure altitude and RA/Dec are the derived pair, so the driver's value
/// is kept there (a Seestar's RA/Dec can drift; its altitude does not).
/// </summary>
public static class MountHorizontal {
    /// <summary>Altitude and azimuth in degrees, or the driver's values when
    /// they are the better source (or the only one).</summary>
    public static (double Alt, double Az) Of(ITelescope scope, double latDeg, double lonDeg, DateTime utc) {
        double driverAlt = Read(() => scope.Altitude), driverAz = Read(() => scope.Azimuth);
        if (!PreferComputed(driverAlt, driverAz, IsEquatorial(scope), latDeg, lonDeg))
            return (driverAlt, driverAz);
        var (ra, dec) = scope.PointingOfDate();
        if (!double.IsFinite(ra) || !double.IsFinite(dec)) return (driverAlt, driverAz);
        return AltitudeService.RaDecToAltAz(ra, dec, utc, latDeg, lonDeg);
    }

    /// <summary>Computed from RA/Dec when the driver has no horizontal pair,
    /// or when the mount is equatorial and the site is known.</summary>
    internal static bool PreferComputed(double driverAlt, double driverAz, bool equatorial,
                                        double latDeg, double lonDeg)
        => double.IsNaN(driverAlt) || double.IsNaN(driverAz) || (equatorial && SiteKnown(latDeg, lonDeg));

    /// <summary>A German equatorial reports which side of the pier the tube is
    /// on; an alt-az mount reports unknown.</summary>
    internal static bool IsEquatorial(ITelescope scope) {
        try { return scope.SideOfPier is PierSide.pierEast or PierSide.pierWest; }
        catch { return false; }
    }

    /// <summary>0°, 0° is the profile default, not a site.</summary>
    internal static bool SiteKnown(double latDeg, double lonDeg)
        => double.IsFinite(latDeg) && double.IsFinite(lonDeg) && !(latDeg == 0 && lonDeg == 0);

    private static double Read(Func<double> get) {
        try { return get(); } catch { return double.NaN; }
    }
}
