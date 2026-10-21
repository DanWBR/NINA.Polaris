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

namespace NINA.Core.Utility;

/// <summary>
/// The two equatorial frames Polaris meets: J2000, the frame of every catalog,
/// the SKY map and every plate solve, and the mean equator and equinox of date
/// ("JNow"), the frame most mounts point in (INDI's EQUATORIAL_EOD_COORD, an
/// ASCOM driver whose EquatorialSystem is topocentric).
///
/// <para>Polaris works in J2000 everywhere above the mount backends: each
/// backend converts to and from its mount's own frame, once, at the driver
/// boundary. The two differ by the precession accumulated since 2000, about
/// 0.35° (20′) in 2026: enough to put a GoTo off the sensor and the mount's
/// field of view on SKY away from where it really points.</para>
///
/// <para>Precision: rigorous precession (IAU 1976, Meeus "Astronomical
/// Algorithms" 2nd ed. ch. 21) and nothing else. Nutation (≤ 17″) and
/// aberration (≤ 20″) are left out, so against a full apparent-place
/// transformation this is good to well under an arcminute: below what a GoTo
/// resolves, and a plate-solve sync measures the mount in the same frame it
/// is then commanded in, so centering converges regardless.</para>
/// </summary>
public static class EquatorialEpoch {
    // TODO: Investigate nutation and annual aberration in mount coordinate conversions: https://github.com/DanWBR/NINA.Polaris/issues/46
    /// <summary>J2000 to the mean equator and equinox of <paramref name="atUtc"/>.
    /// RA in hours [0, 24), Dec in degrees. NaN stays NaN.</summary>
    public static (double RaHours, double DecDeg) J2000ToJNow(double raHours, double decDeg, DateTime atUtc)
        => Rotate(raHours, decDeg, atUtc, inverse: false);

    /// <summary>Mean equator and equinox of <paramref name="atUtc"/> back to J2000:
    /// the exact inverse rotation of <see cref="J2000ToJNow"/>.</summary>
    public static (double RaHours, double DecDeg) JNowToJ2000(double raHours, double decDeg, DateTime atUtc)
        => Rotate(raHours, decDeg, atUtc, inverse: true);

    /// <summary>A position the mount reported, in J2000: converted when the
    /// mount points in JNow, as is when it already works in J2000.</summary>
    public static (double RaHours, double DecDeg) FromMount(double raHours, double decDeg, bool mountIsJNow)
        => mountIsJNow ? JNowToJ2000(raHours, decDeg, DateTime.UtcNow) : (raHours, decDeg);

    /// <summary>A J2000 position in the mount's own frame, for a slew or a sync.</summary>
    public static (double RaHours, double DecDeg) ToMount(double raHours, double decDeg, bool mountIsJNow)
        => mountIsJNow ? J2000ToJNow(raHours, decDeg, DateTime.UtcNow) : (raHours, decDeg);

    /// <summary>Whether an ASCOM / Alpaca driver's <c>EquatorialSystem</c>
    /// (EquatorialCoordinateType) means JNow. 2 is J2000; 1 is topocentric
    /// (JNow), what nearly every driver reports; 0 ("other") and the rare
    /// J2050 / B1950 are taken as JNow, the frame a driver that cannot say is
    /// most likely in.</summary>
    public static bool AscomSystemIsJNow(int equatorialSystem) => equatorialSystem != 2;

    /// <summary>Normalise an RA in hours to [0, 24).</summary>
    public static double WrapHours(double raHours) {
        var r = raHours % 24.0;
        if (r < 0) r += 24.0;
        return r >= 24.0 ? 0.0 : r;
    }

    private static (double, double) Rotate(double raHours, double decDeg, DateTime atUtc, bool inverse) {
        if (double.IsNaN(raHours) || double.IsNaN(decDeg) || double.IsInfinity(raHours) || double.IsInfinity(decDeg))
            return (double.NaN, double.NaN);
        var m = PrecessionMatrix(atUtc);

        double ra = raHours * 15.0 * Math.PI / 180.0;
        double dec = Math.Clamp(decDeg, -90.0, 90.0) * Math.PI / 180.0;
        double x = Math.Cos(dec) * Math.Cos(ra);
        double y = Math.Cos(dec) * Math.Sin(ra);
        double z = Math.Sin(dec);

        double nx, ny, nz;
        if (!inverse) {
            nx = m[0, 0] * x + m[0, 1] * y + m[0, 2] * z;
            ny = m[1, 0] * x + m[1, 1] * y + m[1, 2] * z;
            nz = m[2, 0] * x + m[2, 1] * y + m[2, 2] * z;
        } else {
            // A rotation's inverse is its transpose.
            nx = m[0, 0] * x + m[1, 0] * y + m[2, 0] * z;
            ny = m[0, 1] * x + m[1, 1] * y + m[2, 1] * z;
            nz = m[0, 2] * x + m[1, 2] * y + m[2, 2] * z;
        }

        double outRa = Math.Atan2(ny, nx) * 180.0 / Math.PI / 15.0;
        double outDec = Math.Asin(Math.Clamp(nz, -1.0, 1.0)) * 180.0 / Math.PI;
        return (WrapHours(outRa), outDec);
    }

    /// <summary>P = R3(-z) · R2(θ) · R3(-ζ), the precession matrix from J2000
    /// to the mean equinox of date (Meeus 21.2 with the J2000 starting epoch,
    /// whose T-free terms vanish).</summary>
    private static double[,] PrecessionMatrix(DateTime atUtc) {
        var utc = atUtc.Kind == DateTimeKind.Local ? atUtc.ToUniversalTime() : atUtc;
        double jd = utc.ToOADate() + 2415018.5;
        double t = (jd - 2451545.0) / 36525.0;
        const double asToRad = Math.PI / 180.0 / 3600.0;
        double zeta = (2306.2181 * t + 0.30188 * t * t + 0.017998 * t * t * t) * asToRad;
        double zed = (2306.2181 * t + 1.09468 * t * t + 0.018203 * t * t * t) * asToRad;
        double theta = (2004.3109 * t - 0.42665 * t * t - 0.041833 * t * t * t) * asToRad;

        double cz = Math.Cos(zeta), sz = Math.Sin(zeta);
        double cZ = Math.Cos(zed), sZ = Math.Sin(zed);
        double ct = Math.Cos(theta), st = Math.Sin(theta);
        return new double[,] {
            { cZ * ct * cz - sZ * sz, -cZ * ct * sz - sZ * cz, -cZ * st },
            { sZ * ct * cz + cZ * sz, -sZ * ct * sz + cZ * cz, -sZ * st },
            { st * cz,                -st * sz,                 ct      },
        };
    }
}
