using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// REST surface for the hardware benchmark (Settings -> Hardware
/// Benchmark). Start a run, poll status, read/clear/export the saved
/// history. The heavy work lives in <see cref="BenchmarkService"/>; these
/// are thin wrappers following the same group pattern as the other
/// endpoint files.
/// </summary>
public static class BenchmarkEndpoints {
    public static void MapBenchmarkEndpoints(this IEndpointRouteBuilder app) {
        var group = app.MapGroup("/api/benchmark");

        group.MapGet("/status", (BenchmarkService svc) => Results.Ok(svc.GetStatus()));

        group.MapPost("/run", (BenchmarkService svc, BenchmarkRequest? req) => {
            var error = svc.Start(req ?? new BenchmarkRequest());
            if (error != null) return Results.Json(new { error }, statusCode: 409);
            return Results.Ok(svc.GetStatus());
        });

        group.MapPost("/cancel", (BenchmarkService svc) => {
            svc.Cancel();
            return Results.Ok(new { cancelled = true });
        });

        group.MapGet("/history", (BenchmarkResultsStore store) =>
            Results.Ok(store.LoadHistory()));

        group.MapGet("/export", (BenchmarkResultsStore store) => {
            var bytes = store.ExportAllJson();
            return Results.File(bytes, "application/json", "polaris-benchmarks.json");
        });

        group.MapDelete("/history", (BenchmarkResultsStore store) =>
            Results.Ok(new { cleared = store.ClearHistory() }));
    }
}
