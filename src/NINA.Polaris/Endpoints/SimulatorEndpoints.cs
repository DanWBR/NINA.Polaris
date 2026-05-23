using NINA.Polaris.Services;
using NINA.Polaris.Services.Simulator;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// REST surface for the built-in equipment simulator (SIM plan).
///
/// <c>GET /status</c> + <c>POST /detect</c> are cheap and safe to
/// call without permission; the launch / shutdown / settings PUT
/// mutate state. All endpoints route through <see cref="SimulatorService"/>
/// which serialises across a SemaphoreSlim, so click-spamming the
/// UI buttons can't race the subprocess lifecycle.
/// </summary>
public static class SimulatorEndpoints {
    public static void MapSimulatorEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/simulator");

        g.MapGet("/status", (SimulatorService sim)
            => Results.Ok(sim.GetStatus()));

        g.MapPost("/detect", async (SimulatorService sim) => {
            var install = await sim.RefreshDetectionAsync();
            return Results.Ok(install);
        });

        g.MapPost("/launch", async (LaunchRequest req, SimulatorService sim) => {
            var devices = req.Devices ?? SimulatorDeviceTags.Defaults;
            var port = req.Port > 0 ? req.Port : 7624;
            var ok = await sim.LaunchAsync(devices, port);
            return ok
                ? Results.Ok(new { launched = true, devices, port })
                : Results.Conflict(new { launched = false, error = sim.LastError ?? "Launch failed" });
        });

        g.MapPost("/shutdown", async (SimulatorService sim) => {
            await sim.ShutdownAsync();
            return Results.Ok(new { shutdown = true });
        });

        g.MapGet("/settings", (ProfileService profiles) => Results.Ok(new SimulatorSettings(
            AutoStart: profiles.Active.SimulatorAutoStart,
            Devices: profiles.Active.SimulatorDevices ?? new List<string>(SimulatorDeviceTags.Defaults),
            Port: profiles.Active.SimulatorPort > 0 ? profiles.Active.SimulatorPort : 7624)));

        g.MapPut("/settings", (SimulatorSettings req, ProfileService profiles) => {
            profiles.Active.SimulatorAutoStart = req.AutoStart;
            if (req.Devices != null && req.Devices.Count > 0) {
                // Filter against the known-tag whitelist so a malicious
                // or buggy client can't sneak a free-form string into
                // the persisted profile (where it would also reach
                // IndiSimulatorBackend's process spawn args).
                var known = SimulatorDeviceTags.All.ToHashSet(StringComparer.OrdinalIgnoreCase);
                profiles.Active.SimulatorDevices = req.Devices
                    .Where(d => known.Contains(d))
                    .Select(d => d.ToLowerInvariant())
                    .Distinct()
                    .ToList();
            }
            if (req.Port > 0 && req.Port < 65536) profiles.Active.SimulatorPort = req.Port;
            profiles.Save();
            return Results.Ok(new { saved = true });
        });
    }

    /// <summary>POST /launch body.</summary>
    public record LaunchRequest(List<string>? Devices, int Port = 7624);

    /// <summary>GET / PUT /settings body. Matches UserProfile.Simulator*
    /// field names with a small bit of friendliness (Devices defaults
    /// when null on output).</summary>
    public record SimulatorSettings(
        bool AutoStart,
        List<string> Devices,
        int Port);
}
