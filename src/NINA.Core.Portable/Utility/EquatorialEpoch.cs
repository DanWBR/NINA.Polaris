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
/// the SKY map and every plate solve, and the apparent place of date ("JNow"),
/// the frame most mounts point in (INDI's EQUATORIAL_EOD_COORD, an ASCOM
/// driver whose EquatorialSystem is topocentric).
///
/// <para>Polaris works in J2000 everywhere above the mount backends: each
/// backend converts to and from its mount's own frame, once, at the driver
/// boundary. The two differ by the precession accumulated since 2000, about
/// 0.35° (20′) in 2026: enough to put a GoTo off the sensor and the mount's
/// field of view on SKY away from where it really points.</para>
///
/// <para>JNow here is the apparent place, the one ASCOM defines for a
/// topocentric mount and the one INDI's own J2000 ↔ observed conversion and
/// N.I.N.A. use: precession (IAU 1976, Meeus "Astronomical Algorithms" 2nd ed.
/// ch. 21), nutation (IAU 1980, the 18 largest terms of Meeus table 22.A,
/// within 0.02″ of the full series) and annual aberration (Meeus ch. 23, κ =
/// 20.49552″, with the eccentricity term). Without the last two a solved field
/// sat 8 to 10″ from where the mount was sent (issue #46). Left out: diurnal
/// aberration (≤ 0.3″), light deflection, refraction, and the difference
/// between UTC and TT (a minute of Sun motion, far below an arcsecond).</para>
///
/// <para><see cref="J2000ToMeanOfDate"/> keeps precession alone, for code that
/// wants the mean equator and equinox of date rather than what a mount
/// reports.</para>
/// </summary>
public static class EquatorialEpoch {
    /// <summary>J2000 to the apparent place of <paramref name="atUtc"/>.
    /// RA in hours [0, 24), Dec in degrees. NaN stays NaN.</summary>
    public static (double RaHours, double DecDeg) J2000ToJNow(double raHours, double decDeg, DateTime atUtc) {
        if (!TryVector(raHours, decDeg, out var v)) return (double.NaN, double.NaN);
        var f = ApparentFrame.At(atUtc);
        v = Mul(f.NutationPrecession, v);
        return ToSpherical(Normalize(Add(v, f.Aberration)));
    }

    /// <summary>Apparent place of <paramref name="atUtc"/> back to J2000, the
    /// inverse of <see cref="J2000ToJNow"/>.</summary>
    public static (double RaHours, double DecDeg) JNowToJ2000(double raHours, double decDeg, DateTime atUtc) {
        if (!TryVector(raHours, decDeg, out var u)) return (double.NaN, double.NaN);
        var f = ApparentFrame.At(atUtc);
        // Aberration is not a rotation: find v with normalize(v + V) = u.
        // |V| is 1e-4, so each pass gains four digits.
        var v = u;
        for (int i = 0; i < 3; i++) {
            double n = Norm(Add(v, f.Aberration));
            v = Normalize(Sub(Scale(u, n), f.Aberration));
        }
        return ToSpherical(MulT(f.NutationPrecession, v));
    }

    /// <summary>J2000 to the mean equator and equinox of <paramref name="atUtc"/>:
    /// precession only.</summary>
    public static (double RaHours, double DecDeg) J2000ToMeanOfDate(double raHours, double decDeg, DateTime atUtc)
        => TryVector(raHours, decDeg, out var v)
            ? ToSpherical(Mul(PrecessionMatrix(Centuries(atUtc)), v))
            : (double.NaN, double.NaN);

    /// <summary>Mean equator and equinox of <paramref name="atUtc"/> back to
    /// J2000: the exact inverse rotation of <see cref="J2000ToMeanOfDate"/>.</summary>
    public static (double RaHours, double DecDeg) MeanOfDateToJ2000(double raHours, double decDeg, DateTime atUtc)
        => TryVector(raHours, decDeg, out var v)
            ? ToSpherical(MulT(PrecessionMatrix(Centuries(atUtc)), v))
            : (double.NaN, double.NaN);

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

    private const double DegToRad = Math.PI / 180.0;
    private const double ArcsecToRad = DegToRad / 3600.0;

