// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details. You should have received a copy of the license along with
// this program. If not, see <https://www.gnu.org/licenses/>.

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

        // List the wireless adapters NetworkManager sees, plus the one the
        // hotspot/station currently binds to, so the UI can offer a picker
        // (e.g. an external USB antenna vs the Pi's built-in radio).
        group.MapGet("/interfaces", async (NetworkManagerService net) => {
            if (!net.IsSupportedOs || !net.NmcliInstalled) {
                return Results.Json(
                    new { error = net.UnsupportedReason ?? "WiFi management not available" },
                    statusCode: 501);
            }
            var ifaces = await net.ListWifiInterfacesAsync();
            return Results.Ok(new { interfaces = ifaces, selected = net.WifiInterface });
        });

        // Bind the hotspot/station to a specific adapter.
        group.MapPost("/interface", async (NetworkManagerService net, InterfaceRequest req) => {
            if (!net.IsSupportedOs || !net.NmcliInstalled) {
                return Results.Json(
                    new { error = net.UnsupportedReason ?? "WiFi management not available" },
                    statusCode: 501);
            }
            if (req == null || string.IsNullOrWhiteSpace(req.Interface)) {
                return Results.BadRequest(new { error = "interface required" });
            }
            var res = await net.SetPreferredInterfaceAsync(req.Interface);
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
    public record InterfaceRequest(string Interface);
}