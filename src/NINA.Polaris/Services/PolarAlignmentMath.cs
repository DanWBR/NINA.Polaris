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

namespace NINA.Polaris.Services;

/// <summary>
/// Polar-axis fit for TPPA (Three-Point Polar Alignment).
///
/// Algorithm (Challis 1879, modernised; same shape as N.I.N.A. desktop,
/// SharpCap, and KStars use):
///
///   - Each plate-solved point gives the TRUE sky direction the
///     optical axis was pointing at instant t (RA/Dec).
///   - A perfectly polar-aligned mount, as it rotates in HA at the
///     sidereal rate, sweeps the optical axis along a small circle
///     of constant declination.
///   - A misaligned mount still produces a small circle on the
///     celestial sphere; the pole of that circle is the MOUNT's
///     rotation axis (not the celestial pole).
///   - Pick any 3 points along that small circle (mount tracking
///     ON for a few seconds, then we slew the mount in RA, then
///     a second short tracking interval, equivalently three
///     RA-offset samples). The unit vectors to those three points
///     lie on the plane of the small circle. The plane's normal is
///     the mount's polar axis direction.
///   - Subtracting the celestial pole direction (which depends on
///     hemisphere) gives the polar misalignment as a small (alt,
///     az) error vector.
///
/// This implementation works in the topocentric Alt/Az frame so
/// the error directly comes out as (azError, altError) in arcsec,
/// matching what the user needs to adjust on the mount knobs.
/// </summary>
public static class PolarAlignmentMath {
    /// <summary>Compute polar-axis offset from 3 plate-solved points.
    /// Returns (azErrorArcsec, altErrorArcsec). Positive azimuth means
    /// the mount's polar axis is east of true pole; positive altitude
    /// means it's higher than true pole. The UI's arrow points the
    /// direction the visual user-facing polar axis should NUDGE to
    /// reduce the error to zero.</summary>
    public static (double azErrSec, double altErrSec) ComputeError(
        PolarPoint p1, PolarPoint p2, PolarPoint p3,
        double siteLatDeg, double siteLongDeg) {

        // 1. Convert each (RA, Dec, time) → unit vector in the local
        //    Alt/Az topocentric frame at that instant.
        var v1 = RaDecToAltAzVector(p1.RaHours, p1.DecDeg, p1.AtUtc, siteLatDeg, siteLongDeg);
        var v2 = RaDecToAltAzVector(p2.RaHours, p2.DecDeg, p2.AtUtc, siteLatDeg, siteLongDeg);
        var v3 = RaDecToAltAzVector(p3.RaHours, p3.DecDeg, p3.AtUtc, siteLatDeg, siteLongDeg);

        // 2. Plane normal = (v2 - v1) × (v3 - v1). All three vectors
        //    sit on the small circle whose axis is the mount's rotation
        //    axis. The normal of THEIR plane IS that axis.
        var a = Sub(v2, v1);
        var b = Sub(v3, v1);
        var n = Normalize(Cross(a, b));

        // 3. The mount's polar axis vector in Alt/Az coordinates.
        //    Northern hemisphere: ideal axis points to Alt=lat, Az=0
        //    (north). Southern hemisphere: Alt=|lat|, Az=180 (south).
        //    We disambiguate which end of the n vector is the axis
        //    "north pole" by picking the one closer to the expected
        //    ideal axis direction (otherwise we'd report a 180°
        //    misalignment when the cross product happened to flip).
        var idealAxis = HemisphereIdealAxis(siteLatDeg);
        if (Dot(n, idealAxis) < 0) {
            n = V(-n.X, -n.Y, -n.Z);
        }

        // 4. Convert the mount-axis vector to (Alt, Az). Subtract from
        //    the ideal to get the residual error.
        var (mountAltDeg, mountAzDeg) = AltAzFromVector(n);

        double idealAltDeg = Math.Abs(siteLatDeg);
        double idealAzDeg = siteLatDeg >= 0 ? 0.0 : 180.0;

        double altErrDeg = mountAltDeg - idealAltDeg;
        double azErrDeg = NormalizeAzDelta(mountAzDeg - idealAzDeg);

        return (azErrDeg * 3600.0, altErrDeg * 3600.0);
    }

