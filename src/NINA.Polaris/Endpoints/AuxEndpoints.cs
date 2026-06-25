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
/// Auxiliary (second) camera + focuser endpoints. Mirrors the imaging/guide
/// camera select/connect/discover surface so the RIGS aux card and the FOCUS
/// aux-source switch can drive them. Capture+save itself is owned by
/// <see cref="AuxCaptureService"/>; these endpoints just wire the equipment.
/// </summary>
public static class AuxEndpoints {
    public static void MapAuxEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/aux");

        // ----- Aux camera selection + connection -----
        group.MapPost("/camera/select/{deviceName}", (EquipmentManager equip,
                AuxCaptureService aux, string deviceName, string? driver) => {
            try {
                equip.SelectAuxCamera(driver ?? "indi", deviceName);
                aux.Sync();
                return Results.Ok(new { selected = deviceName, driver = driver ?? "indi" });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/camera/connect", async (EquipmentManager equip, AuxCaptureService aux,
                ProfileService profileSvc, ILoggerFactory loggerFactory) => {
            if (equip.AuxCamera == null)
                return Results.BadRequest(new { error = "No aux camera selected. Use POST /api/aux/camera/select/{name} first" });
            await equip.AuxCamera.ConnectAsync();
            // Per-rig pixel-size fallback for a DSLR on the aux port (gphoto
            // reports CCD_INFO pixel size as 0). Mirrors the main-camera connect.
            try {
                var rig = profileSvc.ActiveEquipmentProfile;
                if (rig != null && equip.AuxCamera.MaxX <= 0
                    && rig.AuxCameraMaxX > 0 && rig.AuxCameraMaxY > 0 && rig.AuxCameraPixelSizeUm > 0
                    && equip.AuxCamera is NINA.INDI.Devices.IndiCamera indiCam) {
                    await indiCam.TrySetCcdInfoAsync(rig.AuxCameraMaxX, rig.AuxCameraMaxY,
                        rig.AuxCameraPixelSizeUm, rig.AuxCameraBitDepth);
                    loggerFactory.CreateLogger("Polaris.AuxCamera")
                        .LogInformation("Pushed rig aux CCD_INFO into {Dev}: {X}x{Y} px, {P}µm, {B}-bit " +
                            "(aux camera reported 0 — DSLR bootstrap)", equip.AuxCamera.DeviceName,
                            rig.AuxCameraMaxX, rig.AuxCameraMaxY, rig.AuxCameraPixelSizeUm, rig.AuxCameraBitDepth);
                }
            } catch (Exception ex) {
                loggerFactory.CreateLogger("Polaris.AuxCamera")
                    .LogDebug(ex, "Aux CCD_INFO push on connect skipped (non-fatal)");
            }
            aux.Sync();
            return Results.Ok(new { status = "connected", device = equip.AuxCamera.DeviceName });
        });

        group.MapPost("/camera/disconnect", async (EquipmentManager equip, AuxCaptureService aux) => {
            if (equip.AuxCamera == null)
                return Results.Ok(new { status = "disconnected" });
            await equip.AuxCamera.DisconnectAsync();
            aux.Sync();
            return Results.Ok(new { status = "disconnected" });
        });

        group.MapGet("/camera/discover", (EquipmentManager equip, string? driver)
            => Results.Ok(equip.GetDiscoveredCamerasFor(driver ?? "indi")));

        // DSLR ISO selection for the aux camera (indi_gphoto CCD_ISO). 501 when
        // the aux camera doesn't expose ISO (astro cams use analogue gain).
        group.MapPost("/camera/iso", async (EquipmentManager equip, AuxIsoRequest req) => {
            if (equip.AuxCamera == null || !equip.AuxCamera.IsConnected)
                return Results.BadRequest(new { error = "No aux camera connected" });
            if (!equip.AuxCamera.Capabilities.SupportsIso)
                return Results.Json(new { error = "Aux camera does not support ISO" },
                    statusCode: 501);
            await equip.AuxCamera.SetIsoAsync(req.Iso);
            return Results.Ok(new { iso = req.Iso });
        });

        // ----- Aux focuser selection + connection + manual jog -----
        group.MapPost("/focuser/select/{deviceName}", (EquipmentManager equip,
                string deviceName, string? driver) => {
            try {
                equip.SelectAuxFocuser(driver ?? "indi", deviceName);
                return Results.Ok(new { selected = deviceName, driver = driver ?? "indi" });
            } catch (NotSupportedException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/focuser/connect", async (EquipmentManager equip) => {
            if (equip.AuxFocuser == null)
                return Results.BadRequest(new { error = "No aux focuser selected" });
            await equip.AuxFocuser.ConnectAsync();
            return Results.Ok(new { status = "connected", device = equip.AuxFocuser.DeviceName });
        });

        group.MapPost("/focuser/disconnect", async (EquipmentManager equip) => {
            if (equip.AuxFocuser == null)
                return Results.Ok(new { status = "disconnected" });
            await equip.AuxFocuser.DisconnectAsync();
            return Results.Ok(new { status = "disconnected" });
        });

        group.MapPost("/focuser/move/absolute", async (EquipmentManager equip,
                FocuserEndpoints.MoveAbsoluteRequest request) => {
            if (equip.AuxFocuser == null)
                return Results.BadRequest(new { error = "No aux focuser selected" });
            await equip.AuxFocuser.MoveAbsoluteAsync(request.Position);
            return Results.Ok(new { status = "moving", target = request.Position });
        });

        group.MapPost("/focuser/move/relative", async (EquipmentManager equip,
                FocuserEndpoints.MoveRelativeRequest request) => {
            if (equip.AuxFocuser == null)
                return Results.BadRequest(new { error = "No aux focuser selected" });
            await equip.AuxFocuser.MoveRelativeAsync(request.Steps);
            return Results.Ok(new { status = "moving", steps = request.Steps });
        });

        group.MapPost("/focuser/abort", async (EquipmentManager equip) => {
            if (equip.AuxFocuser == null)
                return Results.BadRequest(new { error = "No aux focuser selected" });
            await equip.AuxFocuser.AbortAsync();
            return Results.Ok(new { status = "stopped" });
        });

        // ----- Aux capture enable toggle + status -----
        // Persists AuxEnabled on the active rig and re-evaluates the loop so the
        // change takes effect immediately (without waiting for a rig PUT).
        group.MapPost("/enabled", (ProfileService profiles, AuxCaptureService aux,
                AuxEnabledRequest req) => {
            var rig = profiles.ActiveEquipmentProfile;
            if (rig != null)
                profiles.UpdateEquipmentProfile(rig.Id, r => r.AuxEnabled = req.Enabled);
            aux.Sync();
            return Results.Ok(new { enabled = req.Enabled });
        });

        group.MapGet("/status", (AuxCaptureService aux) => Results.Ok(new {
            running = aux.IsRunning,
            frameCount = aux.FrameCount,
            lastError = aux.LastError,
            noOutputDir = aux.NoOutputDir
        }));
    }

    public record AuxEnabledRequest(bool Enabled);
    public record AuxIsoRequest(int Iso);
}
