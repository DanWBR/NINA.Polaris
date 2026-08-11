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

using CosineKitty;

namespace NINA.Polaris.Services;

/// <summary>
/// Ranks celestial objects worth observing tonight from the observer's
/// location. Sources:
///   - DSOs: SkyCatalogService (≈200 Messier/Caldwell/NGC objects)
///   - Moon: AstronomyEngine
///   - Planets: AstronomyEngine (Mercury through Neptune; Pluto excluded
///     since it's effectively never bright enough for the use case)
///   - Comets: postponed to TB-4 follow-up (need orbital-element propagator)
///
/// "Tonight" = window from astronomical dusk (sun -18°) to astronomical
/// dawn at the observer's longitude, or a ±6 h fallback if the sun
/// never sets that far (high latitude summer).
///
/// Scoring is a simple composite:
///   - DSO base: (6 - clamp(mag, -2, 12)) so brighter rises
///   - Planet base: same formula but the planet's apparent magnitude
///   - Moon always present, base 50 + illumination bonus
///   - + (peakAlt / 90) * 20: rewards transit altitude
///   - Filter: only keep things with peak altitude ≥ 30° during the
///     night window (10° for the Moon, it's always interesting),
///     and dimmer than mag 10 cuts.
///
/// The "FitsCameraFov" flag is computed when a camera is connected and
/// the active rig has a focal length, comparing the object's major /
/// minor axes against the camera FOV in arcminutes.
/// </summary>
public class TonightsBestService {
    private readonly SkyCatalogService    _catalog;
    private readonly AltitudeService      _altitude;
    private readonly EquipmentManager     _equip;
    private readonly ProfileService       _profile;
    private readonly CometEphemerisService? _comets;
    private readonly ILogger<TonightsBestService> _logger;

    public TonightsBestService(
            SkyCatalogService catalog,
            AltitudeService altitude,
            EquipmentManager equip,
            ProfileService profile,
            ILogger<TonightsBestService> logger,
            CometEphemerisService? comets = null) {
        _catalog  = catalog;
        _altitude = altitude;
        _equip    = equip;
        _profile  = profile;
        _comets   = comets;
        _logger   = logger;
    }

