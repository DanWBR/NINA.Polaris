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

namespace NINA.Polaris.Services.PlateSolving;

/// <summary>
/// Picks the star database / index files a rig's field of view needs.
///
/// <para>The ranges are the publishers' own, kept as data in
/// <c>wwwroot/data/platesolve-databases.json</c>: ASTAP's D-series is graded by
/// star density and each grade has a smallest usable field, and astrometry.net
/// indexes are chosen by skymark size, where a useful index has skymarks
/// between 10% and 100% of the image field.</para>
///
/// <para>Pure decision logic, no I/O, so the recommendation can be tested
/// against the published tables rather than against a live install.</para>
/// </summary>
public static class SolverDatabaseAdvisor {

    public sealed record AstapDatabase(string Id, string Name, double MinFovDeg, double MaxFovDeg,
                                       long ApproxBytes, string? Url, string? Notes);

    public sealed record AstrometryScale(int Scale, double MinArcmin, double MaxArcmin);

    /// <summary>
    /// The ASTAP database to use for a field, from a catalogue ordered widest
    /// to narrowest. Among the databases that cover the field, the SMALLEST
    /// download wins: every D-grade solves the fields it covers, so a denser
    /// one only costs disk and RAM. Null when the field is outside every range.
    /// </summary>
    public static AstapDatabase? RecommendAstap(IReadOnlyList<AstapDatabase> catalogue, double fovDeg) {
        if (catalogue == null || catalogue.Count == 0 || !(fovDeg > 0)) return null;
        AstapDatabase? best = null;
        foreach (var db in catalogue) {
            if (fovDeg < db.MinFovDeg || fovDeg > db.MaxFovDeg) continue;
            if (best == null || db.ApproxBytes < best.ApproxBytes) best = db;
        }
        return best;
    }

    /// <summary>
    /// The astrometry.net skymark bands worth downloading for a field: those
    /// whose range overlaps 10% to 100% of it, which is the project's own rule
    /// of thumb. Returned ascending; an empty list means the field is off the
    /// end of the published table.
    /// </summary>
    public static IReadOnlyList<AstrometryScale> RecommendAstrometryScales(
            IReadOnlyList<AstrometryScale> table, double fovDeg) {
        if (table == null || !(fovDeg > 0)) return Array.Empty<AstrometryScale>();
        double fieldArcmin = fovDeg * 60.0;
        double lo = 0.10 * fieldArcmin, hi = fieldArcmin;
        var hits = table.Where(s => s.MaxArcmin >= lo && s.MinArcmin <= hi)
                        .OrderBy(s => s.Scale)
                        .ToList();
        // A field narrower than the smallest published band still needs the
        // smallest band rather than nothing: without it the solver has no
        // index at all, which is worse than one that is slightly too coarse.
        if (hits.Count == 0 && table.Count > 0) {
            hits.Add(fieldArcmin < table.Min(s => s.MinArcmin)
                ? table.OrderBy(s => s.Scale).First()
                : table.OrderBy(s => s.Scale).Last());
        }
        return hits;
    }

    /// <summary>Field of view (degrees) along the LONG axis from the pixel
    /// scale and sensor size. The long axis is what both publishers' tables are
    /// quoted against, and it is the number an operator recognises.</summary>
    public static double FovDegrees(double pixelScaleArcsecPerPixel, int widthPx, int heightPx) {
        var longest = Math.Max(widthPx, heightPx);
        if (!(pixelScaleArcsecPerPixel > 0) || longest <= 0) return 0;
        return longest * pixelScaleArcsecPerPixel / 3600.0;
    }

    /// <summary>Pixel scale in arcsec/px from the optics, the classic
    /// 206.265 * pixel size (um) / focal length (mm).</summary>
    public static double PixelScale(double pixelSizeUm, double focalLengthMm)
        => (pixelSizeUm > 0 && focalLengthMm > 0)
            ? 206.265 * pixelSizeUm / focalLengthMm
            : 0;
}
