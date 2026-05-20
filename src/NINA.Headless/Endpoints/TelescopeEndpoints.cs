namespace NINA.Headless.Endpoints;

public static class TelescopeEndpoints
{
    public static void MapTelescopeEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/telescope");

        group.MapGet("/status", () =>
        {
            return Results.Ok(new
            {
                connected = false,
                tracking = false,
                ra = 0.0,
                dec = 0.0,
                alt = 0.0,
                az = 0.0,
                pierSide = "unknown",
                slewState = "idle"
            });
        });

        group.MapPost("/slew", (SlewRequest request) =>
        {
            return Results.Accepted(value: new
            {
                jobId = Guid.NewGuid().ToString("N"),
                target = new { request.Ra, request.Dec },
                status = "pending"
            });
        });

        group.MapPost("/park", () => Results.Ok(new { status = "parking" }));
        group.MapPost("/unpark", () => Results.Ok(new { status = "unparking" }));
        group.MapPost("/tracking", (TrackingRequest request) => Results.Ok(new { tracking = request.Enabled }));
    }

    public record SlewRequest(double Ra, double Dec);
    public record TrackingRequest(bool Enabled);
}
