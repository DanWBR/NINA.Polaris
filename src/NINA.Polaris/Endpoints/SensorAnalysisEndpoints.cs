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
