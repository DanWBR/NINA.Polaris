using System.Net.Sockets;
using NINA.INDI.Client;

namespace NINA.Polaris.Endpoints;

public static class IndiEndpoints {
    public static void MapIndiEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/indi");

        group.MapPost("/connect", async (IndiClient client, IndiConnectRequest request) => {
            try {
                await client.ConnectAsync();
                return Results.Ok(new {
                    status = "connected",
                    host = client.Host,
                    port = client.Port
                });
            } catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused
                                            || ex.SocketErrorCode == SocketError.HostNotFound
                                            || ex.SocketErrorCode == SocketError.HostUnreachable
                                            || ex.SocketErrorCode == SocketError.NetworkUnreachable) {
                // 502 Bad Gateway, the most common case: indiserver isn't running
                // at the configured host:port. Translate to a user-facing message
                // instead of leaking the raw localised OS error as a 500.
                return Results.Json(new {
                    error = "indi_unreachable",
                    detail = $"INDI server not reachable at {client.Host}:{client.Port}. " +
                             "Start indiserver on that host or update Indi:Host / Indi:Port (or the rig's INDI endpoint).",
                    socketError = ex.SocketErrorCode.ToString()
                }, statusCode: 502);
            } catch (TimeoutException ex) {
                return Results.Json(new {
                    error = "indi_timeout",
                    detail = ex.Message
                }, statusCode: 504);
            } catch (Exception ex) {
                return Results.Json(new {
                    error = "indi_connect_failed",
                    detail = ex.Message
                }, statusCode: 502);
            }
        });

        group.MapPost("/disconnect", async (IndiClient client) => {
            await client.DisconnectAsync();
            return Results.Ok(new { status = "disconnected" });
        });

        group.MapGet("/status", (IndiClient client) => {
            return Results.Ok(new {
                connected = client.IsConnected,
                host = client.Host,
                port = client.Port,
                devices = client.GetDeviceNames()
            });
        });

        group.MapGet("/devices", (IndiClient client) => {
            var devices = new List<object>();
            foreach (var deviceName in client.GetDeviceNames()) {
                if (client.Devices.TryGetValue(deviceName, out var props)) {
                    var groups = props.Values
                        .Select(p => p.Group)
                        .Where(g => !string.IsNullOrEmpty(g))
                        .Distinct()
                        .ToList();

                    devices.Add(new {
                        name = deviceName,
                        propertyCount = props.Count,
                        groups
                    });
                }
            }
            return Results.Ok(new { devices });
        });

        group.MapGet("/devices/{deviceName}/properties", (IndiClient client, string deviceName) => {
            if (!client.Devices.TryGetValue(deviceName, out var props))
                return Results.NotFound(new { error = $"Device '{deviceName}' not found" });

            var result = props.Values.Select(p => new {
                name = p.Name,
                label = p.Label,
                group = p.Group,
                state = p.State.ToString(),
                permission = p.Permission.ToString(),
                type = p.GetType().Name.Replace("Indi", "").Replace("Property", "")
            });

            return Results.Ok(new { device = deviceName, properties = result });
        });
    }

    public record IndiConnectRequest(string? Host = "localhost", int? Port = 7624);
}
