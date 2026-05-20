namespace NINA.Headless.Endpoints;

public static class EquipmentEndpoints
{
    public static void MapEquipmentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/equipment");

        group.MapGet("/{deviceType}", (string deviceType) =>
        {
            return Results.Ok(new { deviceType, devices = Array.Empty<object>(), message = "Not yet connected to INDI server" });
        });

        group.MapGet("/{deviceType}/{id}", (string deviceType, string id) =>
        {
            return Results.Ok(new { deviceType, id, connected = false });
        });

        group.MapPost("/{deviceType}/{id}/connect", (string deviceType, string id) =>
        {
            return Results.Ok(new { deviceType, id, connected = true, message = "Connection pending INDI implementation" });
        });

        group.MapPost("/{deviceType}/{id}/disconnect", (string deviceType, string id) =>
        {
            return Results.Ok(new { deviceType, id, connected = false });
        });
    }
}
