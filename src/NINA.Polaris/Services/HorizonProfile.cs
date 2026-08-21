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

using System.Globalization;

namespace NINA.Polaris.Services;

/// <summary>
/// Parses and evaluates a custom horizon: an azimuth→minimum-visible-altitude
/// mask for a site (trees, buildings). Understands the common community text
/// format (N.I.N.A. <c>.hrz</c> / Stellarium / TouchNStars): one
/// <c>azimuth altitude</c> pair per line, whitespace- or comma-separated, with
/// <c>#</c>/<c>;</c> comments and blank lines ignored. Azimuth is 0..360°
/// (0 = North, 90 = East); the horizon between samples is linearly
/// interpolated, wrapping across 360°→0°.
/// </summary>
public static class HorizonProfile {
    /// <summary>Parse horizon text into sorted points. Tolerant: skips comments,
    /// blank lines and malformed rows. Returns an empty list when nothing
    /// usable is found.</summary>
    public static List<HorizonPoint> Parse(string? text) {
        var pts = new List<HorizonPoint>();
        if (string.IsNullOrWhiteSpace(text)) return pts;
        foreach (var raw in text.Split('\n')) {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
            // Drop an inline comment.
            var hash = line.IndexOfAny(new[] { '#', ';' });
            if (hash >= 0) line = line[..hash].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(new[] { ' ', '\t', ',', ';' },
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var az)) continue;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var alt)) continue;
            az = ((az % 360) + 360) % 360;             // wrap into 0..360
            alt = Math.Clamp(alt, -90, 90);
            pts.Add(new HorizonPoint { Azimuth = az, Altitude = alt });
        }
        return Normalize(pts);
    }

    /// <summary>Sort by azimuth and drop duplicate azimuths (keep the last),
    /// so the interpolation below is monotonic.</summary>
    public static List<HorizonPoint> Normalize(IEnumerable<HorizonPoint> points) {
        var map = new SortedDictionary<double, double>();
        foreach (var p in points) {
            var az = ((p.Azimuth % 360) + 360) % 360;
            map[az] = Math.Clamp(p.Altitude, -90, 90);
        }
        return map.Select(kv => new HorizonPoint { Azimuth = kv.Key, Altitude = kv.Value }).ToList();
    }

    /// <summary>Minimum visible altitude (deg) at <paramref name="azimuthDeg"/>,
    /// linearly interpolated between the two bracketing points and wrapping
    /// across 360°. Returns 0 (the natural flat horizon) when there are no
    /// points, so callers can use it unconditionally.</summary>
    public static double AltitudeAt(IReadOnlyList<HorizonPoint> points, double azimuthDeg) {
        if (points == null || points.Count == 0) return 0.0;
        if (points.Count == 1) return points[0].Altitude;
        var az = ((azimuthDeg % 360) + 360) % 360;
        // points are sorted by azimuth (see Normalize). Find the bracket.
        HorizonPoint lo = points[^1], hi = points[0];
        for (int i = 0; i < points.Count; i++) {
            if (points[i].Azimuth <= az) { lo = points[i]; }
            if (points[i].Azimuth >= az) { hi = points[i]; break; }
        }
        // Azimuth span from lo to hi, going forward (may wrap past 360).
        double loAz = lo.Azimuth, hiAz = hi.Azimuth;
        double span = hiAz - loAz;
        if (span <= 0) span += 360;            // wrap segment (last → first)
        if (span <= 1e-9) return hi.Altitude;
        double pos = az - loAz;
        if (pos < 0) pos += 360;
        double f = Math.Clamp(pos / span, 0, 1);
        return lo.Altitude + f * (hi.Altitude - lo.Altitude);
    }

    /// <summary>True when a point at (<paramref name="altitudeDeg"/>,
    /// <paramref name="azimuthDeg"/>) is BELOW the custom horizon (blocked).
    /// Always false when no horizon is defined.</summary>
    public static bool IsBlocked(IReadOnlyList<HorizonPoint> points, double altitudeDeg, double azimuthDeg) {
        if (points == null || points.Count == 0) return false;
        return altitudeDeg < AltitudeAt(points, azimuthDeg);
    }
}
