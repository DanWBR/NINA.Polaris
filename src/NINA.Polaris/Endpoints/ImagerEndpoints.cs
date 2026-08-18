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

using System.Linq;
using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// STAGE2: uniform per-imager equipment endpoints (<c>/api/imager/{index}</c>).
/// Index 0 = main camera, 1 = aux, 2+ = additional imaging cameras. Each imager
/// can carry its own focuser and filter wheel. Slots 0/1 delegate to the
/// existing main/aux selectors (so the legacy <c>/api/camera</c> and
/// <c>/api/aux</c> surfaces keep working); 2+ drive the new collection on
/// <see cref="EquipmentManager"/>. Capture loops + WS status per imager are a
/// follow-up; this group is the select/connect/disconnect wiring.
/// </summary>
public static class ImagerEndpoints {
    public static void MapImagerEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/imager");

        // ----- List every imaging-camera slot -----
        group.MapGet("/", (EquipmentManager equip) =>
            Results.Ok(equip.EnumerateImagers().Select(s => new {
                index = s.Index,
                role = s.Role,
                deviceId = s.DeviceId,
                driver = s.Driver,
                deviceName = s.Camera?.DeviceName,
                connected = s.Camera?.IsConnected ?? false,
            })));

        // ----- Camera select / connect / disconnect / status -----
        group.MapPost("/{index:int}/camera/select/{deviceName}", (EquipmentManager equip,
                int index, string deviceName, string? driver) => {
            if (index < 0) return Results.BadRequest(new { error = "index must be >= 0" });
            try {
                equip.SelectImager(index, driver ?? "indi", deviceName);
                return Results.Ok(new { index, selected = deviceName, driver = driver ?? "indi" });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/{index:int}/camera/connect", async (EquipmentManager equip,
                ProfileService profileSvc, ILoggerFactory loggerFactory, int index) => {
            var cam = equip.GetImager(index);
            if (cam == null)
                return Results.BadRequest(new { error = $"No camera selected for imager {index}. Select one first." });
            await DeviceConnectGuard.BoundedAsync("connect", cam.DeviceName, ct => cam.ConnectAsync(ct));
            // DSLR CCD_INFO bootstrap from the rig's imager config (same as the
            // main/aux connect paths), reading the unified Imagers projection.
            try {
                var cfg = profileSvc.ActiveEquipmentProfile?.Imagers.ElementAtOrDefault(index);
                if (cfg != null && cam.MaxX <= 0 && cfg.MaxX > 0 && cfg.MaxY > 0 && cfg.PixelSizeUm > 0
                    && cam is NINA.INDI.Devices.IndiCamera indiCam) {
                    await indiCam.TrySetCcdInfoAsync(cfg.MaxX, cfg.MaxY, cfg.PixelSizeUm, cfg.BitDepth);
                    loggerFactory.CreateLogger("Polaris.Imager")
                        .LogInformation("Pushed rig CCD_INFO into imager {Idx} {Dev}: {X}x{Y} px, {P}µm (DSLR bootstrap)",
                            index, cam.DeviceName, cfg.MaxX, cfg.MaxY, cfg.PixelSizeUm);
                }
            } catch (Exception ex) {
                loggerFactory.CreateLogger("Polaris.Imager").LogDebug(ex, "Imager CCD_INFO push skipped (non-fatal)");
            }
            return Results.Ok(new { index, status = "connected", device = cam.DeviceName });
        });

        group.MapPost("/{index:int}/camera/disconnect", async (EquipmentManager equip, int index) => {
            var cam = equip.GetImager(index);
            if (cam == null) return Results.Ok(new { index, status = "disconnected" });
            await cam.DisconnectAsync();
            return Results.Ok(new { index, status = "disconnected" });
        });

        group.MapGet("/{index:int}/camera/status", (EquipmentManager equip, int index) => {
            var cam = equip.GetImager(index);
            if (cam == null) return Results.Ok(new { index, connected = false, deviceName = (string?)null });
            return Results.Ok(new {
                index,
                connected = cam.IsConnected,
                state = cam.State.ToString(),
                deviceName = cam.DeviceName,
                maxX = cam.MaxX,
                maxY = cam.MaxY,
                binX = cam.BinX,
                binY = cam.BinY,
                gain = cam.Gain,
                temperature = double.IsNaN(cam.Temperature) ? (double?)null : cam.Temperature,
                coolerOn = cam.CoolerOn,
            });
        });

        // ----- Per-imager focuser select / connect / disconnect -----
        group.MapPost("/{index:int}/focuser/select/{deviceName}", (EquipmentManager equip,
                int index, string deviceName, string? driver) => {
            if (index < 0) return Results.BadRequest(new { error = "index must be >= 0" });
            try {
                equip.SelectImagerFocuser(index, driver ?? "indi", deviceName);
                return Results.Ok(new { index, selected = deviceName, driver = driver ?? "indi" });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/{index:int}/focuser/connect", async (EquipmentManager equip, int index) => {
            var foc = equip.GetImagerFocuser(index);
            if (foc == null) return Results.BadRequest(new { error = $"No focuser selected for imager {index}." });
            await DeviceConnectGuard.BoundedAsync("connect", foc.DeviceName, ct => foc.ConnectAsync(ct));
            return Results.Ok(new { index, status = "connected", device = foc.DeviceName });
        });

        group.MapPost("/{index:int}/focuser/disconnect", async (EquipmentManager equip, int index) => {
            var foc = equip.GetImagerFocuser(index);
            if (foc == null) return Results.Ok(new { index, status = "disconnected" });
            await foc.DisconnectAsync();
            return Results.Ok(new { index, status = "disconnected" });
        });

        // ----- Per-imager filter wheel select / connect / disconnect -----
        group.MapPost("/{index:int}/filterwheel/select/{deviceName}", (EquipmentManager equip,
                int index, string deviceName, string? driver) => {
            if (index < 0) return Results.BadRequest(new { error = "index must be >= 0" });
            try {
                equip.SelectImagerFilterWheel(index, driver ?? "indi", deviceName);
                return Results.Ok(new { index, selected = deviceName, driver = driver ?? "indi" });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/{index:int}/filterwheel/connect", async (EquipmentManager equip, int index) => {
            var fw = equip.GetImagerFilterWheel(index);
            if (fw == null) return Results.BadRequest(new { error = $"No filter wheel selected for imager {index}." });
            await DeviceConnectGuard.BoundedAsync("connect", fw.DeviceName, ct => fw.ConnectAsync(ct));
            return Results.Ok(new { index, status = "connected", device = fw.DeviceName });
        });

        group.MapPost("/{index:int}/filterwheel/disconnect", async (EquipmentManager equip, int index) => {
            var fw = equip.GetImagerFilterWheel(index);
            if (fw == null) return Results.Ok(new { index, status = "disconnected" });
            await fw.DisconnectAsync();
            return Results.Ok(new { index, status = "disconnected" });
        });
    }
}
