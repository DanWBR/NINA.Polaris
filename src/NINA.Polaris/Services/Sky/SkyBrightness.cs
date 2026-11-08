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

using System.Text.RegularExpressions;

namespace NINA.Polaris.Services.Sky;

/// <summary>
/// Sky brightness for target ranking: the site's dark-sky zenith brightness
/// (from the Bortle class or an SQM reading), brightened by the Moon and by
/// airmass at the target's position, against the target's mean surface
/// brightness.
///
/// <para>All brightnesses are V-band mag/arcsec² (higher is darker). The Moon
/// term is Krisciunas &amp; Schaefer (1991, PASP 103, 1033), the standard
/// model behind most planners; the airmass term is theirs too. Neither knows
/// about clouds or the site's light domes, so the result is a ranking aid,
/// not a prediction of what an SQM will read.</para>
/// </summary>
public static class SkyBrightness {
    /// <summary>Zenith SQM taken as typical of each Bortle class (index 1..9),
    /// inside the usual class ranges (class 4: 20.49 to 21.69; class 6:
    /// 18.94 to 19.50; 8 and 9 share "below 18.38").</summary>
    private static readonly double[] BortleSqm = { 0, 22.0, 21.93, 21.8, 21.1, 20.0, 19.2, 18.65, 18.1, 17.5 };

    /// <summary>Lower SQM bound of each class 1..8; anything brighter than
    /// class 8's bound is class 9.</summary>
    private static readonly double[] BortleFloor = { 0, 21.99, 21.89, 21.69, 20.49, 19.50, 18.94, 18.38, 17.80 };

    public static double SqmForBortle(int bortle) => BortleSqm[Math.Clamp(bortle, 1, 9)];

    public static int BortleForSqm(double sqm) {
        for (int b = 1; b <= 8; b++)
            if (sqm >= BortleFloor[b]) return b;
        return 9;
    }

    /// <summary>A plausible zenith SQM, as typed or read off a meter.</summary>
    public static bool IsValidSqm(double v) => double.IsFinite(v) && v >= 15 && v <= 22.5;

    // ---- brightness units ----

    // K&S work in nanoLamberts. B[nL] = 34.08 exp(20.7233 - 0.92104 V).
    private static double ToNanoLambert(double magPerArcsec2) => 34.08 * Math.Exp(20.7233 - 0.92104 * magPerArcsec2);
    private static double ToMag(double nanoLambert) => (20.7233 - Math.Log(nanoLambert / 34.08)) / 0.92104;

    /// <summary>V-band extinction coefficient, mag per airmass, for an average site.</summary>
    public const double Extinction = 0.172;

    public static double Airmass(double altDeg) {
        double z = (90 - Math.Clamp(altDeg, 0, 90)) * Math.PI / 180;
        double s = Math.Sin(z);
        return 1.0 / Math.Sqrt(1 - 0.96 * s * s);
    }

    /// <summary>
    /// Sky brightness towards a target at <paramref name="targetAltDeg"/>.
    /// The site's zenith brightness grows towards the horizon with airmass,
    /// and the Moon (when above the horizon) adds its scattered light.
    /// </summary>
    /// <param name="zenithSqm">The site's dark-sky zenith brightness.</param>
    /// <param name="moonAltDeg">Moon altitude; at or below 0 it adds nothing.</param>
    /// <param name="moonPhaseAngleDeg">0 = full, 180 = new.</param>
    /// <param name="moonSeparationDeg">Moon to target, degrees.</param>
    public static double AtTarget(double zenithSqm, double targetAltDeg,
                                  double moonAltDeg, double moonPhaseAngleDeg, double moonSeparationDeg) {
        double x = Airmass(targetAltDeg);
        double dark = ToNanoLambert(zenithSqm) * Math.Pow(10, -0.4 * Extinction * (x - 1)) * x;
        double moon = 0;
        if (moonAltDeg > 0) {
            double a = Math.Abs(moonPhaseAngleDeg);
            double iStar = Math.Pow(10, -0.4 * (3.84 + 0.026 * a + 4e-9 * Math.Pow(a, 4)));
            // Scattering below ~10° from the Moon diverges in the fit; hold it there.
            double rho = Math.Max(moonSeparationDeg, 10);
            double c = Math.Cos(rho * Math.PI / 180);
            double f = Math.Pow(10, 5.36) * (1.06 + c * c) + Math.Pow(10, 6.15 - rho / 40);
            double xm = Airmass(moonAltDeg);
            moon = f * iStar * Math.Pow(10, -0.4 * Extinction * xm) * (1 - Math.Pow(10, -0.4 * Extinction * x));
        }
        return ToMag(dark + moon);
    }

