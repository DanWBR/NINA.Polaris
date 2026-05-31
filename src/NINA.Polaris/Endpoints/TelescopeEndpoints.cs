using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

public static class TelescopeEndpoints {
    public static void MapTelescopeEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/telescope");

        group.MapGet("/status", (EquipmentManager equip) => {
            if (equip.Telescope == null)
                return Results.Ok(new {
                    connected = false, tracking = false,
                    ra = 0.0, dec = 0.0, alt = 0.0, az = 0.0,
                    pierSide = "unknown", slewing = false, parked = false
                });

            return Results.Ok(new {
                connected = equip.Telescope.IsConnected,
                tracking = equip.Telescope.IsTracking,
                ra = equip.Telescope.RightAscension,
                dec = equip.Telescope.Declination,
                alt = equip.Telescope.Altitude,
                az = equip.Telescope.Azimuth,
                pierSide = equip.Telescope.SideOfPier.ToString(),
                slewing = equip.Telescope.IsSlewing,
                parked = equip.Telescope.IsParked
            });
        });

        group.MapPost("/slew", async (EquipmentManager equip, SlewRequest request) => {
            if (equip.Telescope == null)
                return Results.BadRequest(new { error = "No telescope selected" });

            try {
                await equip.Telescope.SlewAsync(request.Ra, request.Dec);
                return Results.Ok(new {
                    status = "slewing",
                    target = new { request.Ra, request.Dec }
                });
            } catch (Exception ex) {
                return Results.Problem(ex.Message);
            }
        });

        group.MapPost("/sync", async (EquipmentManager equip, SlewRequest request) => {
            if (equip.Telescope == null)
                return Results.BadRequest(new { error = "No telescope selected" });

            await equip.Telescope.SyncAsync(request.Ra, request.Dec);
            return Results.Ok(new { status = "synced", ra = request.Ra, dec = request.Dec });
        });

        group.MapPost("/park", async (EquipmentManager equip) => {
            if (equip.Telescope == null)
                return Results.BadRequest(new { error = "No telescope selected" });

            await equip.Telescope.ParkAsync();
            return Results.Ok(new { status = "parking" });
        });

        group.MapPost("/unpark", async (EquipmentManager equip) => {
            if (equip.Telescope == null)
                return Results.BadRequest(new { error = "No telescope selected" });

            await equip.Telescope.UnparkAsync();
            return Results.Ok(new { status = "unparking" });
        });

        // Drive the mount to its mechanical home position. Surfaces
        // NotSupportedException as a 501 + actionable error message
        // so the UI can toast "this mount doesn't support Find Home"
        // instead of failing silently. Most GEM and alt-az mounts
        // honour it via INDI TELESCOPE_HOME / Alpaca findhome.
        group.MapPost("/find-home", async (EquipmentManager equip) => {
            if (equip.Telescope == null)
                return Results.BadRequest(new { error = "No telescope selected" });
            try {
                await equip.Telescope.FindHomeAsync();
                return Results.Ok(new { status = "homing" });
            } catch (NotSupportedException ex) {
                return Results.Json(new { error = ex.Message }, statusCode: 501);
            } catch (Exception ex) {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        group.MapPost("/tracking", async (EquipmentManager equip, TrackingRequest request) => {
            if (equip.Telescope == null)
                return Results.BadRequest(new { error = "No telescope selected" });

            await equip.Telescope.SetTrackingAsync(request.Enabled);
            return Results.Ok(new { tracking = request.Enabled });
        });

        group.MapPost("/abort", async (EquipmentManager equip) => {
            if (equip.Telescope == null)
                return Results.BadRequest(new { error = "No telescope selected" });

            await equip.Telescope.AbortSlewAsync();
            return Results.Ok(new { status = "stopped" });
        });

        group.MapPost("/move/{direction}", async (EquipmentManager equip, string direction) => {
            if (equip.Telescope == null)
                return Results.BadRequest(new { error = "No telescope selected" });

            switch (direction.ToLowerInvariant()) {
                case "north": await equip.Telescope.MoveNorthAsync(); break;
                case "south": await equip.Telescope.MoveSouthAsync(); break;
                case "east": await equip.Telescope.MoveEastAsync(); break;
                case "west": await equip.Telescope.MoveWestAsync(); break;
                case "stop": await equip.Telescope.StopMotionAsync(); break;
                default: return Results.BadRequest(new { error = $"Unknown direction: {direction}" });
            }

            return Results.Ok(new { status = "moving", direction });
        });

        group.MapPost("/select/{deviceName}", (EquipmentManager equip, string deviceName, string? driver) => {
            // ?driver=indi (default) | synscan-wifi | nexstar-wifi |
            // lx200-tcp | alpaca. Legacy clients omit it and get INDI.
            try {
                equip.SelectTelescope(driver ?? "indi", deviceName);
                return Results.Ok(new {
                    selected = deviceName,
                    driver = driver ?? "indi"
                });
            } catch (NotSupportedException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Mount driver catalogue. Same shape as /api/camera/drivers.
        // INDI is always available; direct WiFi drivers (SynScan UDP,
        // NexStar TCP, LX200 TCP) advertise as "not installed" until
        // their backend lands, see docs/mounts-wifi.md.
        group.MapGet("/drivers", (EquipmentManager equip)
            => Results.Ok(equip.GetAvailableMountDrivers()));

        // Per-driver telescope discovery. INDI uses device names from
        // the active connection; ASCOM uses registered ProgIDs from
        // the local Windows registry. SynScan-WiFi is host:port-based
        // (no enumeration possible), so it returns empty here — the
        // user types the address directly.
        group.MapGet("/discover", (EquipmentManager equip, string? driver) => {
            var d = (driver ?? "indi").Trim().ToLowerInvariant();
            if (d == "ascom-com") {
                return Results.Ok(equip.GetAscomDrivers(
                    NINA.Ascom.Com.AscomComRegistry.DeviceType.Telescope));
            }
            if (d == "alpaca") {
                return Results.Ok(equip.GetDiscoveredTelescopesFor("alpaca"));
            }
            if (d == "indi") {
                return Results.Ok(equip.GetDeviceNames()
                    .Select(n => new DiscoveredCamera(n, n, n))
                    .ToList());
            }
            return Results.Ok(Array.Empty<DiscoveredCamera>());
        });

        group.MapPost("/connect", async (EquipmentManager equip) => {
            if (equip.Telescope == null)
                return Results.BadRequest(new { error = "No telescope selected" });

            await equip.Telescope.ConnectAsync();
            return Results.Ok(new { status = "connected", device = equip.Telescope.DeviceName });
        });

        group.MapPost("/disconnect", async (EquipmentManager equip) => {
            if (equip.Telescope == null)
                return Results.BadRequest(new { error = "No telescope selected" });

            await equip.Telescope.DisconnectAsync();
            return Results.Ok(new { status = "disconnected" });
        });
    }

    public record SlewRequest(double Ra, double Dec);
    public record TrackingRequest(bool Enabled);
}
