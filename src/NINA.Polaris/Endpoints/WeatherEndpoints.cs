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

using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

public static class WeatherEndpoints {
    public static void MapWeatherEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/weather");

        group.MapGet("/status", (EquipmentManager equip) => {
            if (equip.Weather == null)
                return Results.Ok(new {
                    connected = false,
                    safe = false
                });

            return Results.Ok(new {
                connected = equip.Weather.IsConnected,
                name = equip.Weather.DeviceName,
                temperature = Safe(equip.Weather.Temperature),
                humidity = Safe(equip.Weather.Humidity),
                dewPoint = Safe(equip.Weather.DewPoint),
                windSpeed = Safe(equip.Weather.WindSpeed),
                windGust = Safe(equip.Weather.WindGust),
                pressure = Safe(equip.Weather.Pressure),
                cloudCover = Safe(equip.Weather.CloudCover),
                rainRate = Safe(equip.Weather.RainRate),
                skyQuality = Safe(equip.Weather.SkyQuality),
                safe = equip.Weather.IsSafe
            });
        });

        group.MapPost("/refresh", async (EquipmentManager equip) => {
            if (equip.Weather == null)
                return Results.BadRequest(new { error = "No weather device selected" });

            await equip.Weather.RefreshAsync();
            return Results.Ok(new { status = "refreshing" });
        });

        group.MapPost("/select/{deviceName}", (EquipmentManager equip, string deviceName) => {
            equip.SelectWeather(deviceName);
            return Results.Ok(new { selected = deviceName });
        });

        group.MapPost("/connect", async (EquipmentManager equip) => {
            if (equip.Weather == null)
                return Results.BadRequest(new { error = "No weather device selected" });

            await equip.Weather.ConnectAsync();
            return Results.Ok(new { status = "connected", device = equip.Weather.DeviceName });
        });

        group.MapPost("/disconnect", async (EquipmentManager equip) => {
            if (equip.Weather == null)
                return Results.BadRequest(new { error = "No weather device selected" });

            await equip.Weather.DisconnectAsync();
            return Results.Ok(new { status = "disconnected" });
        });

        // 7Timer astronomical forecast (3-day, 3-hour slots). Lat/lon may be
        // given in the query to serve a different site; when omitted they fall
        // back to the active profile's observer location, so callers that don't
        // know the coordinates (e.g. the assistant) still get a forecast.
        // Backend caches per coord for 15 minutes, so even a tab refresh loop
        // won't hammer 7Timer.
        group.MapGet("/forecast", async (
            WeatherForecastService svc,
            ProfileService profile,
            double? lat,
            double? lon,
            CancellationToken ct) => {
            var la = lat ?? profile.Active.Latitude;
            var lo = lon ?? profile.Active.Longitude;
            if (la is < -90 or > 90 || lo is < -180 or > 180) {
                return Results.BadRequest(new { error = "lat must be in [-90, 90] and lon in [-180, 180]" });
            }
            var forecast = await svc.GetForecastAsync(la, lo, ct);
            return Results.Ok(forecast);
        });
    }

    static double? Safe(double v) => double.IsNaN(v) || double.IsInfinity(v) ? null : v;
}