using NINA.Headless.Services;

namespace NINA.Headless.Endpoints;

public static class LiveStackEndpoints {
    public static void MapLiveStackEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/livestack");

        group.MapPost("/start", (LiveStackingService stack) => {
            stack.Start();
            return Results.Ok(new { status = "started" });
        });

        group.MapPost("/stop", (LiveStackingService stack) => {
            stack.Stop();
            return Results.Ok(new { status = "stopped", frameCount = stack.FrameCount });
        });

        group.MapPost("/reset", (LiveStackingService stack) => {
            stack.Reset();
            return Results.Ok(new { status = "reset" });
        });

        group.MapGet("/status", (LiveStackingService stack) => {
            return Results.Ok(stack.GetStatus());
        });

        group.MapGet("/preview", (LiveStackingService stack, ImageRelayService relay, int? quality) => {
            var jpeg = relay.GetLatestJpeg(quality ?? 85);
            if (jpeg == null)
                return Results.NotFound(new { error = "No stacked image available" });
            return Results.File(jpeg, "image/jpeg");
        });
    }
}