    /// <summary>Everything the apparent place needs for one instant: N · P,
    /// and the Earth's velocity over c in the true equator of date (radians).</summary>
    private readonly record struct ApparentFrame(double[,] NutationPrecession, double[] Aberration) {
        internal static ApparentFrame At(DateTime atUtc) {
            double t = Centuries(atUtc);
            var (dPsi, dEps) = Nutation(t);
            double eps0 = (84381.448 - 46.8150 * t - 0.00059 * t * t + 0.001813 * t * t * t) * ArcsecToRad;
            double eps = eps0 + dEps;
            // N = R1(-ε) · R3(-Δψ) · R1(ε0)
            var n = MulM(R1(-eps), MulM(R3(-dPsi), R1(eps0)));
            return new ApparentFrame(MulM(n, PrecessionMatrix(t)), AberrationVector(t, eps));
        }
    }

    private static double Centuries(DateTime atUtc) {
        var utc = atUtc.Kind == DateTimeKind.Local ? atUtc.ToUniversalTime() : atUtc;
        double jd = utc.ToOADate() + 2415018.5;
        return (jd - 2451545.0) / 36525.0;
    }

    /// <summary>P = R3(-z) · R2(θ) · R3(-ζ), the precession matrix from J2000
    /// to the mean equinox of date (Meeus 21.2 with the J2000 starting epoch,
    /// whose T-free terms vanish).</summary>
    private static double[,] PrecessionMatrix(double t) {
        double zeta = (2306.2181 * t + 0.30188 * t * t + 0.017998 * t * t * t) * ArcsecToRad;
        double zed = (2306.2181 * t + 1.09468 * t * t + 0.018203 * t * t * t) * ArcsecToRad;
        double theta = (2004.3109 * t - 0.42665 * t * t - 0.041833 * t * t * t) * ArcsecToRad;

        double cz = Math.Cos(zeta), sz = Math.Sin(zeta);
        double cZ = Math.Cos(zed), sZ = Math.Sin(zed);
        double ct = Math.Cos(theta), st = Math.Sin(theta);
        return new double[,] {
            { cZ * ct * cz - sZ * sz, -cZ * ct * sz - sZ * cz, -cZ * st },
            { sZ * ct * cz + cZ * sz, -sZ * ct * sz + cZ * cz, -sZ * st },
            { st * cz,                -st * sz,                 ct      },
        };
    }

    // IAU 1980 nutation, largest terms of Meeus table 22.A. Multiples of
    // D, M, M', F, Ω; then Δψ sine and Δε cosine coefficients, constant and
    // per century, in 0.0001″.
    private static readonly (int D, int M, int Mp, int F, int Om, double Psi, double PsiT, double Eps, double EpsT)[] NutationTerms = [
        (0, 0, 0, 0, 1, -171996, -174.2, 92025, 8.9),
        (-2, 0, 0, 2, 2, -13187, -1.6, 5736, -3.1),
        (0, 0, 0, 2, 2, -2274, -0.2, 977, -0.5),
        (0, 0, 0, 0, 2, 2062, 0.2, -895, 0.5),
        (0, 1, 0, 0, 0, 1426, -3.4, 54, -0.1),
        (0, 0, 1, 0, 0, 712, 0.1, -7, 0),
        (-2, 1, 0, 2, 2, -517, 1.2, 224, -0.6),
        (0, 0, 0, 2, 1, -386, -0.4, 200, 0),
        (0, 0, 1, 2, 2, -301, 0, 129, -0.1),
        (-2, -1, 0, 2, 2, 217, -0.5, -95, 0.3),
        (-2, 0, 1, 0, 0, -158, 0, 0, 0),
        (-2, 0, 0, 2, 1, 129, 0.1, -70, 0),
        (0, 0, -1, 2, 2, 123, 0, -53, 0),
        (2, 0, 0, 0, 0, 63, 0, 0, 0),
        (0, 0, 1, 0, 1, 63, 0.1, -33, 0),
        (2, 0, -1, 2, 2, -59, 0, 26, 0),
        (0, 0, -1, 0, 1, -58, -0.1, 32, 0),
        (0, 0, 1, 2, 1, -51, 0, 27, 0),
    ];

