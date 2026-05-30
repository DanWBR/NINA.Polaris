namespace NINA.Polaris.Endpoints;

/// <summary>
/// Endpoints specific to the ASCOM Platform COM-interop adapter. The
/// per-device routes (/api/camera, /api/telescope, /api/focuser,
/// /api/filterwheel) already cover select / connect / disconnect for
/// every backend; this group is for ASCOM-only actions that don't
/// fit those (running the driver's SetupDialog, platform-presence
/// probes, etc.).
/// </summary>
public static class AscomEndpoints {
    public static void MapAscomEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/ascom");

        // True when the ASCOM Platform is installed AND at least one
        // driver of ANY device type is registered. Used by the RIGS UI
        // to decide whether to render "ASCOM (COM)" entries in the
        // driver-source dropdowns at all. Cheap registry probe, never
        // throws, returns false on non-Windows.
        group.MapGet("/status", () => {
            if (!OperatingSystem.IsWindows()) {
                return Results.Ok(new {
                    supported = false,
                    platformInstalled = false,
                    reason = "ASCOM COM-interop is Windows-only."
                });
            }
            return AscomStatus();
        });

        // Open the driver's modal SetupDialog. Blocks until the user
        // dismisses the form, the body of the response carries the
        // outcome. UI shows a spinner / disables Connect while the
        // request is in flight.
        group.MapPost("/setup/{progId}", async (string progId) => {
            if (!OperatingSystem.IsWindows())
                return Results.BadRequest(new { error = "ASCOM is Windows-only." });
            try {
                await OpenSetup(progId);
                return Results.Ok(new { progId, opened = true });
            } catch (Exception ex) {
                return Results.BadRequest(new {
                    progId,
                    error = ex.Message,
                    hint = "SetupDialog requires Polaris to run in an interactive Windows session (not as a service)."
                });
            }
        });
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IResult AscomStatus() => Results.Ok(new {
        supported = true,
        platformInstalled = NINA.Ascom.Com.AscomComRegistry.IsPlatformInstalled(),
    });

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Task OpenSetup(string progId)
        => NINA.Ascom.Com.AscomComSetup.OpenSetupDialogAsync(progId);
}