    public TonightsBestResult Compute(int limit = 30) {
        var lat = _profile.Active.Latitude;
        var lng = _profile.Active.Longitude;
        var nowUtc = DateTime.UtcNow;

        // Night window: astro dusk → astro dawn. Fallback to ±6 h if the
        // sun never reaches -18° (polar summer).
        var night = _altitude.ComputeNightWindow(nowUtc);
        var nightStart = night.AstronomicalDuskUtc;
        var nightEnd   = night.AstronomicalDawnUtc;
        if (nightEnd <= nightStart) {
            nightStart = nowUtc.AddHours(-6);
            nightEnd   = nowUtc.AddHours( 6);
        }

        var fov = ComputeCameraFov();
        var items = new List<TonightCandidate>();

        // --- DSOs ---
        foreach (var dso in _catalog.AllPlanningObjects) {
            // FromDso maps a NULL catalog magnitude to the 99.0 sentinel, so a
            // value < 90 means we really have a magnitude. Big emission/bright
            // nebulae (Sh2, LBN) have no magnitude but a known size; the pool
            // includes those (size ≥ 10′) so they can be ranked by SIZE instead.
            bool hasMag = dso.Magnitude < 90;
            double sizeArcmin = dso.SizeArcmin ?? 0;
            if (hasMag) {
                // Coarse brightness gate before the (expensive) altitude track.
                if (dso.Magnitude > 10) continue;
            } else {
                // Magnitude-less: keep only the big, imageable nebulae.
                if (sizeArcmin < 10) continue;
            }
            var (peakAlt, peakUtc) = PeakAltitude(dso.Ra, dso.Dec, nightStart, nightEnd, stepMinutes: 30);
            if (peakAlt < 30) continue;
            var (curAlt, curAz) = AltitudeService.RaDecToAltAz(dso.Ra, dso.Dec, nowUtc, lat, lng);
            // Magnitude-scored when we have one; otherwise a size-based score so
            // a large nebula ranks comparably to a mid-brightness object. log2
            // keeps the curve gentle (10′→~20, 30′→~26, 100′→~32) and the clamp
            // stops a giant complex from dominating the whole list.
            var score = hasMag
                ? (int)Math.Round((6 - Math.Clamp(dso.Magnitude, -2, 12)) * 8 + peakAlt / 90.0 * 20)
                : (int)Math.Round(Math.Clamp(6.0 * Math.Log2(Math.Max(sizeArcmin, 5.0)), 10, 42)
                                  + peakAlt / 90.0 * 20);
            items.Add(new TonightCandidate(
                Category:        "Dso",
                Name:            dso.Name,
                CommonName:      dso.CommonName,
                Type:            dso.Type,
                RaHours:         dso.Ra,
                DecDeg:          dso.Dec,
                Magnitude:       hasMag ? dso.Magnitude : (double?)null,
                Size:            sizeArcmin > 0 ? $"{sizeArcmin:0.#}'" : null,
                SizeMajorArcmin: sizeArcmin > 0 ? sizeArcmin : (double?)null,
                SizeMinorArcmin: null,
                CurrentAltDeg:   Math.Round(curAlt, 1),
                CurrentAzDeg:    Math.Round(curAz,  1),
                PeakAltDeg:      Math.Round(peakAlt, 1),
                PeakUtc:         peakUtc,
                Score:           score,
                FitsCameraFov:   null,                  // catalog has no size for now
                CameraFovWidthArcmin:  fov?.WidthArcmin,
                CameraFovHeightArcmin: fov?.HeightArcmin
            ));
        }

        // Cross-catalogue de-dup: the same physical object is catalogued
        // under several designations (M31 = NGC 224, M33 = NGC 598, …),
        // so it was listed multiple times (field report). Collapse DSOs
        // that share a sky position to one entry, keeping the most
        // familiar designation (Messier > Caldwell > NGC > IC > rest).
        items = DedupDsoByPosition(items);

        // --- Solar-system bodies via AstronomyEngine ---
        var observer = new Observer(lat, lng, _profile.Active.Altitude);
        var time = new AstroTime(nowUtc);

        // Moon, always include if above horizon at peak.
        AddSolarSystem("Moon", Body.Moon, "Moon", observer, time, nightStart, nightEnd, lat, lng, fov, items,
            minPeakAlt: 10, baseBoost: 50);

        // Planets
        foreach (var (label, body) in PlanetSet()) {
            AddSolarSystem(label, body, "Planet", observer, time, nightStart, nightEnd, lat, lng, fov, items,
                minPeakAlt: 15, baseBoost: 0);
        }

        // The Lynds catalogues (LBN bright + LDN dark nebulae) have thousands of
        // magnitude-less, size-ranked entries that would otherwise crowd out
        // everything else. Cap the POOL to the 10 highest-scored of each BEFORE
        // the global cutoff so the freed slots go to other objects.
        items = CapCatalogue(items, "LBN", 10);
        items = CapCatalogue(items, "LDN", 10);

        // Cap DSOs + planets + Moon by score first…
        var byScore = items.OrderByDescending(i => i.Score).ToList();
        var ordered = byScore.Take(limit).ToList();

        // …but a single brightness-weighted global cap STARVES whole types:
        // galaxies are intrinsically fainter than stars/open clusters, so they
        // score low and get buried — the "only Andromeda under Galaxies" report.
        // Guarantee each DSO sub-type's best few are present regardless of the
        // global cutoff (same spirit as the comet append below).
        var seen = new HashSet<string>(ordered.Select(i => i.Category + "/" + i.Name));
        static bool TypeHas(TonightCandidate c, string needle) =>
            c.Type != null && c.Type.Contains(needle, StringComparison.OrdinalIgnoreCase);
        bool IsGalaxy(TonightCandidate c)  => c.Category == "Dso" && TypeHas(c, "Galaxy");
        bool IsNebula(TonightCandidate c)  => c.Category == "Dso" && (TypeHas(c, "Nebula") || TypeHas(c, "HII"));
        bool IsCluster(TonightCandidate c) => c.Category == "Dso" && TypeHas(c, "Cluster") && !TypeHas(c, "Galaxy");
        void TopUp(Func<TonightCandidate, bool> match, int k) {
            int have = ordered.Count(match);
            foreach (var c in byScore) {
                if (have >= k) break;
                if (!match(c)) continue;
                if (seen.Add(c.Category + "/" + c.Name)) { ordered.Add(c); have++; }
            }
        }
        TopUp(IsGalaxy, 25);
        TopUp(IsNebula, 25);
        TopUp(IsCluster, 25);
        // Keep the merged list score-ordered for the default "All" view.
        ordered = ordered.OrderByDescending(i => i.Score).ToList();

        // …then append comets unconditionally. They share their own
        // category-filter chip in the UI, so cutting them by the global
        // limit (DSOs dominate the top of the list and would always
        // win the slot fight) would mean the "Comets" tab silently empty.
        // Curated list is small (~10), so the append cost is trivial.
        var cometItems = new List<TonightCandidate>();
        if (_comets != null) {
            foreach (var comet in _comets.AllComets) {
                AddComet(comet, observer, nightStart, nightEnd, lat, lng, fov, cometItems);
            }
        }
        ordered.AddRange(cometItems.OrderByDescending(i => i.Score));
        return new TonightsBestResult(
            ComputedAtUtc:     nowUtc,
            NightStartUtc:     nightStart,
            NightEndUtc:       nightEnd,
            ObserverLat:       lat,
            ObserverLon:       lng,
            CameraFovWidthArcmin:  fov?.WidthArcmin,
            CameraFovHeightArcmin: fov?.HeightArcmin,
            Items:             ordered);
    }