    /// <summary>Total angular error magnitude in arcsec, what the UI
    /// arrow's length encodes.</summary>
    public static double TotalErrorArcsec(double azErrSec, double altErrSec) {
        return Math.Sqrt(azErrSec * azErrSec + altErrSec * altErrSec);
    }

    /// <summary>Great-circle separation between two RA/Dec positions, in
    /// degrees. Used to reject a degenerate 3-point set (points too close
    /// together) before feeding them to <see cref="ComputeError"/> — three
    /// near-coincident directions give a cross-product dominated by noise,
    /// so the fitted axis (and the error vector) would be garbage.</summary>
    public static double AngularSeparationDeg(
        double ra1Hours, double dec1Deg, double ra2Hours, double dec2Deg) {
        double ra1 = ra1Hours * 15.0 * Math.PI / 180.0;
        double ra2 = ra2Hours * 15.0 * Math.PI / 180.0;
        double d1 = dec1Deg * Math.PI / 180.0;
        double d2 = dec2Deg * Math.PI / 180.0;
        double cosSep = Math.Sin(d1) * Math.Sin(d2)
                      + Math.Cos(d1) * Math.Cos(d2) * Math.Cos(ra1 - ra2);
        return Math.Acos(Math.Clamp(cosSep, -1.0, 1.0)) * 180.0 / Math.PI;
    }

    /// <summary>
    /// RDPA-1: single-target polar-error estimate. Used by the
    /// "rudimentary" alignment workflow: user slews to ONE known
    /// target, plate-solve gives the actual pointing, and we
    /// attribute the entire alt/az offset to polar misalignment.
    ///
    /// Why this works iteratively even though it's an approximation:
    ///   - Pointing error has two main sources: polar-axis
    ///     misalignment (which is what we're trying to fix), and
    ///     mount/optical-train pointing-model errors (cone error,
    ///     non-orthogonality, etc.). Single frame can't separate
    ///     them, so we lump everything into polar.
    ///   - After 1-2 manual nudges to azimuth + altitude knobs, the
    ///     polar component dominates the *change* between iterations.
    ///     The pointing-model component is roughly constant and
    ///     vanishes from the delta the user sees on the arrow.
    ///   - This is the same approximation SharpCap's "Plate-Solve
    ///     Polar Alignment" and KStars' single-target mode use, and
    ///     is the algorithm the operator already runs by hand on
    ///     the ASIAIR.
    ///
    /// Sign convention (matches ComputeError above so the canvas
    /// arrow renderer doesn't need a separate code path):
    ///   - Positive azErrSec → mount pointed east of where it should
    ///     have, indicating the polar axis is east of true pole.
    ///     User nudges azimuth knob WESTWARD to reduce.
    ///   - Positive altErrSec → mount pointed above target, polar
    ///     axis altitude too high. User nudges altitude knob DOWN.
    /// </summary>
    public static (double azErrSec, double altErrSec) ComputeErrorSingleTarget(
        double targetRaHours, double targetDecDeg,
        double solvedRaHours, double solvedDecDeg,
        double siteLatDeg, double siteLongDeg,
        DateTime utcNow) {

        // 1. Both target and solved positions resolved to their
        //    alt/az at the same instant. Same LST cancels out, so
        //    the difference reflects ONLY the polar misalignment +
        //    pointing-model error, not sidereal drift.
        var vTarget = RaDecToAltAzVector(
            targetRaHours, targetDecDeg, utcNow, siteLatDeg, siteLongDeg);
        var vSolved = RaDecToAltAzVector(
            solvedRaHours, solvedDecDeg, utcNow, siteLatDeg, siteLongDeg);

        var (targetAltDeg, targetAzDeg) = AltAzFromVector(vTarget);
        var (solvedAltDeg, solvedAzDeg) = AltAzFromVector(vSolved);

        // 2. Decompose delta. Altitude is the easy axis (no
        //    cosine factor); azimuth has to be scaled by cos(alt)
        //    so 1" of azimuth at the zenith reads the same arcsec
        //    magnitude as 1" near the horizon (otherwise high-altitude
        //    targets would report inflated az errors the user can't
        //    physically adjust).
        double altErrDeg = solvedAltDeg - targetAltDeg;
        double azErrDeg = NormalizeAzDelta(solvedAzDeg - targetAzDeg);

        double altErrSec = altErrDeg * 3600.0;
        double azErrSec = azErrDeg * 3600.0
            * Math.Cos(targetAltDeg * Math.PI / 180.0);

        return (azErrSec, altErrSec);
    }

