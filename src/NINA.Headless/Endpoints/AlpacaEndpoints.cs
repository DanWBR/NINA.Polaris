using NINA.Headless.Services.Alpaca;

namespace NINA.Headless.Endpoints;

public static class AlpacaEndpoints {
    public static void MapAlpacaEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/alpaca");

        // ---- Discovery ----
        group.MapGet("/discover", async (AlpacaDiscovery disc, int? timeoutMs) => {
            var to = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs ?? 3000, 200, 15000));
            var servers = await disc.DiscoverServersAsync(to);
            return Results.Ok(new { count = servers.Count, servers });
        });

        // ---- Manual server query (skip discovery, useful behind NAT / for tests) ----
        group.MapGet("/devices", async (string host, int port) => {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            try {
                var url = $"http://{host}:{port}/management/v1/configureddevices";
                var resp = await http.GetFromJsonAsync<AlpacaResponse<List<AlpacaConfiguredDevice>>>(url);
                return Results.Ok(new {
                    host, port,
                    devices = resp?.Value ?? new List<AlpacaConfiguredDevice>()
                });
            } catch (Exception ex) {
                return Results.Problem($"Failed to query {host}:{port}: {ex.Message}");
            }
        });

        // ---- Camera info probe ----
        group.MapGet("/camera/info", async (string host, int port, int? device) => {
            var cam = new AlpacaCamera(host, port, device ?? 0);
            try {
                return Results.Ok(new {
                    name = await cam.GetNameAsync(),
                    description = await cam.GetDescriptionAsync(),
                    connected = await cam.GetConnectedAsync(),
                    width = await cam.GetCameraXSizeAsync(),
                    height = await cam.GetCameraYSizeAsync(),
                    pixelSizeX = await cam.GetPixelSizeXAsync(),
                    pixelSizeY = await cam.GetPixelSizeYAsync(),
                    coolerOn = await cam.GetCoolerOnAsync(),
                    ccdTemp = await cam.GetCcdTemperatureAsync(),
                    setTemp = await cam.GetSetCcdTemperatureAsync(),
                    binX = await cam.GetBinXAsync(),
                    maxBinX = await cam.GetMaxBinXAsync()
                });
            } catch (Exception ex) {
                return Results.Problem($"Alpaca camera query failed: {ex.Message}");
            }
        });

        group.MapPost("/camera/connect", async (string host, int port, int? device, bool connect) => {
            var cam = new AlpacaCamera(host, port, device ?? 0);
            try {
                await cam.SetConnectedAsync(connect);
                return Results.Ok(new { connected = await cam.GetConnectedAsync() });
            } catch (Exception ex) {
                return Results.Problem($"Alpaca camera connect failed: {ex.Message}");
            }
        });

        // ---- Telescope info probe ----
        group.MapGet("/telescope/info", async (string host, int port, int? device) => {
            var scope = new AlpacaTelescope(host, port, device ?? 0);
            try {
                return Results.Ok(new {
                    name = await scope.GetNameAsync(),
                    connected = await scope.GetConnectedAsync(),
                    ra = await scope.GetRightAscensionAsync(),
                    dec = await scope.GetDeclinationAsync(),
                    alt = await scope.GetAltitudeAsync(),
                    az = await scope.GetAzimuthAsync(),
                    slewing = await scope.GetSlewingAsync(),
                    atPark = await scope.GetAtParkAsync(),
                    tracking = await scope.GetTrackingAsync()
                });
            } catch (Exception ex) {
                return Results.Problem($"Alpaca telescope query failed: {ex.Message}");
            }
        });

        group.MapPost("/telescope/connect", async (string host, int port, int? device, bool connect) => {
            var scope = new AlpacaTelescope(host, port, device ?? 0);
            try {
                await scope.SetConnectedAsync(connect);
                return Results.Ok(new { connected = await scope.GetConnectedAsync() });
            } catch (Exception ex) {
                return Results.Problem($"Alpaca telescope connect failed: {ex.Message}");
            }
        });
    }
}
