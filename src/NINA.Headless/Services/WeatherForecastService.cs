using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Headless.Services;

/// <summary>
/// Proxy in front of the 7Timer ASTRO forecast API (https://www.7timer.info/).
/// 7Timer is a free, no-API-key weather service tuned for astronomy: it
/// returns categorical cloud cover, atmospheric seeing, transparency, and
/// lifted index alongside the usual temperature / humidity / wind data —
/// the parameters astrophotographers actually plan around.
///
/// We proxy the call through the backend so that:
///   1. Multiple clients (laptop + phone + tablet on the LAN) share an
///      in-memory cache with a 15-minute TTL — one HTTP call per coord
///      regardless of how many tabs the user has open.
///   2. Clients on the LAN that have no direct internet route still get
///      a forecast as long as the host server does.
///   3. Errors don't leak as raw fetch failures into the browser — we
///      surface them as DTOs with Available=false and a friendly message.
///
/// The 7Timer endpoint we use:
///   GET https://www.7timer.info/bin/astro.php?lon={lon}&lat={lat}&ac=0&unit=metric&output=json&tzshift=0
/// Returns dataseries[] of 3-hour slots, ~24 slots covering 3 days.
/// Reference: https://www.7timer.info/doc.php
/// </summary>
public class WeatherForecastService {
    private static readonly HttpClient Http = new() {
        Timeout = TimeSpan.FromSeconds(10)
    };
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    private readonly ILogger<WeatherForecastService> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public WeatherForecastService(ILogger<WeatherForecastService> logger) {
        _logger = logger;
        if (!Http.DefaultRequestHeaders.UserAgent.Any()) {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "NINA-Polaris/0.1 (https://github.com/DanWBR/nina-headless)");
        }
    }

    /// <summary>
    /// Fetch a forecast for the given coordinates. Returns cached data if
    /// a fresh entry exists; otherwise calls 7Timer and caches the parsed
    /// result. Never throws — on any failure returns Available=false with
    /// the error string set.
    /// </summary>
    public async Task<WeatherForecastDto> GetForecastAsync(double lat, double lon, CancellationToken ct = default) {
        var key = $"{lat:F2},{lon:F2}";
        if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.FetchedAt < CacheTtl) {
            return hit.Forecast;
        }

        try {
            var url = $"https://www.7timer.info/bin/astro.php" +
                      $"?lon={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                      $"&lat={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                      "&ac=0&unit=metric&output=json&tzshift=0";

            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) {
                _logger.LogWarning("7Timer returned {Status} for {Coords}", resp.StatusCode, key);
                return Unavailable($"Forecast service returned {(int)resp.StatusCode}");
            }

            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var raw = await JsonSerializer.DeserializeAsync<SevenTimerRoot>(stream, cancellationToken: ct);
            if (raw?.Dataseries == null || raw.Dataseries.Count == 0) {
                return Unavailable("Forecast response was empty");
            }

            var initUtc = ParseInitUtc(raw.Init);
            var slots = raw.Dataseries.Select(s => {
                var slotStart = initUtc.AddHours(s.Timepoint);
                var score = ScoreSlot(s);
                return new WeatherSlot(
                    UtcStart:        slotStart,
                    CloudCover:      s.Cloudcover,
                    Seeing:          s.Seeing,
                    Transparency:    s.Transparency,
                    LiftedIndex:     s.LiftedIndex,
                    Temp2m:          s.Temp2m,
                    Rh2m:            ParseRh(s.Rh2m),
                    WindSpeed:       s.Wind10m?.Speed ?? 0,
                    WindDirection:   s.Wind10m?.Direction ?? "",
                    PrecType:        s.PrecType ?? "none",
                    ObservationScore: score
                );
            }).ToList();

            var forecast = new WeatherForecastDto(true, "", initUtc, slots);
            _cache[key] = new CacheEntry(DateTime.UtcNow, forecast);
            return forecast;
        } catch (HttpRequestException ex) {
            _logger.LogWarning(ex, "7Timer unreachable for {Coords}", key);
            return Unavailable("Forecast service unreachable");
        } catch (TaskCanceledException) {
            return Unavailable("Forecast request timed out");
        } catch (JsonException ex) {
            _logger.LogWarning(ex, "7Timer response malformed for {Coords}", key);
            return Unavailable("Forecast response could not be parsed");
        } catch (Exception ex) {
            _logger.LogError(ex, "Unexpected error fetching forecast for {Coords}", key);
            return Unavailable("Unexpected error");
        }
    }

    /// <summary>
    /// Composite 0–100 observation score for a 7Timer slot. Higher is better.
    /// Weights chosen to match how astrophotographers actually rate a night:
    ///   - Cloud cover contributes up to 50 directly (50% weight).
    ///   - Seeing + transparency contribute up to 25 each (25% each), BUT
    ///     multiplied by a "visibility" factor derived from cloud cover —
    ///     perfect atmospheric stability is irrelevant if you can't see
    ///     through the clouds.
    ///   - Hard veto on any precipitation.
    ///   - Heavy penalty on very high humidity (dew kills a session even
    ///     under clear skies).
    /// </summary>
    public static int ScoreSlot(SevenTimerSlot s) {
        // 7Timer ranges:
        //   cloudcover: 1 (0–6%) → 9 (94–100%)
        //   seeing:     1 (<0.5") → 8 (>3")
        //   transparency: 1 (<0.3) → 8 (>1)
        // So smaller is better in all three.
        var cloudComp        = (10 - s.Cloudcover)    * 10.0   * 0.5;   // 0..45
        var seeingComp       = (9  - s.Seeing)        * 12.5   * 0.25;  // 0..25
        var transparencyComp = (9  - s.Transparency)  * 12.5   * 0.25;  // 0..25
        // Visibility: 1.0 when clear, ~0.11 when fully overcast. Gates the
        // seeing/transparency contribution so a cloudy slot with great
        // seeing can't sneak into "marginal" — it has to actually be
        // somewhat clear before atmospheric stability matters.
        var visibility = (10 - s.Cloudcover) / 9.0;
        var score = cloudComp + (seeingComp + transparencyComp) * visibility;

        var prec = (s.PrecType ?? "none").ToLowerInvariant();
        if (prec != "none" && prec != "") {
            return 0;
        }

        var rh = ParseRh(s.Rh2m);
        if (rh > 95) {
            score *= 0.3;
        }

        return (int)Math.Round(Math.Clamp(score, 0, 100));
    }

    private static int ParseRh(int rh2m) {
        // 7Timer rh2m is a bucket index from -4 (0% RH) up to 16 (99% RH).
        // Their docs describe it as a string but the live API returns it as
        // a number. Map linearly to an approximate percentage at the centre
        // of each bucket.
        return Math.Clamp((rh2m + 4) * 5, 0, 100);
    }

    private static DateTime ParseInitUtc(string? init) {
        // "init" is yyyymmddhh in UTC.
        if (!string.IsNullOrEmpty(init) && init.Length == 10
            && DateTime.TryParseExact(init, "yyyyMMddHH",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var t)) {
            return DateTime.SpecifyKind(t, DateTimeKind.Utc);
        }
        return DateTime.UtcNow;
    }

    private static WeatherForecastDto Unavailable(string err) =>
        new(false, err, DateTime.UtcNow, Array.Empty<WeatherSlot>());

    private record CacheEntry(DateTime FetchedAt, WeatherForecastDto Forecast);
}