    /// <summary>
    /// Keep only the <paramref name="max"/> highest-scored candidates whose name
    /// belongs to the given catalogue prefix (e.g. "LBN", "LDN"), dropping the
    /// rest. Non-matching candidates pass through untouched. The prefix must be
    /// followed by a non-letter (space/digit) so "LBN 552" matches but a
    /// hypothetical "LBNxyz" does not.
    /// </summary>
    private static List<TonightCandidate> CapCatalogue(
            List<TonightCandidate> items, string prefix, int max) {
        bool IsMatch(TonightCandidate c) =>
            c.Category == "Dso" && c.Name != null
            && c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && (c.Name.Length == prefix.Length || !char.IsLetter(c.Name[prefix.Length]));
        var keep = items.Where(IsMatch)
                        .OrderByDescending(c => c.Score)
                        .Take(max)
                        .Select(c => c.Name)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return items.Where(c => !IsMatch(c) || keep.Contains(c.Name!)).ToList();
    }

    /// <summary>
    /// Collapse DSO candidates that refer to the same physical object
    /// (same sky position under different catalogue designations) to a
    /// single entry. Non-DSO candidates pass through untouched. Among a
    /// group the most familiar designation wins — Messier, then Caldwell,
    /// then NGC, then IC, then everything else; ties break on higher score.
    /// </summary>
    /// <summary>How far apart two rows may be and still be one object. Wide
    /// enough for catalogues that disagree on a galaxy's centre, far below the
    /// separation of any two objects worth listing separately.</summary>
    internal const double DedupToleranceArcsec = 30.0;