    /// <summary>
    /// POLARUI2: refinement anchor. Given the pointing at the moment the
    /// knobs have NOT yet been touched (reference solve) and the axis
    /// error from TPPA, compute the of-date RA/Dec the SAME pointing
    /// will have once the user fully corrects the mount.
    ///
    /// Why this works: turning the az/alt bolts rotates the ENTIRE mount
    /// rigidly — the correction rotation C (about the zenith by −azErr,
    /// about the east-west hinge by the angle that moves the axis
    /// −altErr) maps every direction, so C·(current pointing) is where
    /// the pointing lands when the axis lands on the pole. And because
    /// the pointing tracks about the mount axis while the target RA/Dec
    /// tracks about the true pole = C·axis, the SAME ground-frame C maps
    /// pointing→target at every instant: the target RA/Dec is constant
    /// in time. Each refresh then needs only ONE solve — no 3-point
    /// re-sweep, no sliding window, no degeneracy. (This is the NINA
    /// desktop TPPA adjustment-phase approach.)
    /// </summary>
    public static (double raHours, double decDeg) ComputeRefineTarget(
        double refRaHours, double refDecDeg, DateTime atUtc,
        double azErrSec, double altErrSec,
        double siteLatDeg, double siteLongDeg) {

        var v = RaDecToAltAzVector(refRaHours, refDecDeg, atUtc, siteLatDeg, siteLongDeg);
        var (b, a) = KnobAnglesFromError(azErrSec, altErrSec, siteLatDeg);
        var vt = RotateAboutEast(RotateAzimuth(v, b), a);
        return AltAzVectorToRaDec(vt, atUtc, siteLatDeg, siteLongDeg);
    }

    /// <summary>
    /// POLARUI2: remaining axis error from ONE solve during refinement.
    /// Decomposes the rotation that takes the current solved pointing to
    /// the anchored target into the two physical knob rotations (about
    /// the zenith + about the east-west altitude hinge) via Gauss-Newton
    /// on the exact rotations, then maps those angles back to the TPPA
    /// (azErrSec, altErrSec) sign convention. Returns null when the
    /// pointing direction makes the 2-parameter decomposition
    /// ill-conditioned (pointing at the zenith, or at the due-east/west
    /// horizon where the alt hinge can't move it).
    /// </summary>
    public static (double azErrSec, double altErrSec)? ComputeRefineError(
        double targetRaHours, double targetDecDeg,
        double solvedRaHours, double solvedDecDeg,
        DateTime atUtc, double siteLatDeg, double siteLongDeg) {

        var vt = RaDecToAltAzVector(targetRaHours, targetDecDeg, atUtc, siteLatDeg, siteLongDeg);
        var vn = RaDecToAltAzVector(solvedRaHours, solvedDecDeg, atUtc, siteLatDeg, siteLongDeg);

        double b = 0, a = 0;   // azimuth-angle + about-east knob rotations
        var v = vn;
        for (int i = 0; i < 12; i++) {
            var d = Sub(vt, v);
            // Small-rotation generators at the current iterate:
            //   ∂v/∂b (azimuth + toward east)  = (v.Y, −v.X, 0)
            //   ∂v/∂a (right-hand about east)  = x̂×v = (0, −v.Z, v.Y)
            var g1 = V(v.Y, -v.X, 0);
            var g2 = V(0, -v.Z, v.Y);
            double a11 = Dot(g1, g1), a12 = Dot(g1, g2), a22 = Dot(g2, g2);
            double det = a11 * a22 - a12 * a12;
            if (det < 1e-9) return null;
            double r1 = Dot(g1, d), r2 = Dot(g2, d);
            double db = (a22 * r1 - a12 * r2) / det;
            double da = (-a12 * r1 + a11 * r2) / det;
            b += db; a += da;
            v = RotateAboutEast(RotateAzimuth(vn, b), a);
            if (db * db + da * da < 1e-20) break;
        }

        // Invert the knob→error mapping used by ComputeRefineTarget.
        double aSign = siteLatDeg >= 0 ? 1.0 : -1.0;
        double azErrSec = -b * 180.0 / Math.PI * 3600.0;
        double altErrSec = -a * aSign * 180.0 / Math.PI * 3600.0;
        return (azErrSec, altErrSec);
    }

