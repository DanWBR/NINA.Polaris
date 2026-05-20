using NINA.Headless.Services;

namespace NINA.Headless.Endpoints;

public static class AutoFocusEndpoints {
    public static void MapAutoFocusEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/autofocus");

        group.MapGet("/status", (AutoFocusService af) => {
            return Results.Ok(new {
                state = af.State.ToString().ToLowerInvariant(),
                progress = new {
                    steps = af.Progress.Steps,
                    currentSampleIndex = af.Progress.CurrentSampleIndex,
                    currentPosition = af.Progress.CurrentPosition,
                    lastHfr = af.Progress.LastHfr,
                    lastStarCount = af.Progress.LastStarCount,
                    points = af.Progress.Points,
                    startedAt = af.Progress.StartedAt
                },
                lastError = af.LastError
            });
        });

        group.MapGet("/result", (AutoFocusService af) => {
            if (af.LastResult == null) return Results.Ok(new { hasResult = false });
            return Results.Ok(new { hasResult = true, result = af.LastResult });
        });

        group.MapPost("/start", (AutoFocusRequest? request, AutoFocusService af) => {
            try {
                af.Start(request ?? new AutoFocusRequest());
                return Results.Ok(new { state = "running" });
            } catch (InvalidOperationException ex) {
                return Results.Conflict(new { error = ex.Message });
            } catch (ArgumentException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/abort", (AutoFocusService af) => {
            af.Abort();
            return Results.Ok(new { state = af.State.ToString().ToLowerInvariant() });
        });
    }
}