    /// <summary>
    /// Mean surface brightness, mag/arcsec², of an object of total magnitude
    /// <paramref name="mag"/> spread over an ellipse of the given axes
    /// (arcminutes). Null when either is unknown.
    /// </summary>
    public static double? SurfaceBrightness(double? mag, double? majorArcmin, double? minorArcmin) {
        if (mag is not { } m || m >= 90 || majorArcmin is not { } a || a <= 0) return null;
        double b = minorArcmin is { } mb && mb > 0 ? mb : a;
        double areaArcsec2 = Math.PI / 4 * a * b * 3600;
        return m + 2.5 * Math.Log10(areaArcsec2);
    }

    /// <summary>
    /// Mean axis ratio assumed for a galaxy whose catalogue entry carries
    /// only the major axis: spread over a circle, an edge-on galaxy reads
    /// a magnitude fainter per arcsec² than it is.
    /// </summary>
    public const double GalaxyAxisRatio = 0.6;

    /// <summary>Surface brightness assumed for a nebula catalogued with a size
    /// and no magnitude (Sh2, LBN, most reflection nebulae such as the Witch
    /// Head): these are the faint, extended ones, a magnitude or two below a
    /// dark sky.</summary>
    public const double FaintNebulaSb = 24.0;

    // ---- what light pollution does to each kind of object ----

    /// <summary>How the sky affects an object type.</summary>
    public enum Kind { Galaxy, Emission, Planetary, Reflection, Dark, Cluster, Other }