    /// <summary>Error → the two physical knob rotation angles (radians).
    /// b: azimuth-angle added to every direction (rotation about the
    /// zenith); a: right-hand rotation about the east axis. A rotation
    /// about east by a changes a direction's altitude by a·cos(azimuth),
    /// so raising the AXIS by δ needs a = δ (axis toward north, cos=+1)
    /// or a = −δ (axis toward south, cos=−1). The correction removes the
    /// error, hence the minus signs.</summary>
    private static (double b, double a) KnobAnglesFromError(
            double azErrSec, double altErrSec, double siteLatDeg) {
        double aSign = siteLatDeg >= 0 ? 1.0 : -1.0;
        double b = -(azErrSec / 3600.0) * Math.PI / 180.0;
        double a = -(altErrSec / 3600.0) * Math.PI / 180.0 / aSign;
        return (b, a);
    }

    /// <summary>
    /// Precess equatorial coordinates from J2000.0 to the mean equator and
    /// equinox of date (rigorous precession, Meeus "Astronomical Algorithms"
    /// 2nd ed., ch. 21, eqs. 21.2–21.4).
    ///
    /// Why this is needed: plate solvers (ASTAP) return J2000, but the
    /// Alt/Az transform here uses Local *Sidereal* Time of date. Feeding
    /// J2000 coordinates straight in offsets every direction by the
    /// accumulated precession since 2000 (~0.35° in 2026) — for a single
    /// set of points compared against the true (of-date) pole, as TPPA
    /// does, that becomes a real systematic polar-error bias far larger
    /// than the arcminute alignment goal. So the caller must precess the
    /// solved J2000 coordinates to date before building a PolarPoint.
    ///
    /// Nutation (≤17″) and aberration (≤20″) are intentionally omitted:
    /// they're below the polar-alignment target and keep this routine
    /// dependency-free (no SOFA/NOVAS in Polaris).
    /// </summary>
    public static (double raHours, double decDeg) PrecessJ2000ToDate(
        double raHours, double decDeg, DateTime atUtc) {
        double jd = atUtc.ToOADate() + 2415018.5;
        double t = (jd - 2451545.0) / 36525.0;   // Julian centuries from J2000

        // Accumulation angles, arcsec → radians. Starting epoch is J2000
        // so the constant (T0-based) terms in Meeus 21.2 vanish.
        const double asToRad = Math.PI / 180.0 / 3600.0;
        double zeta = (2306.2181 * t + 0.30188 * t * t + 0.017998 * t * t * t) * asToRad;
        double zed = (2306.2181 * t + 1.09468 * t * t + 0.018203 * t * t * t) * asToRad;
        double theta = (2004.3109 * t - 0.42665 * t * t - 0.041833 * t * t * t) * asToRad;

        double ra0 = raHours * 15.0 * Math.PI / 180.0;
        double dec0 = decDeg * Math.PI / 180.0;

        double A = Math.Cos(dec0) * Math.Sin(ra0 + zeta);
        double B = Math.Cos(theta) * Math.Cos(dec0) * Math.Cos(ra0 + zeta)
                 - Math.Sin(theta) * Math.Sin(dec0);
        double C = Math.Sin(theta) * Math.Cos(dec0) * Math.Cos(ra0 + zeta)
                 + Math.Cos(theta) * Math.Sin(dec0);

        double ra = Math.Atan2(A, B) + zed;
        double dec = Math.Asin(Math.Clamp(C, -1.0, 1.0));

        double raH = ra * 180.0 / Math.PI / 15.0;
        raH = ((raH % 24.0) + 24.0) % 24.0;
        return (raH, dec * 180.0 / Math.PI);
    }

