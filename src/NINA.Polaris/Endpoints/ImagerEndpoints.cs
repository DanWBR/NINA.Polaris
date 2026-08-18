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

        // ----- Add / remove an extra imager (index 2+) + persist its config -----
        // The per-imager capture config lives in EquipmentProfile.ExtraImagers so
        // it survives a restart AND so MultiImagerCaptureService can gate each loop
        // on its Enabled flag. These write it; RigPatch already round-trips the
        // list on unrelated rig PUTs so it is never wiped.
        group.MapPost("/add", (ProfileService profiles, MultiImagerCaptureService multiImager) => {
            var rig = profiles.ActiveEquipmentProfile;
            if (rig == null) return Results.BadRequest(new { error = "No active rig" });
            int newIndex = 2 + rig.ExtraImagers.Count;
            profiles.UpdateEquipmentProfile(rig.Id, r => r.ExtraImagers.Add(new ImagerConfig {
                Enabled = false, Role = $"imager-{newIndex + 1}"
            }));
            multiImager.Sync();
            return Results.Ok(new { index = newIndex });
        });

        group.MapDelete("/{index:int}", async (EquipmentManager equip, ProfileService profiles,
                MultiImagerCaptureService multiImager, int index) => {
            if (index < 2) return Results.BadRequest(new { error = "Only extra imagers (index >= 2) can be removed" });
            // Disconnect the bound devices first (best effort), then drop the
            // runtime slot and the persisted config together so they stay aligned.
            try { var c = equip.GetImager(index); if (c != null) await c.DisconnectAsync(); } catch { }
            try { var f = equip.GetImagerFocuser(index); if (f != null) await f.DisconnectAsync(); } catch { }
            try { var w = equip.GetImagerFilterWheel(index); if (w != null) await w.DisconnectAsync(); } catch { }
            try { equip.RemoveImager(index); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
            var rig = profiles.ActiveEquipmentProfile;
            if (rig != null) {
                int i = index - 2;
                profiles.UpdateEquipmentProfile(rig.Id, r => {
                    if (i >= 0 && i < r.ExtraImagers.Count) r.ExtraImagers.RemoveAt(i);
                });
            }
            multiImager.Sync();
            return Results.Ok(new { index, removed = true });
        });

        // Persist the capture config for one extra imager (enable toggle, optics,
        // exposure/gain/binning) and re-evaluate its loop immediately, mirroring
        // POST /api/aux/enabled. Absent fields keep their stored value.
        group.MapPut("/{index:int}/config", (ProfileService profiles,
                MultiImagerCaptureService multiImager, int index, ImagerConfigPatch body) => {
            if (index < 2) return Results.BadRequest(new { error = "index must be >= 2" });
            var rig = profiles.ActiveEquipmentProfile;
            if (rig == null) return Results.BadRequest(new { error = "No active rig" });
            int i = index - 2;
            if (i >= rig.ExtraImagers.Count) return Results.NotFound(new { error = $"No imager at index {index}" });
            profiles.UpdateEquipmentProfile(rig.Id, r => {
                var c = r.ExtraImagers[i];
                if (body.Enabled is bool en) c.Enabled = en;
                if (body.ExposureMs is int e) c.ExposureMs = Math.Max(50, e);
                if (body.Gain is int g) c.Gain = Math.Max(0, g);
                if (body.Binning is int b) c.Binning = Math.Clamp(b, 1, 4);
                if (body.FocalLengthMm is double fl) c.FocalLengthMm = Math.Max(0, fl);
                if (body.ApertureMm is double ap) c.ApertureMm = Math.Max(0, ap);
                if (body.PixelSizeUm is double px) c.PixelSizeUm = Math.Max(0, px);
                if (body.TelescopeBrand != null) c.TelescopeBrand = body.TelescopeBrand;
                if (body.TelescopeModel != null) c.TelescopeModel = body.TelescopeModel;
            });
            multiImager.Sync();
            return Results.Ok(new { index, saved = true });
        });

        // ----- Camera select / connect / disconnect / status -----
        group.MapPost("/{index:int}/camera/select/{deviceName}", (EquipmentManager equip,
                ProfileService profiles, MultiImagerCaptureService multiImager,
                int index, string deviceName, string? driver) => {
            if (index < 0) return Results.BadRequest(new { error = "index must be >= 0" });
            try {
                equip.SelectImager(index, driver ?? "indi", deviceName);
                PersistImagerDevice(profiles, index, c => { c.DeviceId = deviceName; c.Driver = driver ?? "indi"; });
                multiImager.Sync();
                return Results.Ok(new { index, selected = deviceName, driver = driver ?? "indi" });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/{index:int}/camera/connect", async (EquipmentManager equip,
                ProfileService profileSvc, MultiImagerCaptureService multiImager,
                ILoggerFactory loggerFactory, int index) => {
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
            multiImager.Sync();   // start this imager's capture loop if a session is active
            return Results.Ok(new { index, status = "connected", device = cam.DeviceName });
        });

        group.MapPost("/{index:int}/camera/disconnect", async (EquipmentManager equip,
                MultiImagerCaptureService multiImager, int index) => {
            var cam = equip.GetImager(index);
            if (cam == null) return Results.Ok(new { index, status = "disconnected" });
            await cam.DisconnectAsync();
            multiImager.Sync();   // stop this imager's capture loop
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
                ProfileService profiles, int index, string deviceName, string? driver) => {
            if (index < 0) return Results.BadRequest(new { error = "index must be >= 0" });
            try {
                equip.SelectImagerFocuser(index, driver ?? "indi", deviceName);
                PersistImagerDevice(profiles, index, c => { c.Focuser = deviceName; c.FocuserDriver = driver ?? "indi"; });
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

        // Per-imager focuser position + manual jog (mirrors /api/aux/focuser/*).
        group.MapGet("/{index:int}/focuser/status", (EquipmentManager equip, int index) => {
            var foc = equip.GetImagerFocuser(index);
            if (foc == null) return Results.Ok(new { index, connected = false });
            return Results.Ok(new {
                index, connected = foc.IsConnected, position = foc.Position,
                maxPosition = foc.MaxPosition, isMoving = foc.IsMoving,
                temperature = double.IsNaN(foc.Temperature) ? (double?)null : foc.Temperature,
            });
        });

        group.MapPost("/{index:int}/focuser/move/relative", async (EquipmentManager equip,
                int index, FocuserEndpoints.MoveRelativeRequest req) => {
            var foc = equip.GetImagerFocuser(index);
            if (foc == null) return Results.BadRequest(new { error = $"No focuser selected for imager {index}." });
            await foc.MoveRelativeAsync(req.Steps);
            return Results.Ok(new { index, status = "moving", steps = req.Steps });
        });

        group.MapPost("/{index:int}/focuser/move/absolute", async (EquipmentManager equip,
                int index, FocuserEndpoints.MoveAbsoluteRequest req) => {
            var foc = equip.GetImagerFocuser(index);
            if (foc == null) return Results.BadRequest(new { error = $"No focuser selected for imager {index}." });
            await foc.MoveAbsoluteAsync(req.Position);
            return Results.Ok(new { index, status = "moving", target = req.Position });
        });

        group.MapPost("/{index:int}/focuser/abort", async (EquipmentManager equip, int index) => {
            var foc = equip.GetImagerFocuser(index);
            if (foc == null) return Results.BadRequest(new { error = $"No focuser selected for imager {index}." });
            await foc.AbortAsync();
            return Results.Ok(new { index, status = "stopped" });
        });

        // ----- Per-imager filter wheel select / connect / disconnect -----
        group.MapPost("/{index:int}/filterwheel/select/{deviceName}", (EquipmentManager equip,
                ProfileService profiles, int index, string deviceName, string? driver) => {
            if (index < 0) return Results.BadRequest(new { error = "index must be >= 0" });
            try {
                equip.SelectImagerFilterWheel(index, driver ?? "indi", deviceName);
                PersistImagerDevice(profiles, index, c => { c.FilterWheel = deviceName; c.FilterWheelDriver = driver ?? "indi"; });
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

        // Per-imager filter wheel position + filter list (mirrors /api/filterwheel/*).
        group.MapGet("/{index:int}/filterwheel/status", (EquipmentManager equip, int index) => {
            var fw = equip.GetImagerFilterWheel(index);
            if (fw == null) return Results.Ok(new { index, connected = false });
            return Results.Ok(new {
                index, connected = fw.IsConnected, position = fw.Position,
                isMoving = fw.IsMoving, filterNames = fw.FilterNames,
                filterCount = fw.FilterCount, currentFilterName = fw.CurrentFilterName,
            });
        });

        group.MapPost("/{index:int}/filterwheel/position", async (EquipmentManager equip,
                int index, FilterPositionRequest req) => {
            var fw = equip.GetImagerFilterWheel(index);
            if (fw == null) return Results.BadRequest(new { error = $"No filter wheel selected for imager {index}." });
            await fw.SetPositionAsync(req.Position);
            return Results.Ok(new { index, status = "moving", position = req.Position });
        });
    }

    /// <summary>Persist a device binding onto the extra imager's stored config,
    /// growing the list if the slot has no config yet (a select can precede an
    /// explicit /add). No-op when there is no active rig.</summary>
    private static void PersistImagerDevice(ProfileService profiles, int index, Action<ImagerConfig> mutate) {
        if (index < 2) return;
        var rig = profiles.ActiveEquipmentProfile;
        if (rig == null) return;
        int i = index - 2;
        profiles.UpdateEquipmentProfile(rig.Id, r => {
            while (r.ExtraImagers.Count <= i) r.ExtraImagers.Add(new ImagerConfig {
                Enabled = false, Role = $"imager-{r.ExtraImagers.Count + 3}"
            });
            mutate(r.ExtraImagers[i]);
        });
    }

    /// <summary>PUT /api/imager/{index}/config body. All fields optional (nullable)
    /// so an absent one keeps the stored value.</summary>
    public record ImagerConfigPatch(
        bool? Enabled = null, int? ExposureMs = null, int? Gain = null, int? Binning = null,
        double? FocalLengthMm = null, double? ApertureMm = null, double? PixelSizeUm = null,
        string? TelescopeBrand = null, string? TelescopeModel = null);

    /// <summary>Body for POST /api/imager/{index}/filterwheel/position.</summary>
    public record FilterPositionRequest(int Position);
}
