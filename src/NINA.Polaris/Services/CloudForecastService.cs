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

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Polaris.Services;

/// <summary>
/// Hourly cloud forecast from Open-Meteo (https://open-meteo.com/), free and
/// keyless: total, low, mid and high cloud cover plus the humidity, dew point,
/// wind and rain chance that decide a night. 7Timer (WeatherForecastService)
/// only resolves cloud cover in 3-hour slots, too coarse to plan a session.
///
/// Proxied for the same reasons as 7Timer: one call per site, model and half
/// hour however many clients are open, a forecast for a tablet on the board's
/// hotspot as long as the board has internet, and failures as a DTO rather
/// than a raw fetch error.
///
/// One day back and three ahead, so a night that began before midnight UTC
/// still has its first hours.
/// </summary>
public class CloudForecastService {
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    /// <summary>The models offered: Open-Meteo's own blend and three globals.</summary>
    public static readonly IReadOnlyList<string> Models = ["best_match", "ecmwf_ifs025", "gfs_seamless", "icon_seamless"];

    private readonly ILogger<CloudForecastService> _logger;
    private readonly ConcurrentDictionary<string, (DateTime At, CloudForecastDto Forecast)> _cache = new();

    public CloudForecastService(ILogger<CloudForecastService> logger) {
        _logger = logger;
        if (!Http.DefaultRequestHeaders.UserAgent.Any())
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("NINA-Polaris/0.1 (https://github.com/DanWBR/nina-polaris)");
    }

    /// <summary>Never throws: any failure comes back with Available=false.</summary>
    public async Task<CloudForecastDto> GetAsync(double lat, double lon, string? model, CancellationToken ct = default) {
        model = Models.Contains(model ?? "") ? model! : Models[0];
        var key = $"{lat:F2},{lon:F2},{model}";
        if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheTtl) return hit.Forecast;

        try {
            var inv = CultureInfo.InvariantCulture;
            var url = "https://api.open-meteo.com/v1/forecast"
                + $"?latitude={lat.ToString(inv)}&longitude={lon.ToString(inv)}"
                + "&hourly=cloud_cover,cloud_cover_low,cloud_cover_mid,cloud_cover_high,"
                + "precipitation_probability,relative_humidity_2m,dew_point_2m,temperature_2m,wind_speed_10m"
                + $"&timezone=GMT&past_days=1&forecast_days=3&models={model}";
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) {
                _logger.LogWarning("Open-Meteo returned {Status} for {Key}", resp.StatusCode, key);
                return Unavailable(model, $"Forecast service returned {(int)resp.StatusCode}");
            }
            var raw = await JsonSerializer.DeserializeAsync<OpenMeteoRoot>(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var forecast = Parse(raw, model);
            if (forecast.Available) _cache[key] = (DateTime.UtcNow, forecast);
            return forecast;
        } catch (HttpRequestException ex) {
            _logger.LogWarning(ex, "Open-Meteo unreachable for {Key}", key);
            return Unavailable(model, "Forecast service unreachable");
        } catch (TaskCanceledException) {
            return Unavailable(model, "Forecast request timed out");
        } catch (JsonException ex) {
            _logger.LogWarning(ex, "Open-Meteo response malformed for {Key}", key);
            return Unavailable(model, "Forecast response could not be parsed");
        }
    }

    /// <summary>Hours from Open-Meteo's column arrays. Times are GMT without
    /// an offset (timezone=GMT); a model with no value for an hour sends null.</summary>
    internal static CloudForecastDto Parse(OpenMeteoRoot? raw, string model) {
        var h = raw?.Hourly;
        if (h?.Time == null || h.Time.Count == 0) return Unavailable(model, "Forecast response was empty");
        static double? At(List<double?>? col, int i) => col != null && i < col.Count ? col[i] : null;
        var hours = new List<CloudHour>(h.Time.Count);
        for (int i = 0; i < h.Time.Count; i++) {
            if (!DateTime.TryParse(h.Time[i], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)) continue;
            hours.Add(new CloudHour(t, At(h.CloudCover, i), At(h.CloudCoverLow, i), At(h.CloudCoverMid, i),
                At(h.CloudCoverHigh, i), At(h.PrecipitationProbability, i), At(h.RelativeHumidity, i),
                At(h.DewPoint, i), At(h.Temperature, i), At(h.WindSpeed, i)));
        }
        return hours.Count == 0 ? Unavailable(model, "Forecast response had no usable hours")
            : new CloudForecastDto(true, "", model, hours);
    }

    private static CloudForecastDto Unavailable(string model, string error) => new(false, error, model, []);

    internal sealed class OpenMeteoRoot {
        [JsonPropertyName("hourly")] public OpenMeteoHourly? Hourly { get; set; }
    }

    internal sealed class OpenMeteoHourly {
        [JsonPropertyName("time")] public List<string>? Time { get; set; }
        [JsonPropertyName("cloud_cover")] public List<double?>? CloudCover { get; set; }
        [JsonPropertyName("cloud_cover_low")] public List<double?>? CloudCoverLow { get; set; }
        [JsonPropertyName("cloud_cover_mid")] public List<double?>? CloudCoverMid { get; set; }
        [JsonPropertyName("cloud_cover_high")] public List<double?>? CloudCoverHigh { get; set; }
        [JsonPropertyName("precipitation_probability")] public List<double?>? PrecipitationProbability { get; set; }
        [JsonPropertyName("relative_humidity_2m")] public List<double?>? RelativeHumidity { get; set; }
        [JsonPropertyName("dew_point_2m")] public List<double?>? DewPoint { get; set; }
        [JsonPropertyName("temperature_2m")] public List<double?>? Temperature { get; set; }
        [JsonPropertyName("wind_speed_10m")] public List<double?>? WindSpeed { get; set; }
    }
}

/// <summary>One forecast hour. Cloud cover and rain chance in %, temperatures
/// in °C, wind in km/h; null where the model has no value.</summary>
public record CloudHour(DateTime UtcStart, double? CloudCover, double? Low, double? Mid, double? High,
    double? PrecipProbability, double? Humidity, double? DewPoint, double? Temperature, double? WindSpeed);

public record CloudForecastDto(bool Available, string Error, string Model, List<CloudHour> Hours);