    // ---------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------

    private record struct Vec3(double X, double Y, double Z);

    private static Vec3 V(double x, double y, double z) => new(x, y, z);

    /// <summary>RA/Dec at time t → unit vector in topocentric Alt/Az
    /// frame. X = east, Y = north, Z = up.</summary>
    private static Vec3 RaDecToAltAzVector(double raHours, double decDeg,
                                            DateTime atUtc, double latDeg, double longDeg) {
        // Local Sidereal Time at the observer at instant t.
        double lstHours = LocalSiderealHours(atUtc, longDeg);

        // Hour Angle = LST - RA (both in hours).
        double haHours = lstHours - raHours;
        double haDeg = haHours * 15.0;
        double haRad = haDeg * Math.PI / 180.0;
        double decRad = decDeg * Math.PI / 180.0;
        double latRad = latDeg * Math.PI / 180.0;

        // Standard equatorial → horizontal transform (e.g. Meeus eq. 13.6).
        double sinAlt = Math.Sin(decRad) * Math.Sin(latRad)
                      + Math.Cos(decRad) * Math.Cos(latRad) * Math.Cos(haRad);
        double altRad = Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0));

        // Azimuth measured from north, increasing eastward (typical
        // astronomy convention, N=0°, E=90°, S=180°, W=270°).
        double sinAz = -Math.Cos(decRad) * Math.Sin(haRad);
        double cosAz = Math.Sin(decRad) * Math.Cos(latRad)
                     - Math.Cos(decRad) * Math.Sin(latRad) * Math.Cos(haRad);
        double azRad = Math.Atan2(sinAz, cosAz);
        if (azRad < 0) azRad += 2 * Math.PI;

        // Unit vector in (east, north, up) coordinates.
        double cosAlt = Math.Cos(altRad);
        return V(
            cosAlt * Math.Sin(azRad),
            cosAlt * Math.Cos(azRad),
            Math.Sin(altRad));
    }

    /// <summary>Exact rotation about the UP axis that ADDS b radians to
    /// the azimuth of every direction (az from north toward east). At
    /// b→0 the derivative is (v.Y, −v.X, 0), matching the g1 generator
    /// in ComputeRefineError.</summary>
    private static Vec3 RotateAzimuth(Vec3 v, double b) {
        double c = Math.Cos(b), s = Math.Sin(b);
        return V(v.X * c + v.Y * s,
                 -v.X * s + v.Y * c,
                 v.Z);
    }

    /// <summary>Exact right-hand rotation about the EAST axis (x̂) by a
    /// radians: raises north-azimuth directions, lowers south ones. At
    /// a→0 the derivative is x̂×v = (0, −v.Z, v.Y), the g2 generator.</summary>
    private static Vec3 RotateAboutEast(Vec3 v, double a) {
        double c = Math.Cos(a), s = Math.Sin(a);
        return V(v.X,
                 v.Y * c - v.Z * s,
                 v.Y * s + v.Z * c);
    }

    /// <summary>Topocentric (east, north, up) unit vector → of-date
    /// RA/Dec at the given instant. Inverse of RaDecToAltAzVector
    /// (standard horizontal → equatorial transform).</summary>
    private static (double raHours, double decDeg) AltAzVectorToRaDec(
            Vec3 v, DateTime atUtc, double latDeg, double longDeg) {
        var (altDeg, azDeg) = AltAzFromVector(v);
        double alt = altDeg * Math.PI / 180.0;
        double az = azDeg * Math.PI / 180.0;
        double lat = latDeg * Math.PI / 180.0;

        double sinDec = Math.Sin(alt) * Math.Sin(lat)
                      + Math.Cos(alt) * Math.Cos(lat) * Math.Cos(az);
        double dec = Math.Asin(Math.Clamp(sinDec, -1.0, 1.0));
        double cosDec = Math.Cos(dec);

        // From cosAlt·sinAz = −cosDec·sinHA and
        // sinAlt = sinDec·sinLat + cosDec·cosLat·cosHA.
        double sinHa = -Math.Sin(az) * Math.Cos(alt) / Math.Max(1e-12, cosDec);
        double cosHa = (Math.Sin(alt) - sinDec * Math.Sin(lat))
                     / Math.Max(1e-12, cosDec * Math.Cos(lat));
        double haHours = Math.Atan2(sinHa, cosHa) * 180.0 / Math.PI / 15.0;

        double raH = LocalSiderealHours(atUtc, longDeg) - haHours;
        raH = ((raH % 24.0) + 24.0) % 24.0;
        return (raH, dec * 180.0 / Math.PI);
    }

    /// <summary>Vector → (altDeg, azDeg). Inverse of RaDecToAltAzVector
    /// once you already have the topocentric Cartesian.</summary>
    private static (double altDeg, double azDeg) AltAzFromVector(Vec3 v) {
        double altRad = Math.Asin(Math.Clamp(v.Z, -1.0, 1.0));
        double azRad = Math.Atan2(v.X, v.Y);
        if (azRad < 0) azRad += 2 * Math.PI;
        return (altRad * 180.0 / Math.PI, azRad * 180.0 / Math.PI);
    }

    /// <summary>Direction of the ideal polar axis at this latitude,
    /// as a (east, north, up) unit vector. Northern: tilted toward
    /// north up at altitude=lat; southern: toward south up at
    /// altitude=|lat|.</summary>
    private static Vec3 HemisphereIdealAxis(double latDeg) {
        double altDeg = Math.Abs(latDeg);
        double azDeg = latDeg >= 0 ? 0.0 : 180.0;
        double altRad = altDeg * Math.PI / 180.0;
        double azRad = azDeg * Math.PI / 180.0;
        double cosAlt = Math.Cos(altRad);
        return V(
            cosAlt * Math.Sin(azRad),
            cosAlt * Math.Cos(azRad),
            Math.Sin(altRad));
    }

    /// <summary>Local Sidereal Time at the given UTC instant +
    /// observer longitude (east positive, degrees). Returned in
    /// hours [0, 24). Meeus formula 12.4, good to a few seconds
    /// over decades, far better than TPPA needs. Public so the
    /// TPPA sweep can pick a meridian-safe slew direction from the
    /// start position's hour angle.</summary>
    public static double LocalSiderealHours(DateTime utc, double longDeg) {
        // Julian Date, DateTime.ToOADate() returns days since 1899-12-30 12:00 UT.
        double jd = utc.ToOADate() + 2415018.5;
        double t = (jd - 2451545.0) / 36525.0;

        // Greenwich Mean Sidereal Time at 0h UT of the date.
        double gmstDeg = 280.46061837
                      + 360.98564736629 * (jd - 2451545.0)
                      + 0.000387933 * t * t
                      - (t * t * t) / 38710000.0;
        gmstDeg = ((gmstDeg % 360.0) + 360.0) % 360.0;

        double lmstDeg = (gmstDeg + longDeg + 360.0) % 360.0;
        return lmstDeg / 15.0;
    }

    /// <summary>Wrap az delta into (-180, +180] so the magnitude makes
    /// sense as a small correction value.</summary>
    private static double NormalizeAzDelta(double deg) {
        var d = ((deg + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
        return d;
    }

    private static Vec3 Sub(Vec3 a, Vec3 b) => V(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static Vec3 Cross(Vec3 a, Vec3 b) => V(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);
    private static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static Vec3 Normalize(Vec3 v) {
        double m = Math.Sqrt(Dot(v, v));
        return m > 0 ? V(v.X / m, v.Y / m, v.Z / m) : v;
    }
}