    /// <summary>
    /// One entry per physical object, under its most familiar name.
    ///
    /// <para>Two things were wrong. The rank was parsed out of the DISPLAY NAME
    /// and only knew four catalogues, so everything else tied at 10 and the
    /// winner fell out of input order. And the position match bucketed
    /// coordinates at two arcseconds, which puts two rows for the same object
    /// in different buckets whenever they straddle a boundary or disagree by
    /// more than the bucket: Arp 168 and M32 are 3 arcsec apart, so Andromeda's
    /// companion was listed twice, once under a name nobody uses.</para>
    ///
    /// <para>Now it compares real angular separation and uses the shared
    /// catalogue rank. Position alone still decides, with no alias or type
    /// check: this is a list of places to point the telescope for a night, and
    /// two catalogue rows at one position are one place. That is deliberately
    /// looser than the annotation overlay, which merges only on an alias link
    /// (see <see cref="Sky.AnnotationSynonyms"/>) because hiding an object from
    /// a picture of the sky is worse than a crowded label.</para>
    /// </summary>
    internal static List<TonightCandidate> DedupDsoByPosition(List<TonightCandidate> items) {
        var others = new List<TonightCandidate>();
        var dsos = new List<TonightCandidate>();
        foreach (var c in items) {
            if (c.Category == "Dso") dsos.Add(c); else others.Add(c);
        }

        // Best first, so the survivor of each group is the one already at the
        // front and every later row is compared against a decided winner.
        var ranked = dsos
            .OrderBy(c => NINA.Polaris.Services.Sky.DesignationRank.OfName(c.Name))
            .ThenByDescending(c => c.Score)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        // Declination bands one tolerance wide, so each row is compared against
        // the handful of neighbours that could possibly be it rather than
        // against everything kept so far. The pool here is thousands of rows.
        const double band = DedupToleranceArcsec / 3600.0;
        var byBand = new Dictionary<long, List<TonightCandidate>>();
        var kept = new List<TonightCandidate>();
        foreach (var c in ranked) {
            long b = (long)Math.Floor(c.DecDeg / band);
            bool duplicate = false;
            for (long k = b - 1; k <= b + 1 && !duplicate; k++) {
                if (!byBand.TryGetValue(k, out var near)) continue;
                duplicate = near.Any(o => SeparationArcsec(o, c) <= DedupToleranceArcsec);
            }
            if (duplicate) continue;
            kept.Add(c);
            if (!byBand.TryGetValue(b, out var list)) byBand[b] = list = new List<TonightCandidate>();
            list.Add(c);
        }

        // Order is irrelevant here, the caller re-sorts by score.
        var result = new List<TonightCandidate>(others);
        result.AddRange(kept);
        return result;
    }

    private static double SeparationArcsec(TonightCandidate a, TonightCandidate b) {
        var meanDec = (a.DecDeg + b.DecDeg) / 2.0 * Math.PI / 180.0;
        var dRa = (a.RaHours - b.RaHours) * 15.0 * Math.Cos(meanDec);
        var dDec = a.DecDeg - b.DecDeg;
        return Math.Sqrt(dRa * dRa + dDec * dDec) * 3600.0;
    }

