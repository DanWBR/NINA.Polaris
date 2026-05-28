using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// REST surface for WiFi hotspot / station management via
/// <see cref="NetworkManagerService"/>. Mirrors the shape of
/// <c>/api/indi/web/*</c> and <c>/api/guider/gui-session/*</c>
/// (status + mutators + 501 guards on unsupported platforms) so
/// frontend dispatch follows the same pattern across all the
/// platform-conditional services.
/// </summary>
public static class NetworkEndpoints {
    public static void MapNetworkEndpoints(this IEndpointRouteBuilder app) {
        var group = app.MapGroup("/api/network");

        group.MapGet("/status", async (NetworkManagerService net) => {
            var snap = await net.GetSnapshotAsync();
            return Results.Ok(snap);
        });

        group.MapGet("/scan", async (NetworkManagerService net) => {
            if (!net.IsSupportedOs || !net.NmcliInstalled || !net.HasWifiInterface) {
                return Results.Json(
                    new { error = net.UnsupportedReason ?? "WiFi management not available" },
                    statusCode: 501);
            }
            var nets = await net.ScanAsync();
            return Results.Ok(nets);
        });

        // Switch to station mode. Blocks up to ~35s on the try-and-revert
        // path: nmcli connection up (up to 35s) + WaitForLeaseAsync
        // (30s). Frontend MUST surface a "switching..." spinner and be
        // resilient to the TCP socket being torn down mid-response when
        // the active wifi link drops during the switch (see app.js
        // _networkSwitchPending handler).
        group.MapPost("/station", async (NetworkManagerService net, StationRequest req) => {
            if (!net.IsSupportedOs || !net.NmcliInstalled || !net.HasWifiInterface) {
                return Results.Json(
                    new { error = net.UnsupportedReason ?? "WiFi management not available" },
                    statusCode: 501);
            }
            if (req == null || string.IsNullOrEmpty(req.Ssid) || string.IsNullOrEmpty(req.Password)) {
                return Results.BadRequest(new { error = "ssid + password required" });
            }
            var res = await net.SwitchToStationAsync(req.Ssid, req.Password);
            return Results.Ok(res);
        });

        group.MapPost("/hotspot", async (NetworkManagerService net) => {
            if (!net.IsSupportedOs || !net.NmcliInstalled || !net.HasWifiInterface) {
                return Results.Json(
                    new { error = net.UnsupportedReason ?? "WiFi management not available" },
                    statusCode: 501);
            }
            var res = await net.SwitchToHotspotAsync();
            return Results.Ok(res);
        });

        group.MapPut("/hotspot/credentials",
            async (NetworkManagerService net, HotspotCredentialsRequest req) => {
                if (!net.IsSupportedOs || !net.NmcliInstalled || !net.HasWifiInterface) {
                    return Results.Json(
                        new { error = net.UnsupportedReason ?? "WiFi management not available" },
                        statusCode: 501);
                }
                if (req == null || string.IsNullOrEmpty(req.Ssid) || string.IsNullOrEmpty(req.Password)) {
                    return Results.BadRequest(new { error = "ssid + password required" });
                }
                var res = await net.SetHotspotCredentialsAsync(req.Ssid, req.Password);
                return Results.Ok(res);
            });
    }

    public record StationRequest(string Ssid, string Password);
    public record HotspotCredentialsRequest(string Ssid, string Password);
}
