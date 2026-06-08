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

public static class FlatDeviceEndpoints {
    public static void MapFlatDeviceEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/flatdevice");

        group.MapGet("/status", (EquipmentManager equip) => {
            if (equip.FlatDevice == null)
                return Results.Ok(new {
                    connected = false,
                    lightOn = false,
                    brightness = 0,
                    coverOpen = false,
                    coverMoving = false
                });

            return Results.Ok(new {
                connected = equip.FlatDevice.IsConnected,
                name = equip.FlatDevice.DeviceName,
                lightOn = equip.FlatDevice.IsLightOn,
                brightness = equip.FlatDevice.Brightness,
                coverOpen = equip.FlatDevice.IsCoverOpen,
                coverMoving = equip.FlatDevice.IsCoverMoving
            });
        });

        group.MapPost("/light", async (EquipmentManager equip, LightRequest request) => {
            if (equip.FlatDevice == null)
                return Results.BadRequest(new { error = "No flat device selected" });

            await equip.FlatDevice.SetLightAsync(request.On);
            return Results.Ok(new { lightOn = request.On });
        });

        group.MapPost("/brightness", async (EquipmentManager equip, BrightnessRequest request) => {
            if (equip.FlatDevice == null)
                return Results.BadRequest(new { error = "No flat device selected" });

            await equip.FlatDevice.SetBrightnessAsync(request.Brightness);
            return Results.Ok(new { brightness = request.Brightness });
        });

        group.MapPost("/cover/open", async (EquipmentManager equip) => {
            if (equip.FlatDevice == null)
                return Results.BadRequest(new { error = "No flat device selected" });

            await equip.FlatDevice.OpenCoverAsync();
            return Results.Ok(new { status = "opening" });
        });

        group.MapPost("/cover/close", async (EquipmentManager equip) => {
            if (equip.FlatDevice == null)
                return Results.BadRequest(new { error = "No flat device selected" });

            await equip.FlatDevice.CloseCoverAsync();
            return Results.Ok(new { status = "closing" });
        });

        group.MapPost("/select/{deviceName}", (EquipmentManager equip, string deviceName) => {
            equip.SelectFlatDevice(deviceName);
            return Results.Ok(new { selected = deviceName });
        });

        group.MapPost("/connect", async (EquipmentManager equip) => {
            if (equip.FlatDevice == null)
                return Results.BadRequest(new { error = "No flat device selected" });

            await equip.FlatDevice.ConnectAsync();
            return Results.Ok(new { status = "connected", device = equip.FlatDevice.DeviceName });
        });

        group.MapPost("/disconnect", async (EquipmentManager equip) => {
            if (equip.FlatDevice == null)
                return Results.BadRequest(new { error = "No flat device selected" });

            await equip.FlatDevice.DisconnectAsync();
            return Results.Ok(new { status = "disconnected" });
        });
    }

    public record LightRequest(bool On);
    public record BrightnessRequest(int Brightness);
}