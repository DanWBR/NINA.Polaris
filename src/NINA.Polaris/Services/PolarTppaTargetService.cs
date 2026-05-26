namespace NINA.Polaris.Services;

/// <summary>
/// PA-6: pick "good targets to start TPPA from now" out of the existing
/// SkyCatalogService. TPPA needs three points spaced by RA, all plate-
/// solved successfully, so the starting region matters for both
/// numerical stability and solver success. Geometry priorities lifted
/// from N.I.N.A. desktop's TPPA docs + SharpCap's polar align guidance:
///
///   • Dec sign matches hemisphere. North: positive Dec; south: negative.
///     The math degenerates as you approach the pole itself (~90°) and
///     loses constraint power below ~50° — sweet spot ~60-75° |Dec|.
///
///   • Altitude ≥ 30° at the START + at all three TPPA points. Below
///     30° atmospheric refraction skews the plate-solve angles and
///     the alt error rendered to the user gets noisy. We sample at
///     start, +5 min (point 2), +10 min (point 3 + a bit of margin
///     for slew/solve time) and reject if any of those dip below.
///
///   • Hour angle small (close to meridian). Far-east / far-west
///     means low alt by the time we get to the third point, AND the
///     altitude error component is harder to disambiguate from
///     azimuth error when the field is near the horizon.
///
///   • Stay clear of the pole circle (avoid |Dec| > 85°). Mathematically
///     fine but the plate solver hates the few-arcmin shift per RA
///     step, often picking the wrong solution.
///
///   • Plate-solvable: skip very faint catalog entries (mag > 11)
///     because they correlate with sparse fields. We use the catalog
///     object's brightness only as a proxy for "I expect stars
///     around it" — the solver itself doesn't care about the named
///     object.
///
/// The output is a small ranked list (default top 5) the UI shows as
/// chips. Clicking a chip skylinks to the existing slew+center flow.
/// Pure suggestion — TPPA still runs from wherever the mount is when
/// the user presses Start, so we don't need to enforce anything.
/// </summary>
public class PolarTppaTargetService {
    private readonly ProfileService _profile;
    private readonly SkyCatalogService _catalog;

    public PolarTppaTargetService(ProfileService profile, SkyCatalogService catalog) {
        _profile = profile;
        _catalog = catalog;
    }

    public IReadOnlyList<PolarTppaTargetSuggestion> Suggest(int limit = 5,
                                                             DateTime? nowUtc = null) {
        var p = _profile.Active;
        double lat = p.Latitude;
        double lng = p.Longitude;
        var now = nowUtc ?? DateTime.UtcNow;
        // TPPA total elapsed time depends on exposure + slew + solve.
        // 10 min covers a comfortable 3-point sweep with 30s exposures
        // and a small slew step. We require the candidate to stay
        // above 30° for the whole window.
        var t1 = now.AddMinutes(5);
        var t2 = now.AddMinutes(10);

        bool northern = lat >= 0;
        var results = new List<PolarTppaTargetSuggestion>();

        foreach (var obj in _catalog.AllObjects) {
            // Hemisphere + |Dec| gate
            double absDec = Math.Abs(obj.Dec);
            if (absDec < 50 || absDec > 85) continue;
            if (northern && obj.Dec < 0) continue;
            if (!northern && obj.Dec > 0) continue;

            // Plate-solve viability proxy
            if (obj.Magnitude > 11) continue;

            // Altitude at start + mid + end of the TPPA window
            var (alt0, az0) = AltitudeService.RaDecToAltAz(obj.Ra, obj.Dec, now, lat, lng);
            if (alt0 < 30) continue;
            var (alt1, _) = AltitudeService.RaDecToAltAz(obj.Ra, obj.Dec, t1, lat, lng);
            if (alt1 < 30) continue;
            var (alt2, _) = AltitudeService.RaDecToAltAz(obj.Ra, obj.Dec, t2, lat, lng);
            if (alt2 < 30) continue;

            // Hour angle (signed, [-12, +12]) — small magnitude = near meridian.
            var lst = MeridianFlipService.ComputeLstHours(now, lng);
            double ha = lst - obj.Ra;
            while (ha >  12) ha -= 24;
            while (ha < -12) ha += 24;

            // Scoring. Higher = better. Three contributions weighted to
            // roughly balance each other on a "decent" candidate.
            //   Dec sweet spot ~65°: penalize distance from 65° in |Dec| space.
            //   Altitude: linear bonus above 30°, capped at 80°.
            //   |HA|: penalize past the meridian, capped at 4h.
            double decSweetness  = 30 - Math.Min(30, Math.Abs(absDec - 65));   // 0..30
            double altBonus      = Math.Min(50, alt0 - 30);                    // 0..50
            double meridianBonus = 20 - Math.Min(20, Math.Abs(ha) * 5);        // 0..20
            int score = (int)Math.Round(decSweetness + altBonus + meridianBonus);

            results.Add(new PolarTppaTargetSuggestion(
                Name: obj.Name,
                CommonName: obj.CommonName,
                Type: obj.Type,
                RaHours: obj.Ra,
                DecDeg: obj.Dec,
                Magnitude: obj.Magnitude,
                CurrentAltDeg: Math.Round(alt0, 1),
                CurrentAzDeg: Math.Round(az0, 1),
                HourAngleHours: Math.Round(ha, 2),
                Score: score));
        }

        return results
            .OrderByDescending(r => r.Score)
            .Take(limit)
            .ToList();
    }
}

public record PolarTppaTargetSuggestion(
    string Name,
    string? CommonName,
    string Type,
    double RaHours,
    double DecDeg,
    double Magnitude,
    double CurrentAltDeg,
    double CurrentAzDeg,
    double HourAngleHours,
    int Score);
