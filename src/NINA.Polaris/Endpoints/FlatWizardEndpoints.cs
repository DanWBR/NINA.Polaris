using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

public static class FlatWizardEndpoints {
    public static void MapFlatWizardEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/flatwizard");

        group.MapGet("/status", (FlatWizardService fw) => Results.Ok(new {
            state = fw.State.ToString().ToLowerInvariant(),
            progress = fw.Progress,
            lastError = fw.LastError
        }));

        group.MapGet("/trained", (FlatWizardService fw) => Results.Ok(fw.TrainedExposures));

        group.MapPost("/start", (FlatWizardRequest request, FlatWizardService fw) => {
            try {
                fw.Start(request);
                return Results.Ok(new { state = "running" });
            } catch (InvalidOperationException ex) {
                return Results.Conflict(new { error = ex.Message });
            } catch (ArgumentException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/abort", (FlatWizardService fw) => {
            fw.Abort();
            return Results.Ok(new { state = fw.State.ToString().ToLowerInvariant() });
        });
    }
}
