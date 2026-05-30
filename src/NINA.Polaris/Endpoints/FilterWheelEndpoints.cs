using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

public static class FilterWheelEndpoints {
    public static void MapFilterWheelEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/filterwheel");

        group.MapGet("/status", (EquipmentManager equip) => {
            if (equip.FilterWheel == null)
                return Results.Ok(new { connected = false });

            return Results.Ok(new {
                connected = true,
                name = equip.FilterWheel.DeviceName,
                position = equip.FilterWheel.Position,
                currentFilter = equip.FilterWheel.CurrentFilterName,
                filters = equip.FilterWheel.FilterNames,
                moving = equip.FilterWheel.IsMoving
            });
        });

        group.MapPost("/position/{slot:int}", async (int slot, EquipmentManager equip) => {
            if (equip.FilterWheel == null)
                return Results.BadRequest(new { error = "No filter wheel connected" });

            await equip.FilterWheel.SetPositionAsync(slot);
            return Results.Ok(new {
                position = slot,
                message = $"Moving to filter slot {slot}"
            });
        });

        group.MapPost("/filter/{filterName}", async (string filterName, EquipmentManager equip) => {
            if (equip.FilterWheel == null)
                return Results.BadRequest(new { error = "No filter wheel connected" });

            try {
                await equip.FilterWheel.SetFilterByNameAsync(filterName);
                return Results.Ok(new {
                    filter = filterName,
                    message = $"Moving to filter '{filterName}'"
                });
            } catch (InvalidOperationException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/select/{deviceName}", (string deviceName, EquipmentManager equip, string? driver) => {
            try {
                equip.SelectFilterWheel(driver ?? "indi", deviceName);
                return Results.Ok(new { device = deviceName, driver = driver ?? "indi" });
            } catch (NotSupportedException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Per-driver filter-wheel discovery + driver catalogue.
        // Same shape as the focuser endpoints.
        group.MapGet("/discover", (EquipmentManager equip, string? driver) => {
            var d = (driver ?? "indi").Trim().ToLowerInvariant();
            if (d == "ascom-com") {
                return Results.Ok(equip.GetAscomDrivers(
                    NINA.Ascom.Com.AscomComRegistry.DeviceType.FilterWheel));
            }
            return Results.Ok(equip.GetDeviceNames()
                .Select(n => new DiscoveredCamera(n, n, n))
                .ToList());
        });

        group.MapGet("/drivers", (EquipmentManager equip) => {
            var list = new List<CameraDriverInfo> {
                new("indi", "INDI", Available: true,
                    Description: "Any filter wheel the running INDI server exposes."),
            };
            if (OperatingSystem.IsWindows()) {
                var n = equip.GetAscomDrivers(
                    NINA.Ascom.Com.AscomComRegistry.DeviceType.FilterWheel).Count;
                list.Add(new("ascom-com", "ASCOM (COM, direct)",
                    Available: n > 0,
                    Description: n > 0
                        ? $"Direct COM-interop. {n} driver(s) registered."
                        : "Install the ASCOM Platform + a filter-wheel driver."));
            }
            return Results.Ok(list);
        });

        group.MapPost("/connect", async (EquipmentManager equip) => {
            if (equip.FilterWheel == null)
                return Results.BadRequest(new { error = "No filter wheel selected" });

            await equip.FilterWheel.ConnectAsync();
            return Results.Ok(new { connected = true });
        });

        group.MapPost("/disconnect", async (EquipmentManager equip) => {
            if (equip.FilterWheel == null)
                return Results.BadRequest(new { error = "No filter wheel selected" });

            await equip.FilterWheel.DisconnectAsync();
            return Results.Ok(new { connected = false });
        });
    }
}
