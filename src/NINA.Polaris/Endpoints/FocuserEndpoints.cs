using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

public static class FocuserEndpoints {
    public static void MapFocuserEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/focuser");

        group.MapGet("/status", (EquipmentManager equip) => {
            if (equip.Focuser == null)
                return Results.Ok(new {
                    connected = false,
                    position = 0,
                    temperature = (double?)null,
                    maxPosition = 0,
                    moving = false
                });

            var temp = equip.Focuser.Temperature;
            return Results.Ok(new {
                connected = true,
                position = equip.Focuser.Position,
                temperature = double.IsNaN(temp) ? (double?)null : temp,
                maxPosition = equip.Focuser.MaxPosition,
                moving = equip.Focuser.IsMoving
            });
        });

        group.MapPost("/move/absolute", async (EquipmentManager equip, MoveAbsoluteRequest request) => {
            if (equip.Focuser == null)
                return Results.BadRequest(new { error = "No focuser selected" });

            await equip.Focuser.MoveAbsoluteAsync(request.Position);
            return Results.Ok(new { status = "moving", target = request.Position });
        });

        group.MapPost("/move/relative", async (EquipmentManager equip, MoveRelativeRequest request) => {
            if (equip.Focuser == null)
                return Results.BadRequest(new { error = "No focuser selected" });

            await equip.Focuser.MoveRelativeAsync(request.Steps);
            return Results.Ok(new { status = "moving", steps = request.Steps });
        });

        group.MapPost("/abort", async (EquipmentManager equip) => {
            if (equip.Focuser == null)
                return Results.BadRequest(new { error = "No focuser selected" });

            await equip.Focuser.AbortAsync();
            return Results.Ok(new { status = "stopped" });
        });

        group.MapPost("/select/{deviceName}", (EquipmentManager equip, string deviceName, string? driver) => {
            // ?driver=indi (default) | ascom-com. Legacy clients omit
            // it and get INDI for backwards compatibility.
            try {
                equip.SelectFocuser(driver ?? "indi", deviceName);
                return Results.Ok(new { selected = deviceName, driver = driver ?? "indi" });
            } catch (NotSupportedException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Per-driver focuser discovery. INDI = device-name list from
        // the active connection. ASCOM = registered ProgIDs from the
        // local Windows registry. Empty on platforms / drivers that
        // can't enumerate.
        group.MapGet("/discover", (EquipmentManager equip, string? driver) => {
            var d = (driver ?? "indi").Trim().ToLowerInvariant();
            if (d == "ascom-com") {
                return Results.Ok(equip.GetAscomDrivers(
                    NINA.Ascom.Com.AscomComRegistry.DeviceType.Focuser));
            }
            if (d == "alpaca") {
                return Results.Ok(equip.GetDiscoveredFocusersFor("alpaca"));
            }
            return Results.Ok(equip.GetDeviceNames()
                .Select(n => new DiscoveredCamera(n, n, n))
                .ToList());
        });

        // Focuser driver catalogue. Mirrors /api/camera/drivers shape
        // so the frontend driver-source dropdown can render the same
        // way for every device type.
        group.MapGet("/drivers", (EquipmentManager equip) => {
            var alpacaCount = equip.GetDiscoveredFocusersFor("alpaca").Count;
            var list = new List<CameraDriverInfo> {
                new("indi", "INDI", Available: true,
                    Description: "Any focuser the running INDI server exposes."),
                new("alpaca", "Alpaca (ASCOM)", Available: alpacaCount > 0,
                    Description: alpacaCount > 0
                        ? $"ASCOM-over-HTTP focusers. {alpacaCount} discovered."
                        : "Run Alpaca Discover in RIGS first to populate this list."),
            };
            if (OperatingSystem.IsWindows()) {
                var n = equip.GetAscomDrivers(
                    NINA.Ascom.Com.AscomComRegistry.DeviceType.Focuser).Count;
                list.Add(new("ascom-com", "ASCOM (COM, direct)",
                    Available: n > 0,
                    Description: n > 0
                        ? $"Direct COM-interop. {n} driver(s) registered."
                        : "Install the ASCOM Platform + a focuser driver."));
            }
            return Results.Ok(list);
        });

        group.MapPost("/connect", async (EquipmentManager equip) => {
            if (equip.Focuser == null)
                return Results.BadRequest(new { error = "No focuser selected" });

            await equip.Focuser.ConnectAsync();
            return Results.Ok(new { status = "connected", device = equip.Focuser.DeviceName });
        });

        group.MapPost("/disconnect", async (EquipmentManager equip) => {
            if (equip.Focuser == null)
                return Results.BadRequest(new { error = "No focuser selected" });

            await equip.Focuser.DisconnectAsync();
            return Results.Ok(new { status = "disconnected" });
        });
    }

    public record MoveAbsoluteRequest(int Position);
    public record MoveRelativeRequest(int Steps);
}
