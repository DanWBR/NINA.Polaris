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

        group.MapPost("/select/{deviceName}", (string deviceName, EquipmentManager equip) => {
            equip.SelectFilterWheel(deviceName);
            return Results.Ok(new { device = deviceName });
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
