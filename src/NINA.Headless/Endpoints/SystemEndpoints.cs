using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NINA.Headless.Endpoints;

public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/system");

        group.MapGet("/status", () =>
        {
            var process = Process.GetCurrentProcess();
            return Results.Ok(new
            {
                version = "0.1.0-alpha",
                platform = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                memoryMb = process.WorkingSet64 / (1024 * 1024),
                uptime = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).ToString(@"d\.hh\:mm\:ss"),
                dotnetVersion = RuntimeInformation.FrameworkDescription
            });
        });

        group.MapGet("/profiles", () =>
        {
            return Results.Ok(new { profiles = new[] { new { id = "default", name = "Default", active = true } } });
        });

        group.MapGet("/settings", () =>
        {
            return Results.Ok(new
            {
                observatoryLatitude = 0.0,
                observatoryLongitude = 0.0,
                observatoryAltitude = 0.0,
                imageFormat = "FITS",
                plateSolver = "ASTAP",
                indiHost = "localhost",
                indiPort = 7624
            });
        });
    }
}