    /// <summary>Nutation in longitude and in obliquity, radians.</summary>
    private static (double DPsi, double DEps) Nutation(double t) {
        double d = 297.85036 + 445267.111480 * t - 0.0019142 * t * t + t * t * t / 189474;
        double m = 357.52772 + 35999.050340 * t - 0.0001603 * t * t - t * t * t / 300000;
        double mp = 134.96298 + 477198.867398 * t + 0.0086972 * t * t + t * t * t / 56250;
        double f = 93.27191 + 483202.017538 * t - 0.0036825 * t * t + t * t * t / 327270;
        double om = 125.04452 - 1934.136261 * t + 0.0020708 * t * t + t * t * t / 450000;
        double psi = 0, eps = 0;
        foreach (var k in NutationTerms) {
            double arg = (k.D * d + k.M * m + k.Mp * mp + k.F * f + k.Om * om) * DegToRad;
            psi += (k.Psi + k.PsiT * t) * Math.Sin(arg);
            eps += (k.Eps + k.EpsT * t) * Math.Cos(arg);
        }
        return (psi * 1e-4 * ArcsecToRad, eps * 1e-4 * ArcsecToRad);
    }

    /// <summary>Annual aberration as the Earth's velocity over c, in the true
    /// equator of date. In ecliptic coordinates it is κ (sin ⊙ − e sin ϖ,
    /// −cos ⊙ + e cos ϖ, 0), which reproduces Meeus 23.2 term for term.</summary>
    private static double[] AberrationVector(double t, double eps) {
        double l0 = 280.46646 + 36000.76983 * t + 0.0003032 * t * t;
        double m = (357.52911 + 35999.05029 * t - 0.0001537 * t * t) * DegToRad;
        double c = (1.914602 - 0.004817 * t - 0.000014 * t * t) * Math.Sin(m)
                 + (0.019993 - 0.000101 * t) * Math.Sin(2 * m)
                 + 0.000289 * Math.Sin(3 * m);
        double sun = (l0 + c) * DegToRad;
        double e = 0.016708634 - 0.000042037 * t - 0.0000001267 * t * t;
        double peri = (102.93735 + 1.71946 * t + 0.00046 * t * t) * DegToRad;
        const double kappa = 20.49552 * ArcsecToRad;
        double x = kappa * (Math.Sin(sun) - e * Math.Sin(peri));
        double y = kappa * (-Math.Cos(sun) + e * Math.Cos(peri));
        return [x, y * Math.Cos(eps), y * Math.Sin(eps)];
    }

    private static double[,] R1(double a) {
        double c = Math.Cos(a), s = Math.Sin(a);
        return new double[,] { { 1, 0, 0 }, { 0, c, s }, { 0, -s, c } };
    }

    private static double[,] R3(double a) {
        double c = Math.Cos(a), s = Math.Sin(a);
        return new double[,] { { c, s, 0 }, { -s, c, 0 }, { 0, 0, 1 } };
    }

    private static bool TryVector(double raHours, double decDeg, out double[] v) {
        v = [];
        if (!double.IsFinite(raHours) || !double.IsFinite(decDeg)) return false;
        double ra = raHours * 15.0 * DegToRad;
        double dec = Math.Clamp(decDeg, -90.0, 90.0) * DegToRad;
        v = [Math.Cos(dec) * Math.Cos(ra), Math.Cos(dec) * Math.Sin(ra), Math.Sin(dec)];
        return true;
    }

    private static (double, double) ToSpherical(double[] v) {
        double ra = Math.Atan2(v[1], v[0]) / DegToRad / 15.0;
        double dec = Math.Asin(Math.Clamp(v[2], -1.0, 1.0)) / DegToRad;
        return (WrapHours(ra), dec);
    }

    private static double[,] MulM(double[,] a, double[,] b) {
        var r = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                r[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j];
        return r;
    }

    private static double[] Mul(double[,] m, double[] v) => [
        m[0, 0] * v[0] + m[0, 1] * v[1] + m[0, 2] * v[2],
        m[1, 0] * v[0] + m[1, 1] * v[1] + m[1, 2] * v[2],
        m[2, 0] * v[0] + m[2, 1] * v[1] + m[2, 2] * v[2],
    ];

    // A rotation's inverse is its transpose.
    private static double[] MulT(double[,] m, double[] v) => [
        m[0, 0] * v[0] + m[1, 0] * v[1] + m[2, 0] * v[2],
        m[0, 1] * v[0] + m[1, 1] * v[1] + m[2, 1] * v[2],
        m[0, 2] * v[0] + m[1, 2] * v[1] + m[2, 2] * v[2],
    ];

    private static double[] Add(double[] a, double[] b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
    private static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
    private static double[] Scale(double[] a, double k) => [a[0] * k, a[1] * k, a[2] * k];
    private static double Norm(double[] a) => Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
    private static double[] Normalize(double[] a) => Scale(a, 1.0 / Norm(a));
}
