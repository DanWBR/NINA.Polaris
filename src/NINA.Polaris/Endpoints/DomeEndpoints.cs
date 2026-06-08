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

public static class DomeEndpoints {
    public static void MapDomeEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/dome");

        group.MapGet("/status", (EquipmentManager equip) => {
            if (equip.Dome == null)
                return Results.Ok(new {
                    connected = false,
                    azimuth = 0.0,
                    moving = false,
                    parked = false,
                    slaved = false,
                    shutter = "unknown"
                });

            var az = equip.Dome.Azimuth;
            return Results.Ok(new {
                connected = equip.Dome.IsConnected,
                name = equip.Dome.DeviceName,
                azimuth = double.IsNaN(az) ? 0.0 : az,
                moving = equip.Dome.IsMoving,
                parked = equip.Dome.IsParked,
                slaved = equip.Dome.IsSlaved,
                shutter = equip.Dome.ShutterStatus.ToString()
            });
        });

        group.MapPost("/slew", async (EquipmentManager equip, SlewDomeRequest request) => {
            if (equip.Dome == null)
                return Results.BadRequest(new { error = "No dome selected" });

            await equip.Dome.SlewToAzimuthAsync(request.Azimuth);
            return Results.Ok(new { status = "slewing", target = request.Azimuth });
        });

        group.MapPost("/shutter/open", async (EquipmentManager equip) => {
            if (equip.Dome == null)
                return Results.BadRequest(new { error = "No dome selected" });

            await equip.Dome.OpenShutterAsync();
            return Results.Ok(new { status = "opening" });
        });

        group.MapPost("/shutter/close", async (EquipmentManager equip) => {
            if (equip.Dome == null)
                return Results.BadRequest(new { error = "No dome selected" });

            await equip.Dome.CloseShutterAsync();
            return Results.Ok(new { status = "closing" });
        });

        group.MapPost("/park", async (EquipmentManager equip) => {
            if (equip.Dome == null)
                return Results.BadRequest(new { error = "No dome selected" });

            await equip.Dome.ParkAsync();
            return Results.Ok(new { status = "parking" });
        });

        group.MapPost("/unpark", async (EquipmentManager equip) => {
            if (equip.Dome == null)
                return Results.BadRequest(new { error = "No dome selected" });

            await equip.Dome.UnparkAsync();
            return Results.Ok(new { status = "unparking" });
        });

        group.MapPost("/abort", async (EquipmentManager equip) => {
            if (equip.Dome == null)
                return Results.BadRequest(new { error = "No dome selected" });

            await equip.Dome.AbortAsync();
            return Results.Ok(new { status = "stopped" });
        });

        group.MapPost("/select/{deviceName}", (EquipmentManager equip, string deviceName) => {
            equip.SelectDome(deviceName);
            return Results.Ok(new { selected = deviceName });
        });

        group.MapPost("/connect", async (EquipmentManager equip) => {
            if (equip.Dome == null)
                return Results.BadRequest(new { error = "No dome selected" });

            await equip.Dome.ConnectAsync();
            return Results.Ok(new { status = "connected", device = equip.Dome.DeviceName });
        });

        group.MapPost("/disconnect", async (EquipmentManager equip) => {
            if (equip.Dome == null)
                return Results.BadRequest(new { error = "No dome selected" });

            await equip.Dome.DisconnectAsync();
            return Results.Ok(new { status = "disconnected" });
        });
    }

    public record SlewDomeRequest(double Azimuth);
}