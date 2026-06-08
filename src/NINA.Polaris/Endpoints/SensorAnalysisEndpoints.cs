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

/// <summary>
/// REST surface for the camera Sensor Analysis (Equipment camera card ->
/// Sensor analysis). Thin wrappers over <see cref="SensorAnalysisService"/>,
/// same shape as the benchmark endpoints.
/// </summary>
public static class SensorAnalysisEndpoints {
    public static void MapSensorAnalysisEndpoints(this IEndpointRouteBuilder app) {
        var group = app.MapGroup("/api/sensor-analysis");

        group.MapGet("/status", (SensorAnalysisService svc) => Results.Ok(svc.GetStatus()));

        group.MapPost("/run", (SensorAnalysisService svc, SensorAnalysisRequest? req) => {
            var error = svc.Start(req ?? new SensorAnalysisRequest());
            if (error != null) return Results.Json(new { error }, statusCode: 409);
            return Results.Ok(svc.GetStatus());
        });

        group.MapPost("/cancel", (SensorAnalysisService svc) => {
            svc.Cancel();
            return Results.Ok(new { cancelled = true });
        });

        // Saved run history (optionally filtered to one camera) + export +
        // clear, so a camera's measured curve survives restarts.
        group.MapGet("/history", (SensorAnalysisStore store, string? camera) =>
            Results.Ok(store.LoadHistory(camera)));

        group.MapGet("/latest", (SensorAnalysisStore store, string? camera) =>
            Results.Ok(string.IsNullOrWhiteSpace(camera) ? null : store.LatestForCamera(camera)));

        group.MapGet("/export", (SensorAnalysisStore store) =>
            Results.File(store.ExportAllJson(), "application/json", "polaris-sensor-analysis.json"));

        group.MapDelete("/history", (SensorAnalysisStore store) =>
            Results.Ok(new { cleared = store.ClearHistory() }));
    }
}