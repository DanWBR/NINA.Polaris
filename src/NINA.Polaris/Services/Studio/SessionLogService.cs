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

namespace NINA.Polaris.Services.Studio;

/// <summary>
/// The night log: every frame in the library, grouped into observing nights,
/// with what was shot, on what, how the guiding and the sensor behaved, and
/// which calibration frames in the library match each set of lights. Pure
/// aggregation over what the FITS headers already carry; the operator adds
/// seeing, transparency and notes per night, kept on the profile.
/// </summary>
public class SessionLogService {
    private readonly FrameLibraryService _library;
    private readonly ProfileService _profiles;

    public SessionLogService(FrameLibraryService library, ProfileService profiles) {
        _library = library;
        _profiles = profiles;
    }

    // ----- nights --------------------------------------------------------

    /// <summary>The night a frame belongs to: local date of the evening it
    /// started. A frame taken before local noon counts for the previous
    /// evening, so a 03:00 sub sits with the 22:00 ones. DATE-LOC when the
    /// writer stamped it, else DATE-OBS (UTC) shifted to the host's zone.</summary>
    internal static string NightKeyOf(string dateObs, string dateLoc, TimeZoneInfo? zone = null) {
        DateTime local;
        if (!string.IsNullOrWhiteSpace(dateLoc)
            && DateTime.TryParse(dateLoc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var l)) {
            local = l;
        } else if (DateTime.TryParse(dateObs, CultureInfo.InvariantCulture,
                       DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var u)) {
            local = TimeZoneInfo.ConvertTimeFromUtc(u, zone ?? TimeZoneInfo.Local);
        } else {
            return "";
        }
        return local.AddHours(-12).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static DateTime? LocalTimeOf(string dateObs, string dateLoc) {
        if (!string.IsNullOrWhiteSpace(dateLoc)
            && DateTime.TryParse(dateLoc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var l)) return l;
        if (DateTime.TryParse(dateObs, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var u))
            return TimeZoneInfo.ConvertTimeFromUtc(u, TimeZoneInfo.Local);
        return null;
    }

    private static bool IsLight(NightLogFrame f) =>
        !IsMaster(f) && (string.IsNullOrEmpty(f.ImageType) || f.ImageType.Contains("light", StringComparison.OrdinalIgnoreCase));
    /// <summary>Stacked products (MASTERLIGHT, MASTERCAL, master darks and
    /// flats) are results, not frames of a night.</summary>
    private static bool IsMaster(NightLogFrame f) =>
        (f.ImageType ?? "").Contains("master", StringComparison.OrdinalIgnoreCase);
    private static string Kind(NightLogFrame f) {
        var t = (f.ImageType ?? "").ToLowerInvariant();
        if (t.Contains("master")) return "master";
        if (t.Contains("dark")) return "dark";
        if (t.Contains("flat")) return "flat";
        if (t.Contains("bias")) return "bias";
        if (t.Contains("snap")) return "snap";
        return "light";
    }

    public IReadOnlyList<NightSummary> ListNights() => BuildNights(_library.AllForNightLog())
        .Select(n => n.Summary).OrderByDescending(s => s.Night).ToList();

    public NightDetail? GetNight(string night) {
        var all = _library.AllForNightLog();
        var n = BuildNights(all).FirstOrDefault(x => x.Summary.Night == night);
        if (n == null) return null;
        return BuildDetail(n, all);
    }

    // ----- notes ---------------------------------------------------------

    public SessionNote? GetNote(string night) =>
        _profiles.Active.SessionNotes.FirstOrDefault(x => x.Night == night);

    public SessionNote SaveNote(string night, SessionNote note) {
        var list = _profiles.Active.SessionNotes;
        var existing = list.FirstOrDefault(x => x.Night == night);
        if (existing == null) { existing = new SessionNote { Night = night }; list.Add(existing); }
        existing.Seeing = note.Seeing;
        existing.Transparency = note.Transparency;
        existing.Notes = note.Notes;
        existing.UpdatedUtc = DateTime.UtcNow;
        _profiles.Save();
        return existing;
    }

    // ----- aggregation -----------------------------------------------------

    private sealed class NightBucket {
        public NightSummary Summary = null!;
        public List<NightLogFrame> Frames = new();
    }

    internal static List<(string Night, List<NightLogFrame> Frames)> GroupByNight(IEnumerable<NightLogFrame> frames, TimeZoneInfo? zone = null) {
        var groups = new Dictionary<string, List<NightLogFrame>>();
        foreach (var f in frames) {
            var k = NightKeyOf(f.DateObs, f.DateLoc, zone);
            if (k.Length == 0) continue;
            if (!groups.TryGetValue(k, out var l)) groups[k] = l = new List<NightLogFrame>();
            l.Add(f);
        }
        return groups.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private List<NightBucket> BuildNights(IReadOnlyList<NightLogFrame> all) {
        var notes = _profiles.Active.SessionNotes.ToDictionary(n => n.Night, n => n);
        var result = new List<NightBucket>();
        foreach (var (night, allFrames) in GroupByNight(all)) {
            var frames = allFrames.Where(f => !IsMaster(f)).ToList();
            var lights = frames.Where(IsLight).ToList();
            // A night with no lights and no calibration frames (snaps, solve
            // frames, streams) is not a session.
            if (lights.Count == 0 && !frames.Any(f => Kind(f) is "dark" or "flat" or "bias")) continue;
            var times = frames.Select(f => LocalTimeOf(f.DateObs, f.DateLoc)).Where(t => t.HasValue).Select(t => t!.Value).ToList();
            var rms = lights.Where(f => f.GuideRms is > 0).Select(f => f.GuideRms!.Value).ToList();
            var temps = lights.Where(f => f.CcdTemp.HasValue).Select(f => f.CcdTemp!.Value).ToList();
            notes.TryGetValue(night, out var note);
            var summary = new NightSummary(
                Night: night,
                Start: times.Count > 0 ? times.Min() : null,
                End: times.Count > 0 ? times.Max() : null,
                Cameras: frames.Select(f => f.Camera).Where(s => s.Length > 0).Distinct().OrderBy(s => s).ToList(),
                Telescopes: frames.Select(f => f.Telescope).Where(s => s.Length > 0).Distinct().OrderBy(s => s).ToList(),
                Targets: lights.Select(f => f.Target).Where(s => s.Length > 0).Distinct().OrderBy(s => s).ToList(),
                Lights: lights.Count,
                IntegrationHours: Math.Round(lights.Sum(f => f.ExposureSec) / 3600.0, 2),
                Darks: frames.Count(f => Kind(f) == "dark"),
                Flats: frames.Count(f => Kind(f) == "flat"),
                Biases: frames.Count(f => Kind(f) == "bias"),
                GuideRmsMean: rms.Count > 0 ? Math.Round(rms.Average(), 2) : null,
                CcdTempMin: temps.Count > 0 ? Math.Round(temps.Min(), 1) : null,
                CcdTempMax: temps.Count > 0 ? Math.Round(temps.Max(), 1) : null,
                Seeing: note?.Seeing, Transparency: note?.Transparency, HasNotes: !string.IsNullOrWhiteSpace(note?.Notes));
            result.Add(new NightBucket { Summary = summary, Frames = frames });
        }
        return result;
    }

    private NightDetail BuildDetail(NightBucket n, IReadOnlyList<NightLogFrame> all) {
        var frames = n.Frames;
        var lights = frames.Where(IsLight).ToList();
        var targets = new List<TargetSession>();
        foreach (var tg in lights.GroupBy(f => f.Target).OrderBy(g => g.Min(f => f.DateObs))) {
            var filters = tg.GroupBy(f => f.Filter.Length == 0 ? "(none)" : f.Filter)
                .Select(fg => new FilterSet(
                    Filter: fg.Key,
                    Frames: fg.Count(),
                    ExposuresSec: fg.Select(f => f.ExposureSec).Distinct().OrderBy(x => x).ToList(),
                    IntegrationMin: Math.Round(fg.Sum(f => f.ExposureSec) / 60.0, 1),
                    Gain: fg.Select(f => f.Gain).Distinct().ToList(),
                    Binning: fg.Select(f => f.Binning).Distinct().ToList()))
                .OrderBy(f => f.Filter).ToList();
            var rms = tg.Where(f => f.GuideRms is > 0).Select(f => f.GuideRms!.Value).ToList();
            var times = tg.Select(f => LocalTimeOf(f.DateObs, f.DateLoc)).Where(t => t.HasValue).Select(t => t!.Value).ToList();
            var temps = tg.Where(f => f.CcdTemp.HasValue).Select(f => f.CcdTemp!.Value).ToList();
            var focus = tg.Where(f => f.FocusPos.HasValue).Select(f => f.FocusPos!.Value).ToList();
            targets.Add(new TargetSession(
                Target: tg.Key.Length == 0 ? "(no target)" : tg.Key,
                Start: times.Count > 0 ? times.Min() : null,
                End: times.Count > 0 ? times.Max() : null,
                Frames: tg.Count(),
                IntegrationMin: Math.Round(tg.Sum(f => f.ExposureSec) / 60.0, 1),
                Filters: filters,
                GuideRmsMean: rms.Count > 0 ? Math.Round(rms.Average(), 2) : null,
                GuideRmsMax: rms.Count > 0 ? Math.Round(rms.Max(), 2) : null,
                GuidedFrames: rms.Count,
                CcdTempMean: temps.Count > 0 ? Math.Round(temps.Average(), 1) : null,
                FocusPosMin: focus.Count > 0 ? focus.Min() : null,
                FocusPosMax: focus.Count > 0 ? focus.Max() : null,
                PierSides: tg.Select(f => f.PierSide).Where(s => s.Length > 0).Distinct().ToList(),
                Cameras: tg.Select(f => f.Camera).Where(s => s.Length > 0).Distinct().ToList()));
        }

        var amb = lights.Where(f => f.AmbientTemp.HasValue).Select(f => f.AmbientTemp!.Value).ToList();
        var hum = lights.Where(f => f.Humidity.HasValue).Select(f => f.Humidity!.Value).ToList();
        var sky = lights.Where(f => f.SkyBrightness.HasValue).Select(f => f.SkyBrightness!.Value).ToList();
        // Lights name the rig; a dark or flat only adds a line when its camera
        // shot no light that night (a calibration-only night).
        var equipment = (lights.Count > 0 ? lights : frames)
            .Select(f => (f.Camera, f.Telescope, f.FocalLen))
            .Where(e => e.Camera.Length > 0 || e.Telescope.Length > 0)
            .Distinct()
            .Select(e => new EquipmentLine(e.Camera, e.Telescope, e.FocalLen))
            .ToList();

        return new NightDetail(
            Summary: n.Summary,
            Targets: targets,
            Equipment: equipment,
            AmbientTempMin: amb.Count > 0 ? Math.Round(amb.Min(), 1) : null,
            AmbientTempMax: amb.Count > 0 ? Math.Round(amb.Max(), 1) : null,
            HumidityMean: hum.Count > 0 ? Math.Round(hum.Average(), 0) : null,
            SkyBrightnessMean: sky.Count > 0 ? Math.Round(sky.Average(), 2) : null,
            Calibration: MatchCalibration(lights, all, n.Summary.Night),
            Note: GetNote(n.Summary.Night));
    }

    // ----- calibration matching ---------------------------------------------

    /// <summary>For each distinct light configuration of the night, what the
    /// library holds to calibrate it: darks with the same camera, gain,
    /// offset, binning and exposure within 2 percent and sensor temperature
    /// within 2 degrees; bias with the same camera, gain, offset and binning;
    /// flats with the same camera, filter and binning, the same night first,
    /// otherwise the nearest night. Counts and the night they come from, so a
    /// gap is visible before the stacking session finds it.</summary>
    internal static List<CalibrationMatch> MatchCalibration(IReadOnlyList<NightLogFrame> lights,
            IReadOnlyList<NightLogFrame> library, string night, TimeZoneInfo? zone = null) {
        var result = new List<CalibrationMatch>();
        var darks = library.Where(f => Kind(f) == "dark").ToList();
        var biases = library.Where(f => Kind(f) == "bias").ToList();
        var flats = library.Where(f => Kind(f) == "flat").ToList();
        var configs = lights
            .GroupBy(f => (f.Camera, f.Gain, f.Offset, f.Binning, Exp: Math.Round(f.ExposureSec, 1), Filter: f.Filter))
            .OrderBy(g => g.Key.Filter).ThenBy(g => g.Key.Exp);
        foreach (var g in configs) {
            var k = g.Key;
            var temps = g.Where(f => f.CcdTemp.HasValue).Select(f => f.CcdTemp!.Value).ToList();
            double? temp = temps.Count > 0 ? temps.Average() : null;
            var dk = darks.Where(d => SameCamera(d.Camera, k.Camera) && d.Gain == k.Gain && d.Offset == k.Offset
                                      && d.Binning == k.Binning
                                      && Math.Abs(d.ExposureSec - k.Exp) <= Math.Max(0.5, 0.02 * k.Exp)
                                      && (temp == null || d.CcdTemp == null || Math.Abs(d.CcdTemp.Value - temp.Value) <= 2.0)).ToList();
            var bi = biases.Where(b => SameCamera(b.Camera, k.Camera) && b.Gain == k.Gain && b.Offset == k.Offset && b.Binning == k.Binning).ToList();
            var fl = flats.Where(f => SameCamera(f.Camera, k.Camera) && f.Binning == k.Binning
                                      && string.Equals(f.Filter, k.Filter, StringComparison.OrdinalIgnoreCase)).ToList();
            var flatNight = NearestNight(fl, night, zone);
            var flSame = fl.Where(f => NightKeyOf(f.DateObs, f.DateLoc, zone) == flatNight).ToList();
            result.Add(new CalibrationMatch(
                Camera: k.Camera, Filter: k.Filter.Length == 0 ? "(none)" : k.Filter,
                ExposureSec: k.Exp, Gain: k.Gain, Offset: k.Offset, Binning: k.Binning,
                CcdTemp: temp.HasValue ? Math.Round(temp.Value, 1) : null,
                Lights: g.Count(),
                Darks: dk.Count, DarksNights: dk.Select(d => NightKeyOf(d.DateObs, d.DateLoc, zone)).Distinct().OrderByDescending(s => s).Take(3).ToList(),
                Biases: bi.Count,
                Flats: flSame.Count, FlatsNight: flatNight));
        }
        return result;
    }

    private static bool SameCamera(string a, string b) =>
        a.Length == 0 || b.Length == 0 || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? NearestNight(List<NightLogFrame> frames, string night, TimeZoneInfo? zone) {
        if (frames.Count == 0) return null;
        if (!DateTime.TryParseExact(night, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var target)) return null;
        string? best = null; double bestDist = double.MaxValue;
        foreach (var f in frames) {
            var k = NightKeyOf(f.DateObs, f.DateLoc, zone);
            if (!DateTime.TryParseExact(k, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
            var dist = Math.Abs((d - target).TotalDays);
            if (dist < bestDist) { bestDist = dist; best = k; }
        }
        return best;
    }
}

public record NightSummary(string Night, DateTime? Start, DateTime? End,
    List<string> Cameras, List<string> Telescopes, List<string> Targets,
    int Lights, double IntegrationHours, int Darks, int Flats, int Biases,
    double? GuideRmsMean, double? CcdTempMin, double? CcdTempMax,
    int? Seeing, int? Transparency, bool HasNotes);

public record FilterSet(string Filter, int Frames, List<double> ExposuresSec, double IntegrationMin, List<int> Gain, List<int> Binning);

public record TargetSession(string Target, DateTime? Start, DateTime? End, int Frames, double IntegrationMin,
    List<FilterSet> Filters, double? GuideRmsMean, double? GuideRmsMax, int GuidedFrames,
    double? CcdTempMean, int? FocusPosMin, int? FocusPosMax, List<string> PierSides, List<string> Cameras);

public record EquipmentLine(string Camera, string Telescope, double FocalLen);

public record CalibrationMatch(string Camera, string Filter, double ExposureSec, int Gain, int Offset, int Binning,
    double? CcdTemp, int Lights, int Darks, List<string> DarksNights, int Biases, int Flats, string? FlatsNight);

public record NightDetail(NightSummary Summary, List<TargetSession> Targets, List<EquipmentLine> Equipment,
    double? AmbientTempMin, double? AmbientTempMax, double? HumidityMean, double? SkyBrightnessMean,
    List<CalibrationMatch> Calibration, SessionNote? Note);
