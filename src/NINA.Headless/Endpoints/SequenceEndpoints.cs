using NINA.Headless.Services;

namespace NINA.Headless.Endpoints;

public static class SequenceEndpoints {
    public static void MapSequenceEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/sequence");

        group.MapGet("/", (SequenceEngine engine) => {
            return Results.Ok(new {
                items = engine.Items.Select(i => new {
                    i.Name, i.Exposure, i.Gain, i.Binning, i.Count,
                    i.Filter, i.Ra, i.Dec
                }),
                state = engine.State.ToString().ToLowerInvariant()
            });
        });

        group.MapPost("/", (List<SequenceItem> items, SequenceEngine engine) => {
            try {
                engine.LoadSequence(items);
                return Results.Ok(new { message = "Sequence loaded", itemCount = items.Count });
            } catch (InvalidOperationException ex) {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        group.MapPost("/start", (SequenceEngine engine) => {
            engine.Start();
            return Results.Ok(new { state = engine.State.ToString().ToLowerInvariant() });
        });

        group.MapPost("/pause", (SequenceEngine engine) => {
            engine.Pause();
            return Results.Ok(new { state = engine.State.ToString().ToLowerInvariant() });
        });

        group.MapPost("/resume", (SequenceEngine engine) => {
            engine.Resume();
            return Results.Ok(new { state = engine.State.ToString().ToLowerInvariant() });
        });

        group.MapPost("/stop", (SequenceEngine engine) => {
            engine.Stop();
            return Results.Ok(new { state = engine.State.ToString().ToLowerInvariant() });
        });

        group.MapGet("/status", (SequenceEngine engine) => {
            return Results.Ok(engine.GetStatus());
        });

        group.MapPost("/items/add", (SequenceItem item, SequenceEngine engine) => {
            if (engine.State == SequenceState.Running)
                return Results.Conflict(new { error = "Cannot modify sequence while running" });

            engine.Items.Add(item);
            return Results.Ok(new { message = "Item added", itemCount = engine.Items.Count });
        });

        group.MapDelete("/items/{index:int}", (int index, SequenceEngine engine) => {
            if (engine.State == SequenceState.Running)
                return Results.Conflict(new { error = "Cannot modify sequence while running" });

            if (index < 0 || index >= engine.Items.Count)
                return Results.NotFound(new { error = "Item index out of range" });

            engine.Items.RemoveAt(index);
            return Results.Ok(new { message = "Item removed", itemCount = engine.Items.Count });
        });
    }
}
