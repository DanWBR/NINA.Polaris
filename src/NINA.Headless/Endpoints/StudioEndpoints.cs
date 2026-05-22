using NINA.Headless.Services.Studio;

namespace NINA.Headless.Endpoints;

public static class StudioEndpoints {
    public static void MapStudioEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/studio");

        // Force re-walk of the active profile's image output dir. Runs
        // in the background — progress is exposed via /rescan/status and
        // (later) broadcast on the status WebSocket.
        g.MapPost("/rescan", (FrameLibraryService svc) => {
            _ = Task.Run(() => svc.RescanAsync());
            return Results.Accepted(value: new { status = "started" });
        });

        g.MapGet("/rescan/status", (FrameLibraryService svc) => Results.Ok(svc.Rescan));

        // Paginated frame list. All query params optional. Empty strings
        // are treated as "no filter" by the service.
        g.MapGet("/frames", (FrameLibraryService svc,
            string? type, string? filter, string? target,
            string? dateFrom, string? dateTo,
            int? limit, int? offset) => {
            var q = new FrameQuery(
                Type:     type,
                Filter:   filter,
                Target:   target,
                DateFrom: dateFrom,
                DateTo:   dateTo,
                Limit:    limit  ?? 100,
                Offset:   offset ?? 0);
            return Results.Ok(svc.Query(q));
        });

        g.MapGet("/frames/{id:int}", (FrameLibraryService svc, int id) => {
            var row = svc.GetById(id);
            return row == null ? Results.NotFound() : Results.Ok(row);
        });

        // Returns a JPEG thumbnail (256 px max side). Generated on first
        // request, cached to disk thereafter.
        g.MapGet("/frames/{id:int}/thumb", async (FrameLibraryService svc, int id, CancellationToken ct) => {
            var path = await svc.GetThumbnailAsync(id, ct);
            return path == null ? Results.NotFound() : Results.File(path, "image/jpeg");
        });

        // Aggregate stats — total light frames, total exposure (h),
        // distinct targets / filters. Used by the toolbar header.
        g.MapGet("/stats", (FrameLibraryService svc) => Results.Ok(svc.GetStats()));
    }
}