    private void AddSolarSystem(string name, Body body, string category,
                                Observer observer, AstroTime time,
                                DateTime nightStart, DateTime nightEnd,
                                double lat, double lng,
                                CameraFov? fov,
                                List<TonightCandidate> items,
                                double minPeakAlt, int baseBoost) {
        try {
            // Apparent equatorial coords (RA in hours, Dec in deg). Use
            // EquatorEpoch.OfDate so the position aligns with the
            // observation epoch rather than J2000.
            var eq = Astronomy.Equator(body, time, observer, EquatorEpoch.OfDate, Aberration.Corrected);
            var ra  = eq.ra;
            var dec = eq.dec;

            var (peakAlt, peakUtc) = PeakAltitudeBody(body, observer, nightStart, nightEnd, stepMinutes: 30);
            if (peakAlt < minPeakAlt) return;
            var (curAlt, curAz) = AltitudeService.RaDecToAltAz(ra, dec, DateTime.UtcNow, lat, lng);

            double? mag = null;
            try {
                var illum = Astronomy.Illumination(body, time);
                mag = illum.mag;
            } catch { /* Some bodies (Sun-only) don't compute; safe to skip */ }

            double scoreBase = mag.HasValue
                ? (4 - Math.Clamp(mag.Value, -13, 12)) * 6
                : 30;
            var score = (int)Math.Round(baseBoost + scoreBase + peakAlt / 90.0 * 20);

            items.Add(new TonightCandidate(
                Category:        category,
                Name:            name,
                CommonName:      null,
                Type:            category,
                RaHours:         ra,
                DecDeg:          dec,
                Magnitude:       mag.HasValue ? Math.Round(mag.Value, 2) : null,
                Size:            null,
                SizeMajorArcmin: null,
                SizeMinorArcmin: null,
                CurrentAltDeg:   Math.Round(curAlt, 1),
                CurrentAzDeg:    Math.Round(curAz,  1),
                PeakAltDeg:      Math.Round(peakAlt, 1),
                PeakUtc:         peakUtc,
                Score:           score,
                FitsCameraFov:   null,
                CameraFovWidthArcmin:  fov?.WidthArcmin,
                CameraFovHeightArcmin: fov?.HeightArcmin
            ));
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Skipping body {Name} (AstronomyEngine error)", name);
        }
    }

    private void AddComet(CometElements c, Observer observer,
                          DateTime nightStart, DateTime nightEnd,
                          double lat, double lng,
                          CameraFov? fov, List<TonightCandidate> items) {
        try {
            // Sample position at peak-search points (every 30 min through
            // the night). We track the best altitude AND remember the
            // closest-to-now position for the "current" RA/Dec.
            var nowUtc = DateTime.UtcNow;
            var nowPos = _comets!.Compute(c, nowUtc);
            // Don't gate on magnitude, every curated periodic comet is
            // worth knowing about, and the score formula naturally pushes
            // dim ones (mag 15+) to the bottom of the list. Users who
            // care about brightness can read the magnitude on the card.

            double peakAlt = -90;
            DateTime peakUtc = nightStart;
            for (var t = nightStart; t <= nightEnd; t = t.AddMinutes(30)) {
                var p = _comets.Compute(c, t);
                var (alt, _) = AltitudeService.RaDecToAltAz(p.RaHours, p.DecDeg, t, lat, lng);
                if (alt > peakAlt) { peakAlt = alt; peakUtc = t; }
            }
            if (peakAlt < 15) return;

            var (curAlt, curAz) = AltitudeService.RaDecToAltAz(
                nowPos.RaHours, nowPos.DecDeg, nowUtc, lat, lng);

            // Comets get a small boost on score so an actually-bright apparition
            // outranks a mediocre DSO with the same magnitude, they're event-
            // worthy targets the user probably wants to plan around.
            var score = (int)Math.Round(
                (8 - Math.Clamp(nowPos.EstimatedMagnitude, -5, 13)) * 7
                + peakAlt / 90.0 * 20
                + 5);

            items.Add(new TonightCandidate(
                Category:        "Comet",
                Name:            c.Name,
                CommonName:      null,
                Type:            "Periodic comet",
                RaHours:         nowPos.RaHours,
                DecDeg:          nowPos.DecDeg,
                Magnitude:       Math.Round(nowPos.EstimatedMagnitude, 2),
                Size:            null,
                SizeMajorArcmin: null,
                SizeMinorArcmin: null,
                CurrentAltDeg:   Math.Round(curAlt, 1),
                CurrentAzDeg:    Math.Round(curAz,  1),
                PeakAltDeg:      Math.Round(peakAlt, 1),
                PeakUtc:         peakUtc,
                Score:           score,
                FitsCameraFov:   null,
                CameraFovWidthArcmin:  fov?.WidthArcmin,
                CameraFovHeightArcmin: fov?.HeightArcmin
            ));
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Skipping comet {Name}", c.Name);
        }
    }

