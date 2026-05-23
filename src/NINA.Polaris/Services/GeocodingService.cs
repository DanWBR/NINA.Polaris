using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Polaris.Services;

/// <summary>
/// Thin proxy in front of the Nominatim (OpenStreetMap) geocoding API.
/// Proxied via the backend so we can:
///   1. Send the required User-Agent header (Nominatim TOS).
///   2. Respect the 1 req/s rate limit centrally instead of relying on
///      every browser tab to behave.
///   3. Avoid CORS surprises when serving the Web UI off a different host.
///
/// We keep a single shared <see cref="HttpClient"/> and a tiny semaphore so
/// concurrent requests are funnelled through one-at-a-time.
/// </summary>
public class GeocodingService {
    private static readonly HttpClient Http = new() {
        Timeout = TimeSpan.FromSeconds(8)
    };
    private static readonly SemaphoreSlim RateLimit = new(1, 1);
    private static DateTime _lastRequest = DateTime.MinValue;
    private static readonly TimeSpan MinSpacing = TimeSpan.FromSeconds(1);

    private readonly ILogger<GeocodingService> _logger;

    public GeocodingService(ILogger<GeocodingService> logger) {
        _logger = logger;
        // Set User-Agent once. Nominatim TOS requires a unique, identifiable
        // UA per application.
        if (!Http.DefaultRequestHeaders.UserAgent.Any()) {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "NINA-Headless/0.1 (https://github.com/DanWBR/nina-polaris)");
        }
    }

    public async Task<List<GeocodingResult>> SearchAsync(string query, int limit = 5, CancellationToken ct = default) {
        if (string.IsNullOrWhiteSpace(query)) return new();

        await RateLimit.WaitAsync(ct);
        try {
            // Enforce 1 req/s minimum spacing across all callers
            var since = DateTime.UtcNow - _lastRequest;
            if (since < MinSpacing) {
                await Task.Delay(MinSpacing - since, ct);
            }

            var url = "https://nominatim.openstreetmap.org/search" +
                      $"?q={Uri.EscapeDataString(query)}" +
                      "&format=json" +
                      $"&limit={Math.Clamp(limit, 1, 20)}" +
                      "&addressdetails=1";

            using var resp = await Http.GetAsync(url, ct);
            _lastRequest = DateTime.UtcNow;
            if (!resp.IsSuccessStatusCode) {
                _logger.LogWarning("Nominatim returned {Status} for query {Query}", resp.StatusCode, query);
                return new();
            }
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var raw = await JsonSerializer.DeserializeAsync<List<NominatimResult>>(stream, cancellationToken: ct);
            if (raw == null) return new();
            return raw.Select(r => new GeocodingResult {
                DisplayName = r.DisplayName,
                Latitude = double.TryParse(r.Lat, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var la) ? la : 0,
                Longitude = double.TryParse(r.Lon, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var lo) ? lo : 0,
                Type = r.Type,
                Class = r.Class,
                Importance = r.Importance
            }).ToList();
        } catch (HttpRequestException ex) {
            _logger.LogWarning(ex, "Nominatim unreachable");
            throw new InvalidOperationException("Geocoding service unreachable. Check internet connection.");
        } catch (TaskCanceledException) {
            throw new TimeoutException("Geocoding request timed out");
        } finally {
            RateLimit.Release();
        }
    }
}

public class GeocodingResult {
    public string DisplayName { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? Type { get; set; }
    public string? Class { get; set; }
    public double Importance { get; set; }
}

internal class NominatimResult {
    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "";
    [JsonPropertyName("lat")]
    public string Lat { get; set; } = "0";
    [JsonPropertyName("lon")]
    public string Lon { get; set; } = "0";
    [JsonPropertyName("type")]
    public string? Type { get; set; }
    [JsonPropertyName("class")]
    public string? Class { get; set; }
    [JsonPropertyName("importance")]
    public double Importance { get; set; }
}
