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
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Polaris.Services;

/// <summary>
/// One satellite as CelesTrak publishes it: a name line and the two TLE lines.
/// </summary>
public sealed record SatelliteTle(string Name, int Norad, string Line1, string Line2) {
    /// <summary>Epoch from line 1 (columns 19-32, two-digit year + day of year).</summary>
    public DateTime EpochUtc => SatelliteTleService.EpochOf(Line1);
}

/// <summary>
/// The satellites the SKY map draws: the ISS, Tiangong and the hundred-odd
/// brightest artificial satellites, as CelesTrak's "visual" group lists them.
///
/// The sky engine propagates TLEs itself (SGP4) and reads them from
/// <c>/sky/data/skydata/tle_satellite.jsonl.gz</c>. This service owns that
/// file: a snapshot ships in wwwroot, a downloaded set replaces it under the
/// data directory, and a middleware serves whichever is current. A TLE is
/// only good for days (the ISS is a degree off after a week), so the file's
/// age is reported for the UI and refreshed daily when the host has internet.
/// </summary>
public class SatelliteTleService {
    public const string FileName = "tle_satellite.jsonl.gz";
    private const string StatusFileName = "tle_satellite.status.json";

    // ISS and Tiangong: kept from the "stations" group even if the "visual"
    // list ever drops them, and given search aliases.
    public const int IssNorad = 25544;
    public const int TiangongNorad = 48274;
    private static readonly Dictionary<int, string[]> Aliases = new() {
        [IssNorad] = new[] { "ISS", "International Space Station" },
        [TiangongNorad] = new[] { "Tiangong", "CSS", "Chinese Space Station" },
        [20580] = new[] { "Hubble", "HST", "Hubble Space Telescope" },
    };

    private readonly ILogger<SatelliteTleService> _logger;
    private readonly string _bundledPath;
    private readonly string _overridePath;
    private readonly string _statusPath;
    private readonly Dictionary<int, double> _stdMag = new();
    private readonly object _gate = new();

