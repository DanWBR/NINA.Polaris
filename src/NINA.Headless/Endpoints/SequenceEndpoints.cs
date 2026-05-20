namespace NINA.Headless.Endpoints;

public static class SequenceEndpoints
{
    public static void MapSequenceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/sequence");

        group.MapGet("/", () =>
        {
            return Results.Ok(new { items = Array.Empty<object>(), status = "idle" });
        });

        group.MapPost("/", () =>
        {
            return Results.Ok(new { message = "Sequence loaded" });
        });

        group.MapPost("/start", () => Results.Ok(new { status = "running" }));
        group.MapPost("/pause", () => Results.Ok(new { status = "paused" }));
        group.MapPost("/stop", () => Results.Ok(new { status = "stopped" }));

        group.MapGet("/status", () =>
        {
            return Results.Ok(new
            {
                status = "idle",
                currentItem = (string?)null,
                progress = 0.0,
                estimatedTimeRemaining = (string?)null
            });
        });
    }
}
