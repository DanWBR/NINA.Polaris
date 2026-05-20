using NINA.Headless.Services;

namespace NINA.Headless.Endpoints;

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

        group.MapPost("/select/{deviceName}", (EquipmentManager equip, string deviceName) => {
            equip.SelectFocuser(deviceName);
            return Results.Ok(new { selected = deviceName });
        });

        group.MapPost("/connect", async (EquipmentManager equip) => {
            if (equip.Focuser == null)
                return Results.BadRequest(new { error = "No focuser selected" });

            await equip.Focuser.ConnectAsync();
            return Results.Ok(new { status = "connected", device = equip.Focuser.DeviceName });
        });
    }

    public record MoveAbsoluteRequest(int Position);
    public record MoveRelativeRequest(int Steps);
}
