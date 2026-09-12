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

public static class RotatorEndpoints {
    public static void MapRotatorEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/rotator");

        group.MapGet("/status", async (EquipmentManager equip, ProfileService profiles, CancellationToken ct) => {
            if (equip.Rotator == null)
                return Results.Ok(new {
                    connected = false,
                    position = 0.0,
                    moving = false,
                    reversed = false
                });

            await equip.Rotator.RefreshAsync(ct);
            var pos = equip.Rotator.Position;
            return Results.Ok(new {
                connected = equip.Rotator.IsConnected,
                name = equip.Rotator.DeviceName,
                position = double.IsNaN(pos) ? 0.0 : pos,
                moving = equip.Rotator.IsMoving,
                reversed = equip.Rotator.IsReversed,
                maxAngle = profiles.ActiveEquipmentProfile?.RotatorMaxAngle ?? 360
            });
        });

        group.MapPost("/move", async (EquipmentManager equip, ProfileService profiles, MoveRotatorRequest request) => {
            if (equip.Rotator == null)
                return Results.BadRequest(new { error = "No rotator selected" });
            var maxAngle = RotatorRange.NormalizeMaximum(
                profiles.ActiveEquipmentProfile?.RotatorMaxAngle ?? 360);
            if (!RotatorRange.Allows(request.Angle, maxAngle))
                return Results.BadRequest(new { error = RotatorRange.Error(maxAngle) });

            await equip.Rotator.MoveToAsync(request.Angle);
            return Results.Ok(new { status = "moving", target = request.Angle });
        });

        group.MapPost("/reverse", async (EquipmentManager equip, ReverseRequest request) => {
            if (equip.Rotator == null)
                return Results.BadRequest(new { error = "No rotator selected" });

            await equip.Rotator.ReverseAsync(request.Reversed);
            return Results.Ok(new { reversed = request.Reversed });
        });

        group.MapPost("/abort", async (EquipmentManager equip) => {
            if (equip.Rotator == null)
                return Results.BadRequest(new { error = "No rotator selected" });

            await equip.Rotator.AbortAsync();
            return Results.Ok(new { status = "stopped" });
        });

        group.MapPost("/select/{deviceName}", (EquipmentManager equip, string deviceName, string? driver) => {
            try {
                equip.SelectRotator(driver ?? "indi", deviceName);
                return Results.Ok(new { selected = deviceName, driver = driver ?? "indi" });
            } catch (NotSupportedException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapGet("/discover", (EquipmentManager equip, string? driver) => {
            var d = (driver ?? "indi").Trim().ToLowerInvariant();
            if (d == "alpaca") return Results.Ok(equip.GetDiscoveredRotatorsFor("alpaca"));
            return Results.Ok(equip.GetDeviceNames().Select(n => new DiscoveredCamera(n, n, n)).ToList());
        });

        group.MapGet("/drivers", (EquipmentManager equip) => {
            var alpacaCount = equip.GetDiscoveredRotatorsFor("alpaca").Count;
            return Results.Ok(new List<CameraDriverInfo> {
                new("indi", "INDI", true, "Any rotator the running INDI server exposes."),
                new("alpaca", "Alpaca (ASCOM)", alpacaCount > 0,
                    alpacaCount > 0 ? $"ASCOM-over-HTTP rotators. {alpacaCount} discovered."
                                    : "Run Alpaca Discover in RIGS first to populate this list."),
            });
        });

        group.MapPost("/connect", async (EquipmentManager equip) => {
            if (equip.Rotator == null)
                return Results.BadRequest(new { error = "No rotator selected" });

            return await DeviceConnectGuard.RunAsync(
                "connect", equip.Rotator.DeviceName,
                ct => equip.Rotator.ConnectAsync(ct),
                () => Results.Ok(new { status = "connected", device = equip.Rotator.DeviceName }));
        });

        group.MapPost("/disconnect", async (EquipmentManager equip) => {
            if (equip.Rotator == null)
                return Results.BadRequest(new { error = "No rotator selected" });

            return await DeviceConnectGuard.RunAsync(
                "disconnect", equip.Rotator.DeviceName,
                ct => equip.Rotator.DisconnectAsync(ct),
                () => Results.Ok(new { status = "disconnected" }));
        });
    }

    public record MoveRotatorRequest(double Angle);
    public record ReverseRequest(bool Reversed);
}
