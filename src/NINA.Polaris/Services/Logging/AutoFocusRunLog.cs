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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Polaris.Services.Logging;

/// <summary>
/// One JSON file per autofocus run under <c>logs/autofocus/</c>, as N.I.N.A.
/// keeps them: the points, the fit, R², the chosen position, temperature,
/// filter and attempts. Kept for 90 days, apart from the 7-day application log,
/// so two nights can be compared later ("was the softer night focus or
/// seeing?") without digging the run out of the session logs.
/// </summary>
public static class AutoFocusRunLog {
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NINA.Polaris", "logs", "autofocus");

    internal static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    // A heavily defocused sample has no HFR (NaN); written as "NaN" rather
    // than failing the whole file.
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public sealed record Settings(int OffsetSteps, int StepSize, double ExposureSeconds, int Binning,
        int FramesPerPoint, string Metric, double RSquaredThreshold, int MaxAttempts);

    public sealed record Run(int Version, string Source, string? Focuser, string? Camera, string? Filter,
        double? TemperatureC, Settings Settings, AutoFocusResult Result);

    public sealed record Summary(string Name, long Size, DateTime StartedUtc, bool Success, string? Filter,
        double? TemperatureC, double? FinalHfr, double RSquared, int FinalPosition, string Method,
        int Attempts, int Points, string? Error);

    internal static string FileName(DateTime startedUtc, string? filter) {
        var stamp = (startedUtc == default ? DateTime.UtcNow : startedUtc).ToLocalTime()
            .ToString("yyyy-MM-dd_HHmmss");
        var tag = SafeTag(filter);
        return tag.Length > 0 ? $"af_{stamp}_{tag}.json" : $"af_{stamp}.json";
    }

    private static string SafeTag(string? s) {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var chars = s.Trim().Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray();
        return new string(chars).Trim('_');
    }

    /// <summary>Writes the run and sweeps files past the retention. Returns
    /// the path written.</summary>
    public static string Write(Run run, string? dir = null) {
        dir ??= Dir;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileName(run.Result.StartedAt, run.Filter));
        File.WriteAllText(path, JsonSerializer.Serialize(run, Json));
        Sweep(dir, DateTime.UtcNow);
        return path;
    }

    internal static void Sweep(string dir, DateTime nowUtc) {
        try {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("af_*.json"))
                if (nowUtc - f.LastWriteTimeUtc > Retention) { try { f.Delete(); } catch { } }
        } catch { }
    }

    /// <summary>Newest first, with what a list needs to tell runs apart.
    /// A file that does not parse is listed by name only.</summary>
    public static List<Summary> List(string? dir = null, int max = 300) {
        dir ??= Dir;
        var list = new List<Summary>();
        if (!Directory.Exists(dir)) return list;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("af_*.json")
                     .OrderByDescending(f => f.LastWriteTimeUtc).Take(max)) {
            try {
                var run = JsonSerializer.Deserialize<Run>(File.ReadAllText(f.FullName), Json);
                var r = run?.Result;
                if (run == null || r == null) throw new JsonException("empty");
                list.Add(new Summary(f.Name, f.Length, r.StartedAt, r.Success, run.Filter, run.TemperatureC,
                    r.FinalMeasuredHfr is double h && double.IsFinite(h) ? h : null,
                    double.IsFinite(r.RSquared) ? r.RSquared : 0, r.FinalPosition, r.Method,
                    r.Attempts, r.Points?.Count ?? 0, r.Error));
            } catch {
                list.Add(new Summary(f.Name, f.Length, f.LastWriteTimeUtc, false, null, null, null, 0, 0, "",
                    0, 0, "unreadable file"));
            }
        }
        return list;
    }
}
