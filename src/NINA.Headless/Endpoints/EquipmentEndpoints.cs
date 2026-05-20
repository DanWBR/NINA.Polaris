using NINA.Headless.Services;
using NINA.INDI.Client;

namespace NINA.Headless.Endpoints;

public static class EquipmentEndpoints {
    public static void MapEquipmentEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/equipment");

        group.MapGet("/devices", (IndiClient client) => {
            var devices = new List<object>();
            foreach (var deviceName in client.GetDeviceNames()) {
                if (client.Devices.TryGetValue(deviceName, out var props)) {
                    var groups = props.Values
                        .Select(p => p.Group)
                        .Where(g => !string.IsNullOrEmpty(g))
                        .Distinct()
                        .ToList();

                    var driverInfo = props.Values.FirstOrDefault(p => p.Name == "DRIVER_INFO");
                    string? driverName = null;
                    string? driverInterface = null;
                    if (driverInfo is NINA.INDI.Protocol.IndiTextProperty tp) {
                        tp.Values.TryGetValue("DRIVER_NAME", out driverName);
                        tp.Values.TryGetValue("DRIVER_INTERFACE", out driverInterface);
                    }

                    devices.Add(new {
                        name = deviceName,
                        driver = driverName,
                        @interface = driverInterface,
                        propertyCount = props.Count,
                        groups
                    });
                }
            }
            return Results.Ok(new { devices });
        });

        group.MapGet("/status", (EquipmentManager equip) => {
            return Results.Ok(equip.GetEquipmentStatus());
        });
    }
}