    public SatelliteTleService(IWebHostEnvironment env, IConfiguration config,
                               ILogger<SatelliteTleService> logger) {
        _logger = logger;
        _bundledPath = Path.Combine(env.WebRootPath, "sky", "data", "skydata", FileName);
        var dir = config.GetValue("Sky:SatelliteDir",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA.Polaris", "sky"))!;
        _overridePath = Path.Combine(dir, FileName);
        _statusPath = Path.Combine(dir, StatusFileName);
        LoadStdMag(Path.Combine(env.WebRootPath, "data", "satellite-stdmag.json"));
        LoadStatus();
    }

    /// <summary>"bundled" or "celestrak".</summary>
    public string Source { get; private set; } = "bundled";
    public DateTime? FetchedAtUtc { get; private set; }
    public DateTime? LatestEpochUtc { get; private set; }
    public int Count { get; private set; }

    /// <summary>The file the sky engine should get right now.</summary>
    public string CurrentPath =>
        Source != "bundled" && File.Exists(_overridePath) ? _overridePath : _bundledPath;

    // ---- TLE text ----

    /// <summary>Parse CelesTrak's 3-line format (name, line 1, line 2); a bare
    /// 2-line pair gets its NORAD number as name. Lines that do not check
    /// out (wrong prefix, no catalogue number) are skipped.</summary>
    public static List<SatelliteTle> Parse(string text) {
        var result = new List<SatelliteTle>();
        var lines = text.Replace("\r", "").Split('\n');
        string? name = null;
        for (int i = 0; i < lines.Length; i++) {
            var l = lines[i].TrimEnd();
            if (l.Length == 0) continue;
            if (l.StartsWith("1 ") && i + 1 < lines.Length && lines[i + 1].StartsWith("2 ")) {
                var l2 = lines[i + 1].TrimEnd();
                if (l.Length < 32 || !int.TryParse(l.AsSpan(2, 5), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var norad)) {
                    name = null; i++; continue;
                }
                result.Add(new SatelliteTle(name ?? norad.ToString("00000", CultureInfo.InvariantCulture),
                    norad, l, l2));
                name = null;
                i++;
                continue;
            }
            if (l.StartsWith("2 ")) continue;
            name = l.Trim();
        }
        return result;
    }

    public static DateTime EpochOf(string line1) {
        var yy = int.Parse(line1.AsSpan(18, 2), NumberStyles.Integer, CultureInfo.InvariantCulture);
        var doy = double.Parse(line1.AsSpan(20, 12), NumberStyles.Float, CultureInfo.InvariantCulture);
        var year = yy < 57 ? 2000 + yy : 1900 + yy;
        return new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(doy - 1);
    }

    /// <summary>The working set from the two CelesTrak groups: everything in
    /// "visual", plus the ISS and Tiangong from "stations" (the rest of that
    /// group is modules and cargo ships docked to them, on the same orbit).
    /// One entry per NORAD number.</summary>
    public static List<SatelliteTle> Select(IEnumerable<SatelliteTle> visual, IEnumerable<SatelliteTle> stations) {
        var byNorad = new Dictionary<int, SatelliteTle>();
        foreach (var s in stations)
            if (s.Norad == IssNorad || s.Norad == TiangongNorad) byNorad[s.Norad] = s;
        foreach (var s in visual)
            byNorad.TryAdd(s.Norad, s);
        return byNorad.Values.OrderBy(s => s.Norad).ToList();
    }

    // ---- the engine's file ----

    /// <summary>One JSON object per line in the shape the sky engine's
    /// satellites module parses (types, model, model_data.tle, names).</summary>
    public string ToJsonl(IEnumerable<SatelliteTle> sats) {
        var sb = new StringBuilder();
        // TLE lines carry "+" in the drag term; keep it literal rather than
        // as +, which the engine's JSON reader does not unescape.
        var json = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        foreach (var s in sats) {
            var names = new List<string>();
            var shortName = s.Name;
            if (Aliases.TryGetValue(s.Norad, out var aliases)) {
                shortName = aliases[0];
                foreach (var a in aliases) names.Add("NAME " + a);
            }
            // "ISS (ZARYA)" also answers to "ISS"; "OAO 3 (COPERNICUS)" to "OAO 3".
            var paren = s.Name.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0) {
                var bare = s.Name[..paren].Trim();
                if (!names.Contains("NAME " + bare)) names.Add("NAME " + bare);
                if (shortName == s.Name) shortName = bare;
            }
            if (!names.Contains("NAME " + s.Name)) names.Add("NAME " + s.Name);
            names.Add("NORAD " + s.Norad.ToString("00000", CultureInfo.InvariantCulture));

            var model = new Dictionary<string, object> {
                ["norad_number"] = s.Norad,
                ["designation"] = s.Line1.Substring(9, 8).Trim(),
                ["tle"] = new[] { s.Line1, s.Line2 },
            };
            if (_stdMag.TryGetValue(s.Norad, out var mag)) model["mag"] = mag;
            var obj = new Dictionary<string, object> {
                ["types"] = new[] { "Asa" },
                ["model"] = "tle_satellite",
                ["model_data"] = model,
                ["names"] = names,
                ["short_name"] = shortName,
            };
            sb.Append(JsonSerializer.Serialize(obj, json)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>gzip with the FNAME flag set and a file name in the header.
    /// The engine's reader accepts exactly that header shape (the one
    /// <c>gzip</c> and Python write) and rejects the bare one GZipStream
    /// produces, so the members are written by hand.</summary>
    public static byte[] Gzip(string text, string name = "tle_satellite.jsonl") {
        var data = Encoding.UTF8.GetBytes(text);
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x1f, 0x8b, 8, 8, 0, 0, 0, 0, 0, 3 });   // FLG=FNAME, mtime 0, OS=Unix
        ms.Write(Encoding.ASCII.GetBytes(name));
        ms.WriteByte(0);
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data);
        ms.Write(BitConverter.GetBytes(Crc32(data)));
        ms.Write(BitConverter.GetBytes((uint)data.Length));
        return ms.ToArray();
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable() {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++) {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] data) {
        uint c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    /// <summary>Install a downloaded set: write the engine file and its status
    /// sidecar under the data directory. An empty set is refused so a bad
    /// download never leaves the map with fewer satellites than the snapshot.</summary>
    public void Replace(IReadOnlyList<SatelliteTle> sats, string source, DateTime fetchedUtc) {
        if (sats == null || sats.Count == 0)
            throw new ArgumentException("Refusing to install an empty satellite set.", nameof(sats));
        if (!sats.Any(s => s.Norad == IssNorad))
            throw new ArgumentException("The downloaded set has no ISS; keeping the current one.", nameof(sats));

        Directory.CreateDirectory(Path.GetDirectoryName(_overridePath)!);
        var tmp = _overridePath + ".tmp";
        File.WriteAllBytes(tmp, Gzip(ToJsonl(sats)));
        File.Move(tmp, _overridePath, overwrite: true);

        var status = new StatusFile {
            Source = source, FetchedAtUtc = fetchedUtc, Count = sats.Count,
            LatestEpochUtc = sats.Max(s => s.EpochUtc)
        };
        File.WriteAllText(_statusPath, JsonSerializer.Serialize(status));
        lock (_gate) {
            Source = status.Source; FetchedAtUtc = status.FetchedAtUtc;
            Count = status.Count; LatestEpochUtc = status.LatestEpochUtc;
        }
        _logger.LogInformation("Installed {Count} satellite TLEs from {Source} (latest epoch {Epoch:u})",
            sats.Count, source, status.LatestEpochUtc);
    }

    // ---- startup ----

    private void LoadStatus() {
        if (File.Exists(_overridePath) && File.Exists(_statusPath)) {
            try {
                var s = JsonSerializer.Deserialize<StatusFile>(File.ReadAllText(_statusPath));
                if (s != null && s.Count > 0) {
                    Source = s.Source; FetchedAtUtc = s.FetchedAtUtc;
                    Count = s.Count; LatestEpochUtc = s.LatestEpochUtc;
                    _logger.LogInformation("Satellite TLEs: {Count} from {Source}, fetched {At:u}",
                        Count, Source, FetchedAtUtc);
                    return;
                }
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Satellite TLE status file unreadable; using the bundled set");
            }
        }
        Source = "bundled";
        FetchedAtUtc = null;
        try {
            var (count, latest) = Inspect(_bundledPath);
            Count = count; LatestEpochUtc = latest;
            _logger.LogInformation("Satellite TLEs: bundled set of {Count}, latest epoch {Epoch:u}", Count, latest);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Bundled satellite file unreadable at {Path}", _bundledPath);
            Count = 0; LatestEpochUtc = null;
        }
    }

    /// <summary>Count and latest epoch of an engine file, read back from the
    /// TLE line 1 of each record.</summary>
    public static (int count, DateTime? latest) Inspect(string gzPath) {
        if (!File.Exists(gzPath)) return (0, null);
        using var fs = File.OpenRead(gzPath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        int count = 0; DateTime? latest = null;
        string? line;
        while ((line = reader.ReadLine()) != null) {
            if (line.Length == 0) continue;
            try {
                using var doc = JsonDocument.Parse(line);
                var tle = doc.RootElement.GetProperty("model_data").GetProperty("tle");
                var l1 = tle[0].GetString();
                if (l1 == null) continue;
                var e = EpochOf(l1);
                if (latest == null || e > latest) latest = e;
                count++;
            } catch (Exception) { /* a bad line is the engine's problem, not the counter's */ }
        }
        return (count, latest);
    }

    private void LoadStdMag(string path) {
        try {
            if (!File.Exists(path)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(path));
            if (map == null) return;
            foreach (var (k, v) in map)
                if (int.TryParse(k, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) _stdMag[n] = v;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "satellite-stdmag.json unreadable; the engine's default magnitude applies");
        }
    }

    private sealed class StatusFile {
        [JsonPropertyName("source")] public string Source { get; set; } = "celestrak";
        [JsonPropertyName("fetchedAtUtc")] public DateTime? FetchedAtUtc { get; set; }
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("latestEpochUtc")] public DateTime? LatestEpochUtc { get; set; }
    }
}

/// <summary>
/// Downloads the two CelesTrak groups and installs them. Offline is normal in
/// the field, so a failed fetch is reported quietly and the current set stays.
/// </summary>
public class SatelliteTleUpdater {
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SatelliteTleService _sats;
    private readonly ILogger<SatelliteTleUpdater> _logger;