    private static IEnumerable<(string Name, Body Body)> PlanetSet() {
        yield return ("Mercury", Body.Mercury);
        yield return ("Venus",   Body.Venus);
        yield return ("Mars",    Body.Mars);
        yield return ("Jupiter", Body.Jupiter);
        yield return ("Saturn",  Body.Saturn);
        yield return ("Uranus",  Body.Uranus);
        yield return ("Neptune", Body.Neptune);
    }

    private (double peakAlt, DateTime peakUtc) PeakAltitude(double ra, double dec,
                                                            DateTime from, DateTime to,
                                                            int stepMinutes) {
        var track = _altitude.ComputeTrack(ra, dec, from, to, stepMinutes);
        if (track.Count == 0) return (-90, from);
        var best = track.OrderByDescending(s => s.AltitudeDeg).First();
        return (best.AltitudeDeg, best.Utc);
    }

    private (double peakAlt, DateTime peakUtc) PeakAltitudeBody(Body body, Observer observer,
                                                                DateTime from, DateTime to,
                                                                int stepMinutes) {
        double peak = -90;
        DateTime peakAt = from;
        for (var t = from; t <= to; t = t.AddMinutes(stepMinutes)) {
            var time = new AstroTime(t);
            var eq = Astronomy.Equator(body, time, observer, EquatorEpoch.OfDate, Aberration.Corrected);
            var horiz = Astronomy.Horizon(time, observer, eq.ra, eq.dec, Refraction.Normal);
            if (horiz.altitude > peak) { peak = horiz.altitude; peakAt = t; }
        }
        return (peak, peakAt);
    }

    private CameraFov? ComputeCameraFov() {
        var cam = _equip.Camera;
        if (cam == null || !cam.IsConnected) return null;
        double pixX, pixY; int mx, my;
        try {
            pixX = cam.PixelSizeX;
            pixY = cam.PixelSizeY;
            mx   = cam.MaxX;
            my   = cam.MaxY;
        } catch { return null; }
        if (pixX <= 0 || pixY <= 0 || mx <= 0 || my <= 0) return null;

        var sensorWmm = mx * pixX / 1000.0;
        var sensorHmm = my * pixY / 1000.0;
        var focalMm   = _profile.Active.FocalLengthMm;
        if (focalMm <= 0) return null;

        var fovWdeg = 2 * Math.Atan(sensorWmm / (2 * focalMm)) * (180.0 / Math.PI);
        var fovHdeg = 2 * Math.Atan(sensorHmm / (2 * focalMm)) * (180.0 / Math.PI);
        return new CameraFov(
            WidthArcmin:  Math.Round(fovWdeg  * 60, 1),
            HeightArcmin: Math.Round(fovHdeg  * 60, 1));
    }

    private record CameraFov(double WidthArcmin, double HeightArcmin);
}

// ---------- DTOs ----------

public record TonightsBestResult(
    DateTime ComputedAtUtc,
    DateTime NightStartUtc,
    DateTime NightEndUtc,
    double ObserverLat,
    double ObserverLon,
    double? CameraFovWidthArcmin,
    double? CameraFovHeightArcmin,
    IReadOnlyList<TonightCandidate> Items);

public record TonightCandidate(
    string Category,
    string Name,
    string? CommonName,
    string? Type,
    double RaHours,
    double DecDeg,
    double? Magnitude,
    string? Size,
    double? SizeMajorArcmin,
    double? SizeMinorArcmin,
    double CurrentAltDeg,
    double CurrentAzDeg,
    double PeakAltDeg,
    DateTime PeakUtc,
    int Score,
    bool? FitsCameraFov,
    double? CameraFovWidthArcmin,
    double? CameraFovHeightArcmin);