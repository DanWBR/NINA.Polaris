namespace NINA.Headless.Endpoints;

public static class SkyEndpoints
{
    public static void MapSkyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/sky");

        group.MapPost("/slew-and-center", (SlewAndCenterRequest request) =>
        {
            var jobId = Guid.NewGuid().ToString("N");
            return Results.Accepted(value: new
            {
                jobId,
                target = new { request.Ra, request.Dec },
                toleranceArcsec = request.ToleranceArcsec,
                status = "pending"
            });
        });

        group.MapGet("/slew-and-center/{jobId}/status", (string jobId) =>
        {
            return Results.Ok(new
            {
                jobId,
                state = "idle",
                iteration = 0,
                errorArcsec = 0.0
            });
        });

        group.MapGet("/catalog/search", (string query) =>
        {
            return Results.Ok(new { query, results = Array.Empty<object>() });
        });

        group.MapGet("/fov", () =>
        {
            return Results.Ok(new
            {
                sensorWidthMm = 23.5,
                sensorHeightMm = 15.7,
                focalLengthMm = 478.0,
                fovWidthDeg = 2.82,
                fovHeightDeg = 1.88,
                scaleArcsecPerPixel = 1.55
            });
        });
    }

    public record SlewAndCenterRequest(double Ra, double Dec, double ToleranceArcsec = 30.0);
}
