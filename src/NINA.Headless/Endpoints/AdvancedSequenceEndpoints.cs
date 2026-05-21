using NINA.Headless.Services.Sequencer;

namespace NINA.Headless.Endpoints;

public static class AdvancedSequenceEndpoints {
    public static void MapAdvancedSequenceEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/sequencer");

        // ---- Document IO ----
        g.MapGet("/document", (AdvancedSequenceEngine engine) => Results.Ok(new {
            engine.Document,
            engine.State,
            engine.LastError,
            engine.StartedAt,
            engine.FinishedAt,
            engine.AbortReason
        }));

        g.MapPost("/document", (SequenceDocument doc, AdvancedSequenceEngine engine) => {
            engine.Load(doc);
            return Results.Ok(new { loaded = true, validation = engine.Validate() });
        });

        // Convenience: round-trip JSON so the UI can save the current document
        // to disk via the browser's download dialog, or import a hand-edited file.
        g.MapGet("/document/json", (AdvancedSequenceEngine engine) =>
            Results.Text(SequenceJson.Serialize(engine.Document), "application/json"));

        g.MapPost("/document/json", async (HttpRequest req, AdvancedSequenceEngine engine) => {
            using var sr = new StreamReader(req.Body);
            var text = await sr.ReadToEndAsync();
            try {
                var doc = SequenceJson.Deserialize(text);
                engine.Load(doc);
                return Results.Ok(new { loaded = true, validation = engine.Validate() });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // ---- Lifecycle ----
        g.MapPost("/start", (AdvancedSequenceEngine engine) => {
            engine.Start();
            return Results.Ok(new { state = engine.State.ToString(), error = engine.LastError });
        });

        g.MapPost("/stop", (AdvancedSequenceEngine engine) => {
            engine.Stop();
            return Results.Ok(new { state = engine.State.ToString() });
        });

        g.MapPost("/validate", (AdvancedSequenceEngine engine) =>
            Results.Ok(new { errors = engine.Validate() }));

        // ---- Palette ----
        g.MapGet("/types", () => Results.Ok(SequenceEntityJsonConverter.KnownTypes.Select(t => new {
            type = t.Type, category = t.Category, kind = t.Class
        })));

        // ---- Templates ----
        g.MapGet("/templates", (SequenceTemplateStore store) => Results.Ok(new {
            dir = store.Dir,
            templates = store.List().ToArray()
        }));

        g.MapGet("/templates/{name}", (string name, SequenceTemplateStore store) => {
            var doc = store.Load(name);
            return doc == null ? Results.NotFound() : Results.Ok(doc);
        });

        g.MapPost("/templates/{name}", (string name, SequenceDocument doc, SequenceTemplateStore store) => {
            store.Save(name, doc);
            return Results.Ok(new { saved = true });
        });

        g.MapDelete("/templates/{name}", (string name, SequenceTemplateStore store) => {
            return store.Delete(name) ? Results.Ok(new { deleted = true }) : Results.NotFound();
        });
    }
}
