using NINA.Headless.Services;

namespace NINA.Headless.Endpoints;

public static class StellariumEndpoints {
    public static void MapStellariumEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/stellarium");

        group.MapGet("/target", async (string? host, int? port, StellariumClient client) => {
            var h = string.IsNullOrWhiteSpace(host) ? "localhost" : host!;
            var p = port ?? 8090;
            try {
                var target = await client.GetSelectedObjectAsync(h, p);
                if (target == null) return Results.NotFound(new { error = "No object currently selected in Stellarium" });
                return Results.Ok(target);
            } catch (TimeoutException ex) {
                return Results.Problem(ex.Message);
            } catch (InvalidOperationException ex) {
                return Results.Problem(ex.Message);
            }
        });

        group.MapGet("/view", async (string? host, int? port, StellariumClient client) => {
            var h = string.IsNullOrWhiteSpace(host) ? "localhost" : host!;
            var p = port ?? 8090;
            var view = await client.GetViewAsync(h, p);
            if (view == null) return Results.NotFound(new { error = "Stellarium view query failed" });
            return Results.Ok(view);
        });
    }
}
