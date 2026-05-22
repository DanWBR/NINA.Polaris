using System.Diagnostics;
using System.Runtime.InteropServices;
using NINA.Headless.Services;

namespace NINA.Headless.Endpoints;

public static class SystemEndpoints {
    public static void MapSystemEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/system");

        group.MapGet("/geocode", async (string query, int? limit, GeocodingService geo) => {
            if (string.IsNullOrWhiteSpace(query))
                return Results.BadRequest(new { error = "query parameter required" });
            try {
                var results = await geo.SearchAsync(query, limit ?? 5);
                return Results.Ok(new {
                    query,
                    count = results.Count,
                    results
                });
            } catch (TimeoutException ex) {
                return Results.Problem(ex.Message, statusCode: 504);
            } catch (InvalidOperationException ex) {
                return Results.Problem(ex.Message, statusCode: 502);
            }
        });

        group.MapGet("/relay", (RelayClient relay) => Results.Ok(new {
            state = relay.State.ToString().ToLowerInvariant(),
            hostname = relay.AssignedHostname,
            lastError = relay.LastError
        }));

        group.MapGet("/status", (EquipmentManager equip) => {
            var process = Process.GetCurrentProcess();
            return Results.Ok(new {
                version = "0.1.0-alpha",
                platform = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                memoryMb = process.WorkingSet64 / (1024 * 1024),
                uptime = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).ToString(@"d\.hh\:mm\:ss"),
                dotnetVersion = RuntimeInformation.FrameworkDescription,
                equipment = equip.GetEquipmentStatus()
            });
        });

        // Profiles
        group.MapGet("/profiles", (ProfileService profiles) => {
            var list = profiles.ListProfiles();
            return Results.Ok(new {
                active = profiles.Active.Name,
                profiles = list
            });
        });

        group.MapGet("/profile", (ProfileService profiles) => {
            return Results.Ok(profiles.Active);
        });

        group.MapPut("/profile", (UserProfile update, ProfileService profiles) => {
            profiles.UpdateSettings(p => {
                p.Latitude = update.Latitude;
                p.Longitude = update.Longitude;
                p.Altitude = update.Altitude;
                p.SensorWidthMm = update.SensorWidthMm;
                p.SensorHeightMm = update.SensorHeightMm;
                p.FocalLengthMm = update.FocalLengthMm;
                p.SensorPixelsX = update.SensorPixelsX;
                p.SensorPixelsY = update.SensorPixelsY;
                p.DefaultExposure = update.DefaultExposure;
                p.DefaultGain = update.DefaultGain;
                p.DefaultBinning = update.DefaultBinning;
                p.IndiHost = update.IndiHost;
                p.IndiPort = update.IndiPort;
                p.AstapPath = update.AstapPath;
                p.SolveToleranceArcsec = update.SolveToleranceArcsec;
                p.ImageOutputDir = update.ImageOutputDir;
                p.ImageNamePattern = update.ImageNamePattern;
                p.ImageFormat = update.ImageFormat;
                p.PreferAdvancedSequencer = update.PreferAdvancedSequencer;
                // External-tool path overrides. Empty/null = auto-detect.
                p.SirilPath = update.SirilPath;
                p.SirilScriptsDir = update.SirilScriptsDir;
                p.GraXpertPath = update.GraXpertPath;
                p.GraXpertBgeSmoothing = update.GraXpertBgeSmoothing;
                p.GraXpertBgeCorrection = update.GraXpertBgeCorrection
                                              ?? p.GraXpertBgeCorrection;
                p.GraXpertDeconStrength = update.GraXpertDeconStrength;
                p.GraXpertDeconPsfSize = update.GraXpertDeconPsfSize;
                p.GraXpertDenoiseStrength = update.GraXpertDenoiseStrength;
            });
            return Results.Ok(new { message = "Profile saved" });
        });

        group.MapPost("/profile/save-as", (SaveAsRequest request, ProfileService profiles) => {
            profiles.SaveAs(request.Name);
            return Results.Ok(new { message = $"Profile saved as '{request.Name}'" });
        });

        group.MapPost("/profile/load/{id}", (string id, ProfileService profiles) => {
            if (profiles.LoadProfile(id))
                return Results.Ok(new { message = "Profile loaded", name = profiles.Active.Name });
            return Results.NotFound(new { error = "Profile not found" });
        });

        // Legacy settings (redirect to profile)
        group.MapGet("/settings", (ProfileService profiles) => {
            var p = profiles.Active;
            return Results.Ok(new {
                observatoryLatitude = p.Latitude,
                observatoryLongitude = p.Longitude,
                observatoryAltitude = p.Altitude,
                sensorWidthMm = p.SensorWidthMm,
                sensorHeightMm = p.SensorHeightMm,
                focalLengthMm = p.FocalLengthMm,
                imageFormat = p.ImageFormat,
                plateSolver = "ASTAP",
                indiHost = p.IndiHost,
                indiPort = p.IndiPort
            });
        });
    }

    record SaveAsRequest(string Name);
}