    public static Kind KindOf(string? type) {
        var t = type ?? "";
        bool Has(string s) => t.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("Galax")) return Kind.Galaxy;
        if (Has("Planetary")) return Kind.Planetary;
        if (Has("Reflection")) return Kind.Reflection;
        if (Has("Dark")) return Kind.Dark;
        if (Has("Nebula") || Has("HII") || Has("Remnant")) return Kind.Emission;
        if (Has("Cluster")) return Kind.Cluster;
        return Kind.Other;
    }

    /// <summary>
    /// The share of the surface-brightness deficit that counts against an
    /// object. Galaxies and reflection or dark nebulae take all of it: their
    /// light is broadband, like the light pollution. An emission or planetary
    /// nebula behind a narrowband filter takes a third, since the filter cuts
    /// most of the sky and keeps the lines. Star clusters are resolved stars,
    /// which a bright sky hurts far less than diffuse light.
    /// </summary>
    public static double Sensitivity(Kind kind, bool narrowband) => kind switch {
        Kind.Galaxy or Kind.Reflection or Kind.Dark => 1.0,
        Kind.Emission or Kind.Planetary => narrowband ? 0.3 : 1.0,
        Kind.Cluster => 0.4,
        _ => 0.0,
    };

    // ---- ratings ----
    //
    // Calibrated against two sites (Daniel, November 2026):
    //  - Florânia, Bortle 4 (SQM ~21.1): the faint Orion nebulae (the
    //    Horsehead and IC 434, the Flame, Barnard's Loop), the Ghost of
    //    Cassiopeia, M78 with its dust and M31 all show in minutes and are
    //    finished in three to four hours; only targets low in the sky are
    //    harder. Everything high up there is "great".
    //  - Mossoró, Bortle 8 to 9 (SQM 17.5 to 18.1): none of those faint
    //    nebulae can be done, while M31, M33, M42, M45 and the Carina Nebula
    //    can: "good", and nothing is "great" under a sky that bright.

    /// <summary>Up to this deficit an object comes out easily: no penalty,
    /// and "great" when the sky is dark enough (<see cref="GreatSkyFloor"/>).
    /// A faint nebula at Florânia's zenith sits 1.9 below the sky.</summary>
    public const double DeficitAllowance = 2.0;

    /// <summary>Up to this deficit, "good": M31 and M33 from Bortle 9 sit
    /// at about 3.7, M42 at 3.9.</summary>
    public const double GoodDeficit = 4.0;

    /// <summary>Up to this deficit "possible", beyond it "hard". A faint
    /// nebula from Bortle 8 sits at 4.9.</summary>
    public const double HardDeficit = 4.6;

    /// <summary>A sky brighter than this towards the target (about Bortle
    /// 5) rates nothing better than "good": the faint outskirts that make a
    /// great picture are gone, however bright the object's core.</summary>
    public const double GreatSkyFloor = 20.0;

    /// <summary>Score points per magnitude of deficit beyond the allowance,
    /// and the most magnitudes that count. 8 points is one magnitude of
    /// total brightness in the base score, so the 10 here weighs a magnitude
    /// of sky deficit a little above a magnitude of brightness.</summary>
    public const double PointsPerMag = 10, MaxDeficitMag = 4;

    /// <summary>
    /// How much brighter than its catalogue mean the part of an object that
    /// makes the picture is. The mean spreads the light over the full
    /// catalogued extent, which is the faint outskirts. A galaxy's light is
    /// concentrated in its bulge and inner disc, ~2 mag/arcsec² above the
    /// mean. A bright emission nebula (<paramref name="brightNebula"/>: one
    /// whose measured magnitude puts its mean above <see cref="FaintNebulaSb"/>,
    /// such as M42, the Carina Nebula or the Lagoon) has a brilliant central
    /// region, ~1.5; the Carina Nebula is easy from a Bortle 9 city even
    /// low in the sky. A faint or unmeasured nebula is more even, ~1. A
    /// cluster's stars sit in between.
    /// </summary>
    public static double CoreGain(Kind kind, bool brightNebula = false) => kind switch {
        Kind.Galaxy => 2.0,
        Kind.Emission or Kind.Planetary or Kind.Reflection or Kind.Dark => brightNebula ? 1.5 : 1.0,
        _ => 1.5,
    };

    /// <summary>Score penalty and the rating for one object against the sky,
    /// from its catalogue mean surface brightness. Ratings: "great",
    /// "good", "fair" (possible) and "hard".</summary>
    public static (int Penalty, string Rating, double Deficit) Rate(double objectSb, double skySb, Kind kind, bool narrowband,
                                                                    bool brightNebula = false) {
        double deficit = (objectSb - CoreGain(kind, brightNebula) - skySb) * Sensitivity(kind, narrowband);
        double counted = Math.Clamp(deficit - DeficitAllowance, 0, MaxDeficitMag);
        int penalty = (int)Math.Round(counted * PointsPerMag);
        string rating = deficit <= DeficitAllowance && skySb >= GreatSkyFloor ? "great"
                      : deficit <= GoodDeficit ? "good"
                      : deficit <= HardDeficit ? "fair"
                      : "hard";
        return (penalty, rating, deficit);
    }

    // ---- narrowband filters ----

    private static readonly Regex NarrowbandName = new(
        @"(?ix)
          (^|[^a-z])(h-?a|h-?alpha|ha\d|oiii|o-?iii|o3|sii|s-?ii|s2|h-?b|h-?beta|nii|n-?ii)([^a-z]|$)
        | l-?extreme | l-?ultimate | l-?synergy | l-?enhance
        | sv220 | alp-?t | dual | tri-?band | quad-?band | antlia-(quad|edge)
        | ^(d1|d2|c1)$",
        RegexOptions.Compiled);

    /// <summary>True when a filter name or code is a narrowband or multi-band
    /// line filter (Ha, OIII, SII, dual/tri/quad-band, the Optolong L-series
    /// line filters, SV220, ALP-T, Askar D1/D2/C1). Antlia's "Triband RGB"
    /// is a colour filter set, not a line filter.</summary>
    public static bool IsNarrowbandFilter(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.Contains("rgb", StringComparison.OrdinalIgnoreCase)
        && NarrowbandName.IsMatch(name.Trim());
}