    public SatelliteTleUpdater(SatelliteTleService sats, ILogger<SatelliteTleUpdater> logger) {
        _sats = sats;
        _logger = logger;
        if (!Http.DefaultRequestHeaders.UserAgent.Any())
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("NINA.Polaris/1.0 (+https://github.com/DanWBR/NINA.Polaris)");
    }

    public static string VisualUrl => "https://celestrak.org/NORAD/elements/gp.php?GROUP=visual&FORMAT=tle";
    public static string StationsUrl => "https://celestrak.org/NORAD/elements/gp.php?GROUP=stations&FORMAT=tle";

    public async Task<int> RefreshAsync(CancellationToken ct = default) {
        _logger.LogInformation("Fetching satellite TLEs from CelesTrak");
        var visual = await FetchAsync(VisualUrl, ct);
        var stations = await FetchAsync(StationsUrl, ct);
        var selected = SatelliteTleService.Select(SatelliteTleService.Parse(visual), SatelliteTleService.Parse(stations));
        if (selected.Count == 0)
            throw new InvalidOperationException("CelesTrak returned no usable TLEs");
        _sats.Replace(selected, "celestrak", DateTime.UtcNow);
        return selected.Count;
    }

    /// <summary>Install text fetched by a client (both groups concatenated is fine).</summary>
    public int Import(string text) {
        var all = SatelliteTleService.Parse(text);
        var selected = SatelliteTleService.Select(all, all);
        if (selected.Count == 0)
            throw new InvalidOperationException("No usable TLEs in the supplied data");
        _sats.Replace(selected, "celestrak", DateTime.UtcNow);
        return selected.Count;
    }

