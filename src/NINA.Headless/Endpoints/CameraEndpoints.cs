namespace NINA.Headless.Endpoints;

public static class CameraEndpoints
{
    public static void MapCameraEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/camera");

        group.MapPost("/capture", (CaptureRequest request) =>
        {
            return Results.Accepted(value: new
            {
                jobId = Guid.NewGuid().ToString("N"),
                status = "pending",
                request.Exposure,
                request.Gain,
                request.Binning,
                request.Filter
            });
        });

        group.MapGet("/status", () =>
        {
            return Results.Ok(new
            {
                state = "idle",
                temperature = double.NaN,
                coolerOn = false,
                exposureProgress = 0.0
            });
        });

        group.MapGet("/image/latest/stats", () =>
        {
            return Results.Ok(new
            {
                starCount = 0,
                hfr = 0.0,
                mean = 0.0,
                median = 0.0,
                mad = 0.0,
                min = 0,
                max = 0
            });
        });
    }

    public record CaptureRequest(double Exposure = 1.0, int Gain = 100, int Binning = 1, string? Filter = null);
}
