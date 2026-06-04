using NINA.Polaris.Services.Studio;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// Read-only optical diagnostics for a single FITS frame, used by the
/// STUDIO "Analyze" tool (Tilt + Aberration). Synchronous: one frame
/// detects + analyses in ~1-2 s on a Pi 5, so there's no async-job dance
/// like GraXpert. Nothing is written to disk, so no FrameLibrary rescan.
/// </summary>
public static class AnalysisEndpoints {
    public static void MapAnalysisEndpoints(this IEndpointRouteBuilder app) {
        var g = app.MapGroup("/api/analysis");

        g.MapPost("/frame", (FrameAnalysisService svc, AnalysisRequest req) => {
            if (string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new { error = "path is required" });
            try {
                return Results.Ok(svc.Analyze(req.Path));
            } catch (FileNotFoundException) {
                return Results.NotFound(new { error = "File not found" });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    public record AnalysisRequest(string Path);
}