// ---------- Public DTOs (serialised to JSON for the browser) ----------

public record WeatherForecastDto(
    bool Available,
    string Error,
    DateTime InitUtc,
    IReadOnlyList<WeatherSlot> Slots);

public record WeatherSlot(
    DateTime UtcStart,
    int CloudCover,
    int Seeing,
    int Transparency,
    int LiftedIndex,
    double Temp2m,
    int Rh2m,
    double WindSpeed,
    string WindDirection,
    string PrecType,
    int ObservationScore);

// ---------- Internal 7Timer JSON shape ----------

public class SevenTimerRoot {
    [JsonPropertyName("product")]    public string?              Product    { get; set; }
    [JsonPropertyName("init")]       public string?              Init       { get; set; }
    [JsonPropertyName("dataseries")] public List<SevenTimerSlot>? Dataseries { get; set; }
}

public class SevenTimerSlot {
    [JsonPropertyName("timepoint")]    public int      Timepoint    { get; set; }
    [JsonPropertyName("cloudcover")]   public int      Cloudcover   { get; set; }
    [JsonPropertyName("seeing")]       public int      Seeing       { get; set; }
    [JsonPropertyName("transparency")] public int      Transparency { get; set; }
    [JsonPropertyName("lifted_index")] public int      LiftedIndex  { get; set; }
    [JsonPropertyName("rh2m")]         public int      Rh2m         { get; set; }
    [JsonPropertyName("temp2m")]       public double   Temp2m       { get; set; }
    [JsonPropertyName("prec_type")]    public string?  PrecType     { get; set; }
    [JsonPropertyName("wind10m")]      public SevenTimerWind? Wind10m { get; set; }
}

public class SevenTimerWind {
    [JsonPropertyName("direction")] public string Direction { get; set; } = "";
    [JsonPropertyName("speed")]     public int    Speed     { get; set; }
}
