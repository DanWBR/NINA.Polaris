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

namespace NINA.Polaris.Services;

/// <summary>
/// Place search for the Observatory card: the bundled town index first, then
/// Nominatim (OpenStreetMap) when the host can reach it.
///
/// The bundled <see cref="CityGazetteer"/> answers offline, which is the state
/// a rig at a dark site is in, and does so in a few milliseconds. Nominatim adds
/// street-level and landmark results when there is internet; it is proxied via
/// the backend so we can:
///   1. Send the required User-Agent header (Nominatim TOS).
///   2. Respect the 1 req/s rate limit centrally instead of relying on
///      every browser tab to behave.
///   3. Avoid CORS surprises when serving the Web UI off a different host.
///
/// We keep a single shared <see cref="HttpClient"/> and a tiny semaphore so
/// concurrent requests are funnelled through one-at-a-time. Nominatim being
/// unreachable is only an error when the local index had nothing either.
/// </summary>
public class GeocodingService {
    private static readonly HttpClient Http = new() {
        Timeout = TimeSpan.FromSeconds(8)
    };
    private static readonly SemaphoreSlim RateLimit = new(1, 1);
    private static DateTime _lastRequest = DateTime.MinValue;
    private static readonly TimeSpan MinSpacing = TimeSpan.FromSeconds(1);

    private readonly ILogger<GeocodingService> _logger;
    private readonly CityGazetteer _gazetteer;

    public GeocodingService(ILogger<GeocodingService> logger, CityGazetteer? gazetteer = null) {
        _logger = logger;
        _gazetteer = gazetteer ?? new CityGazetteer();
        // Set User-Agent once. Nominatim TOS requires a unique, identifiable
        // UA per application.
        if (!Http.DefaultRequestHeaders.UserAgent.Any()) {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "NINA-Headless/0.1 (https://github.com/DanWBR/nina-polaris)");
        }
    }

    public async Task<List<GeocodingResult>> SearchAsync(string query, int limit = 5, CancellationToken ct = default) {
        if (string.IsNullOrWhiteSpace(query)) return new();
        limit = Math.Clamp(limit, 1, 20);

        var local = SearchLocal(query, limit);
        List<GeocodingResult> remote;
        try {
            remote = await SearchNominatimAsync(query, limit, ct);
        } catch (Exception) when (local.Count > 0) {
            // Offline, or Nominatim is down: the bundled towns are the answer.
            return local;
        }
        return Merge(local, remote, limit);
    }

    /// <summary>The bundled town index, as geocoding results.</summary>
    public List<GeocodingResult> SearchLocal(string query, int limit) {
        try {
            return _gazetteer.Search(query, limit).Select(c => new GeocodingResult {
                DisplayName = c.DisplayName,
                Latitude = c.Latitude,
                Longitude = c.Longitude,
                Type = "city",
                Class = "place",
                Importance = Math.Min(1.0, Math.Log10(Math.Max(c.Population, 1)) / 8.0),
                Source = "bundled"
            }).ToList();
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Bundled town index unavailable");
            return new();
        }
    }

    /// <summary>Local results first, then the remote ones that are not the
    /// same place: Nominatim returns the town too, a few hundred metres off
    /// our centroid, and two cards for one town reads as a glitch.</summary>
    internal static List<GeocodingResult> Merge(List<GeocodingResult> local, List<GeocodingResult> remote, int limit) {
        var merged = new List<GeocodingResult>(local);
        foreach (var r in remote) {
            if (merged.Count >= limit) break;
            bool dup = merged.Any(m =>
                Math.Abs(m.Latitude - r.Latitude) < 0.03 && Math.Abs(m.Longitude - r.Longitude) < 0.03);
            if (!dup) merged.Add(r);
        }
        return merged;
    }

    private async Task<List<GeocodingResult>> SearchNominatimAsync(string query, int limit, CancellationToken ct) {
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
                Importance = r.Importance,
                Source = "nominatim"
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
    /// <summary>"bundled" (offline town index) or "nominatim".</summary>
    public string Source { get; set; } = "";
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