    private static async Task<string> FetchAsync(string url, CancellationToken ct) {
        using var resp = await Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"CelesTrak returned {(int)resp.StatusCode} {resp.ReasonPhrase}");
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (body.TrimStart().StartsWith("<", StringComparison.Ordinal))
            throw new InvalidOperationException("CelesTrak answered with a web page, not TLEs (captive portal?)");
        return body;
    }
}

/// <summary>
/// Daily refresh when the set is older than a day. Startup is not delayed and
/// failures only reach the debug log.
/// </summary>
public class SatelliteTleRefreshWorker : BackgroundService {
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Period = TimeSpan.FromHours(6);

    private readonly SatelliteTleUpdater _updater;
    private readonly SatelliteTleService _sats;
    private readonly ILogger<SatelliteTleRefreshWorker> _logger;

    public SatelliteTleRefreshWorker(SatelliteTleUpdater updater, SatelliteTleService sats,
                                     ILogger<SatelliteTleRefreshWorker> logger) {
        _updater = updater;
        _sats = sats;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try { await Task.Delay(StartupDelay, stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested) {
            var age = _sats.FetchedAtUtc is { } t ? DateTime.UtcNow - t : TimeSpan.MaxValue;
            if (age >= MaxAge) {
                try {
                    await _updater.RefreshAsync(stoppingToken);
                } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                    return;
                } catch (Exception ex) {
                    _logger.LogDebug(ex, "Satellite TLE refresh did not complete (offline?); keeping the current set");
                }
            }
            try { await Task.Delay(Period, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
