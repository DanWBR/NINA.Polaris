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

using System.Text.Json;
using System.Text.Json.Nodes;
using NINA.Polaris.Services;
using NINA.INDI.Client;

namespace NINA.Polaris.Endpoints;

public static class EquipmentEndpoints {
    public static void MapEquipmentEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/equipment");

        group.MapGet("/devices", (IndiClient client) => {
            var devices = new List<object>();
            foreach (var deviceName in client.GetDeviceNames()) {
                if (client.Devices.TryGetValue(deviceName, out var props)) {
                    var groups = props.Values
                        .Select(p => p.Group)
                        .Where(g => !string.IsNullOrEmpty(g))
                        .Distinct()
                        .ToList();

                    var driverInfo = props.Values.FirstOrDefault(p => p.Name == "DRIVER_INFO");
                    string? driverName = null;
                    string? driverInterface = null;
                    if (driverInfo is NINA.INDI.Protocol.IndiTextProperty tp) {
                        tp.Values.TryGetValue("DRIVER_NAME", out driverName);
                        tp.Values.TryGetValue("DRIVER_INTERFACE", out driverInterface);
                    }

                    devices.Add(new {
                        name = deviceName,
                        driver = driverName,
                        @interface = driverInterface,
                        propertyCount = props.Count,
                        groups
                    });
                }
            }
            return Results.Ok(new { devices });
        });

        group.MapGet("/status", (EquipmentManager equip) => {
            return Results.Ok(equip.GetEquipmentStatus());
        });

        // ---- Equipment profiles (rigs) ----

        group.MapGet("/rigs", (ProfileService profiles) => {
            var rigs = profiles.ListEquipmentProfiles();
            return Results.Ok(new {
                activeId = profiles.ActiveEquipmentProfile.Id,
                rigs
            });
        });

        group.MapGet("/rigs/active", (ProfileService profiles) => {
            return Results.Ok(profiles.ActiveEquipmentProfile);
        });

        // ---- Rig-set backup / restore (BACKUP-RESTORE) ----
        // Export every rig as a self-describing JSON the user downloads and
        // keeps. Restore merges by Id (see ImportEquipmentProfiles), so a lost
        // rig can be brought back without disturbing the others.
        group.MapGet("/rigs/export", (ProfileService profiles) => {
            return Results.Ok(new {
                format = "polaris-rigs",
                version = 1,
                exportedAt = DateTime.UtcNow.ToString("o"),
                activeId = profiles.ActiveEquipmentProfile.Id,
                rigs = profiles.ListEquipmentProfiles()
            });
        });

        group.MapPost("/rigs/import", async (HttpRequest request, ProfileService profiles) => {
            JsonNode? root;
            try { root = await request.ReadFromJsonAsync<JsonNode>(); }
            catch (JsonException ex) { return Results.BadRequest(new { error = "Invalid rig file: " + ex.Message }); }
            if (root == null) return Results.BadRequest(new { error = "Empty file" });
            // Accept the wrapped export {format, rigs, activeId} or a bare array.
            var arr = root as JsonArray ?? root["rigs"] as JsonArray;
            if (arr == null) return Results.BadRequest(new { error = "No rigs found in file" });
            var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var rigs = new List<EquipmentProfile>();
            foreach (var node in arr) {
                try { if (node.Deserialize<EquipmentProfile>(opts) is { } rig) rigs.Add(rig); }
                catch { /* skip a malformed entry rather than failing the whole restore */ }
            }
            if (rigs.Count == 0) return Results.BadRequest(new { error = "No valid rigs in file" });
            var activeId = root["activeId"]?.GetValue<string>();
            var applied = profiles.ImportEquipmentProfiles(rigs, activeId);
            return Results.Ok(new { imported = applied });
        });

        group.MapPost("/rigs", (CreateRigRequest req, ProfileService profiles) => {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name required" });
            var rig = profiles.CreateEquipmentProfile(req.Name);
            return Results.Ok(rig);
        });

        group.MapPost("/rigs/clone", (CloneRigRequest req, ProfileService profiles) => {
            if (string.IsNullOrWhiteSpace(req.NewName))
                return Results.BadRequest(new { error = "NewName required" });
            var clone = profiles.CloneActiveRigAs(req.NewName);
            return Results.Ok(clone);
        });

        // The body is read as raw JSON and merged onto the STORED rig (RigPatch)
        // instead of being bound straight to an EquipmentProfile. Binding gave
        // every absent property its C# initialiser, and those initialisers are
        // real-looking values: a compute-mode-only PUT arrived carrying
        // Name = "Default", NativeRaAlgorithm = "hysteresis", NativeDecAlgorithm
        // = "resistswitch", NativePierSideHandling = "mirror" — which is exactly
        // how a rig named "SV503" came back as "Default" with its guiding setup
        // reset. After the merge, absent ⇒ the stored value, so the guards and
        // clamps below run against real input only.
        group.MapPut("/rigs/{id}", async (string id, HttpRequest request, ProfileService profiles) => {
            var stored = profiles.ListEquipmentProfiles().FirstOrDefault(r => r.Id == id);
            if (stored == null) return Results.NotFound(new { error = "Rig not found" });
            JsonObject? patch;
            try {
                patch = await request.ReadFromJsonAsync<JsonObject>();
            } catch (JsonException ex) {
                return Results.BadRequest(new { error = "Invalid rig body: " + ex.Message });
            }
            var update = RigPatch.Merge(stored, patch);
            var ok = profiles.UpdateEquipmentProfile(id, r => {
                // Identity + primary-device selections are the
                // source-of-truth fields the operator complained about
                // losing ("rig name reverts to Default", "main telescope
                // / guide camera selection disappears after a while").
                // The server is authoritative for these; a client PUT
                // must never blank them. A blank value here is always a
                // client-side glitch (a debounced save firing while a
                // <select> is mid-render with no matching <option>, a
                // stale in-memory rig, or a partial body from an old
                // client) -- never a deliberate "clear". The UI has no
                // flow that clears a name/camera/mount/focuser/wheel by
                // PUTting empty (the choice handlers fall back to the
                // stored value), so guarding blanks costs no capability
                // and lets the saved rig self-heal on the next reload.
                if (!string.IsNullOrWhiteSpace(update.Name))
                    r.Name = update.Name;
                if (!string.IsNullOrWhiteSpace(update.Camera))
                    r.Camera = update.Camera;
                // Empty/null camera driver from old clients is treated
                // as the legacy default ("indi"), so untouched rig PUTs
                // don't accidentally clear the driver field.
                if (!string.IsNullOrWhiteSpace(update.CameraDriver))
                    r.CameraDriver = update.CameraDriver;
                if (!string.IsNullOrWhiteSpace(update.Telescope))
                    r.Telescope = update.Telescope;
                if (!string.IsNullOrWhiteSpace(update.TelescopeDriver))
                    r.TelescopeDriver = update.TelescopeDriver;
                if (!string.IsNullOrWhiteSpace(update.Focuser))
                    r.Focuser = update.Focuser;
                if (!string.IsNullOrWhiteSpace(update.FilterWheel))
                    r.FilterWheel = update.FilterWheel;
                // Optional accessories stay clearable (empty = "None"):
                // unlike the primary devices, deselecting these is a
                // legitimate operation.
                r.Rotator = update.Rotator;
                r.FlatDevice = update.FlatDevice;
                r.Dome = update.Dome;
                r.Weather = update.Weather;
                r.Switch = update.Switch;
                if (!string.IsNullOrWhiteSpace(update.SwitchDriver))
                    r.SwitchDriver = update.SwitchDriver;
                // RIGPUT-1: every value-type per-rig field is nullable + HasValue-
                // guarded, so a PARTIAL PUT (the {liveStackComputeMode} and
                // {slewConfirmDeg,slewFloorDeg} patches below omit everything else)
                // no longer resets these to the model defaults. Before this, a
                // compute-mode toggle silently zeroed gain/offset/binning/cooler —
                // the offset→0 case clipped the SV405CC to black. An explicit 0 /
                // false still writes; only ABSENT (null) is left alone.
                if (update.CoolerTargetTemperature.HasValue)
                    r.CoolerTargetTemperature = update.CoolerTargetTemperature.Value;
                if (update.CoolerRampDegPerMinute.HasValue)
                    r.CoolerRampDegPerMinute = Math.Max(0, update.CoolerRampDegPerMinute.Value);
                if (update.DefaultGain.HasValue) r.DefaultGain = update.DefaultGain.Value;
                if (update.DefaultOffset.HasValue) r.DefaultOffset = update.DefaultOffset.Value;
                if (update.DefaultBinning.HasValue) r.DefaultBinning = update.DefaultBinning.Value;
                // FIELD-2: per-rig Bayer mosaic override. Treat empty /
                // whitespace as null ("Auto") so the UI <select> with
                // an empty-value default round-trips cleanly. Anything
                // else is normalised to upper-snake (RGGB/GBRG/...) and
                // validated downstream by LiveStackingService when it
                // tries to parse it to BayerPatternEnum.
                r.BayerPatternOverride = string.IsNullOrWhiteSpace(update.BayerPatternOverride)
                    ? null : update.BayerPatternOverride.Trim().ToUpperInvariant();
                // FIELD3-2: vertical-flip companion to the Bayer
                // override. Boolean copies through cleanly (default
                // false). Together with the Bayer override they cover
                // both SVBONY family symptoms: completely-wrong mosaic
                // (Bayer override) vs row-offset mosaic / checkerboard
                // (vertical flip).
                // RIGPUT-1 (same reason): a partial PUT must not reset the user's
                // flip / focuser tuning. null ⇒ absent ⇒ leave alone.
                if (update.VerticalFlipImage.HasValue) r.VerticalFlipImage = update.VerticalFlipImage.Value;
                if (update.FocuserStepSize.HasValue) r.FocuserStepSize = update.FocuserStepSize.Value;
                if (update.FocuserBacklashSteps.HasValue) r.FocuserBacklashSteps = update.FocuserBacklashSteps.Value;
                // AFPORT: per-rig autofocus settings. Null from an old client
                // must NOT blank the stored block (rig-persistence guard).
                r.AutoFocus = update.AutoFocus ?? r.AutoFocus;
                // Polar alignment (TPPA) tunables. Defensive: zero from
                // an old client should not nuke the defaults, clamp.
                if (update.PolarAlignSlewDegrees > 0)
                    r.PolarAlignSlewDegrees = update.PolarAlignSlewDegrees;
                if (update.PolarAlignExposureSec > 0)
                    r.PolarAlignExposureSec = update.PolarAlignExposureSec;
                if (update.PolarAlignSettleSeconds >= 0)
                    r.PolarAlignSettleSeconds = update.PolarAlignSettleSeconds;
                if (update.PolarAlignGain > 0)
                    r.PolarAlignGain = update.PolarAlignGain;
                // Slew & Center plate-solve tunables (per-rig).
                if (update.SlewCenterExposureSec > 0)
                    r.SlewCenterExposureSec = update.SlewCenterExposureSec;
                if (update.SlewCenterGain > 0)
                    r.SlewCenterGain = update.SlewCenterGain;
                // OTA optics on the Main Telescope card. These are also
                // in the "lost after a while" report: a save firing while
                // settings.* hasn't re-hydrated would zero them. A zero
                // focal length / aperture is never a meaningful value, so
                // treat <= 0 as "no change" and keep the stored optic.
                if (update.FocalLengthMm > 0)
                    r.FocalLengthMm = update.FocalLengthMm;
                if (update.ApertureMm > 0)
                    r.ApertureMm = update.ApertureMm;
                // Main-camera pixel size fallback (µm). >=0 so the user can
                // clear it back to 0 ("let the camera report it"); negatives
                // from a stale client are ignored.
                if (update.CameraPixelSizeUm >= 0)
                    r.CameraPixelSizeUm = update.CameraPixelSizeUm;
                if (update.CameraMaxX >= 0) r.CameraMaxX = update.CameraMaxX;
                if (update.CameraMaxY >= 0) r.CameraMaxY = update.CameraMaxY;
                if (update.CameraBitDepth >= 0) r.CameraBitDepth = update.CameraBitDepth;
                // Telescope brand/model picker fields. Only overwrite when
                // the client actually sends a value; a blank from a stale
                // form must not wipe the saved scope name.
                if (!string.IsNullOrWhiteSpace(update.TelescopeBrand))
                    r.TelescopeBrand = update.TelescopeBrand;
                if (!string.IsNullOrWhiteSpace(update.TelescopeModel))
                    r.TelescopeModel = update.TelescopeModel;
                // Main-telescope optical accessory (reducer / flattener / barlow,
                // or "custom" with a multiplier the user typed): source-of-truth,
                // like the scope brand/model. A blank here is a client glitch (a
                // debounced optics save firing while settings.* hasn't
                // re-hydrated after a rig load/reconnect, the exact "lost my
                // reducer/accessory" report), NOT a deliberate clear, so never
                // blank-overwrite. Factor only moves with a real value.
                //
                // Removing an accessory therefore needs a word rather than an
                // absence: the type "none", which only the None option in the
                // picker sends. Old clients never send it, so nothing changes
                // for them.
                if (string.Equals(update.AccessoryType, "none", StringComparison.OrdinalIgnoreCase)) {
                    r.AccessoryType = "";
                    r.AccessoryModel = "";
                    r.AccessoryFactor = 1.0;
                } else {
                    if (!string.IsNullOrWhiteSpace(update.AccessoryType))
                        r.AccessoryType = update.AccessoryType;
                    if (!string.IsNullOrWhiteSpace(update.AccessoryModel)) {
                        r.AccessoryModel = update.AccessoryModel;
                        // A custom accessory is nothing BUT its multiplier, so an
                        // out-of-range one is worth refusing rather than storing:
                        // zero or negative would take the focal length with it.
                        if (update.AccessoryFactor > 0)
                            r.AccessoryFactor = Math.Clamp(update.AccessoryFactor, 0.05, 20.0);
                    }
                }
                r.RequiredBackspacingMm = update.RequiredBackspacingMm;
                if (update.GuiderFocalLengthMm > 0) r.GuiderFocalLengthMm = update.GuiderFocalLengthMm;
                // OAG mode: a plain bool is safe here because `update` is
                // RigPatch.Merge(stored, patch) — an omitting client yields the
                // stored value (no-op) and an explicit false is a real "OAG off".
                r.GuiderIsOag = update.GuiderIsOag;
                // OAG prism geometry (drives the guide FOV square). Both are
                // plain values off RigPatch.Merge(stored, patch), so an omitting
                // client keeps the stored value. Offset >= 0; angle wraps freely.
                if (update.OagOffsetMm >= 0) r.OagOffsetMm = update.OagOffsetMm;
                r.OagPositionAngleDeg = update.OagPositionAngleDeg;
                // Last-known guide-camera sensor geometry (for the offline FOV
                // square). Guarded so a bare/old PUT never zeroes it.
                if (update.GuiderCameraMaxX > 0) r.GuiderCameraMaxX = update.GuiderCameraMaxX;
                if (update.GuiderCameraMaxY > 0) r.GuiderCameraMaxY = update.GuiderCameraMaxY;
                if (update.GuiderCameraPixelSizeUm > 0) r.GuiderCameraPixelSizeUm = update.GuiderCameraPixelSizeUm;
                // Native guider backend selection + tunables. Empty/zero
                // from an old client leaves the existing values alone so a
                // pre-native PUT doesn't clobber the new state.
                if (!string.IsNullOrWhiteSpace(update.GuiderDriver))
                    r.GuiderDriver = update.GuiderDriver.Trim().ToLowerInvariant();
                // Guide camera selection: same source-of-truth guard as
                // the imaging camera. The operator reported it vanishing
                // "even while connected and guiding natively" -- a blank
                // here is a client glitch, not a deliberate clear.
                if (!string.IsNullOrWhiteSpace(update.GuideCamera))
                    r.GuideCamera = update.GuideCamera;
                if (!string.IsNullOrWhiteSpace(update.GuideCameraDriver))
                    r.GuideCameraDriver = update.GuideCameraDriver.Trim().ToLowerInvariant();
                // Auxiliary camera + focuser: same source-of-truth guard as the
                // guide camera (blank = client glitch, not a deliberate clear).
                if (!string.IsNullOrWhiteSpace(update.AuxCamera))
                    r.AuxCamera = update.AuxCamera;
                if (!string.IsNullOrWhiteSpace(update.AuxCameraDriver))
                    r.AuxCameraDriver = update.AuxCameraDriver.Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(update.AuxFocuser))
                    r.AuxFocuser = update.AuxFocuser;
                if (!string.IsNullOrWhiteSpace(update.AuxFocuserDriver))
                    r.AuxFocuserDriver = update.AuxFocuserDriver.Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(update.GuideFocuser))
                    r.GuideFocuser = update.GuideFocuser;
                if (!string.IsNullOrWhiteSpace(update.GuideFocuserDriver))
                    r.GuideFocuserDriver = update.GuideFocuserDriver.Trim().ToLowerInvariant();
                if (update.AuxFocalLengthMm > 0)
                    r.AuxFocalLengthMm = update.AuxFocalLengthMm;
                // Aux-camera pixel-size fallback (µm). >=0 so it can be cleared.
                if (update.AuxCameraPixelSizeUm >= 0)
                    r.AuxCameraPixelSizeUm = update.AuxCameraPixelSizeUm;
                if (update.AuxCameraMaxX >= 0) r.AuxCameraMaxX = update.AuxCameraMaxX;
                if (update.AuxCameraMaxY >= 0) r.AuxCameraMaxY = update.AuxCameraMaxY;
                if (update.AuxCameraBitDepth >= 0) r.AuxCameraBitDepth = update.AuxCameraBitDepth;
                if (update.AuxApertureMm >= 0)
                    r.AuxApertureMm = update.AuxApertureMm;
                // Aux scope brand/model: source-of-truth, never blank-overwrite.
                if (!string.IsNullOrWhiteSpace(update.AuxTelescopeBrand))
                    r.AuxTelescopeBrand = update.AuxTelescopeBrand;
                if (!string.IsNullOrWhiteSpace(update.AuxTelescopeModel))
                    r.AuxTelescopeModel = update.AuxTelescopeModel;
                if (update.AuxExposureMs > 0)
                    r.AuxExposureMs = update.AuxExposureMs;
                if (update.AuxGain >= 0)
                    r.AuxGain = update.AuxGain;
                if (update.AuxBinning > 0)
                    r.AuxBinning = Math.Clamp(update.AuxBinning, 1, 4);
                r.AuxEnabled = update.AuxEnabled;
                // NativeGuideExposureMs is deliberately NOT copied here. Its
                // writer is POST /api/guider/exposure (NativeGuider persists
                // straight to the profile); accepting it on the full-rig PUT
                // let any client holding a stale rigs list (the GUIDE dropdown
                // doesn't refresh that list) silently revert the exposure on
                // the next unrelated rig save — the "always back to 0.1 s" bug.
                if (update.NativeCalibrationStepMs > 0)
                    r.NativeCalibrationStepMs = update.NativeCalibrationStepMs;
                if (update.NativeMinMoveRaPx >= 0)
                    r.NativeMinMoveRaPx = update.NativeMinMoveRaPx;
                if (update.NativeMinMoveDecPx >= 0)
                    r.NativeMinMoveDecPx = update.NativeMinMoveDecPx;
                if (update.NativeRaAggression > 0)
                    r.NativeRaAggression = update.NativeRaAggression;
                if (update.NativeRaHysteresis >= 0)
                    r.NativeRaHysteresis = update.NativeRaHysteresis;
                if (update.NativeMaxRaDurationMs > 0)
                    r.NativeMaxRaDurationMs = update.NativeMaxRaDurationMs;
                if (update.NativeMaxDecDurationMs > 0)
                    r.NativeMaxDecDurationMs = update.NativeMaxDecDurationMs;
                if (!string.IsNullOrWhiteSpace(update.NativeRaAlgorithm))
                    r.NativeRaAlgorithm = update.NativeRaAlgorithm.Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(update.NativeDecAlgorithm))
                    r.NativeDecAlgorithm = update.NativeDecAlgorithm.Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(update.NativeDecGuideMode))
                    r.NativeDecGuideMode = update.NativeDecGuideMode.Trim().ToLowerInvariant();
                // ZFilter exposure factor: 0 means "not provided" (partial saves),
                // so only apply a real value and clamp it to the valid range.
                if (update.NativeZFilterExpFactor > 0)
                    r.NativeZFilterExpFactor = Math.Clamp(update.NativeZFilterExpFactor, 1.0, 20.0);
                r.NativeBacklashComp = update.NativeBacklashComp;
                if (update.NativeBacklashMaxMs >= 0)
                    r.NativeBacklashMaxMs = update.NativeBacklashMaxMs;
                r.NativeMultiStar = update.NativeMultiStar;
                if (update.NativeMaxGuideStars > 0)
                    r.NativeMaxGuideStars = Math.Clamp(update.NativeMaxGuideStars, 1, 12);
                if (!string.IsNullOrWhiteSpace(update.NativePierSideHandling))
                    r.NativePierSideHandling = update.NativePierSideHandling.Trim().ToLowerInvariant();
                r.NativeReverseDecAfterFlip = update.NativeReverseDecAfterFlip;
                if (update.NativeGuideGain >= 0)
                    r.NativeGuideGain = update.NativeGuideGain;
                if (update.NativeGuideBin > 0)
                    r.NativeGuideBin = Math.Clamp(update.NativeGuideBin, 1, 4);
                // Live-stack working resolution: 0 = auto (Polaris picks), else
                // 1/2/4. Anything else is ignored so a bad payload can't wedge
                // the accumulator geometry.
                if (update.LiveStackBinning is 0 or 1 or 2 or 4)
                    r.LiveStackBinning = update.LiveStackBinning;
                // New guide-scope metadata fields (RIGS tab card).
                // Defensive: clamp aperture to a sane lower bound so
                // a stray zero doesn't blow up the f-ratio calc on the UI.
                if (update.GuiderApertureMm > 0) r.GuiderApertureMm = update.GuiderApertureMm;
                // Guide scope brand/model: source-of-truth, never blank-overwrite
                // (the "lost my guide scope" report). Same blank=glitch rule.
                if (!string.IsNullOrWhiteSpace(update.GuideTelescopeBrand))
                    r.GuideTelescopeBrand = update.GuideTelescopeBrand;
                if (!string.IsNullOrWhiteSpace(update.GuideTelescopeModel))
                    r.GuideTelescopeModel = update.GuideTelescopeModel;
                r.PHD2Host = update.PHD2Host;
                r.PHD2Port = update.PHD2Port;
                // PHD2 deep-integration fields. Defensive defaults so an
                // old client (pre-PH2X) PUT-ing a rig doesn't clobber the
                // new state with zero/null.
                if (update.PHD2ProfileId.HasValue) r.PHD2ProfileId = update.PHD2ProfileId;
                if (!string.IsNullOrWhiteSpace(update.PHD2AlgoPreset))
                    r.PHD2AlgoPreset = update.PHD2AlgoPreset;
                if (update.PHD2CalibrationStepMsOverride.HasValue)
                    r.PHD2CalibrationStepMsOverride = update.PHD2CalibrationStepMsOverride;
                r.PHD2AutoSyncOnRigSwitch = update.PHD2AutoSyncOnRigSwitch;
                if (update.PHD2CustomAlgoParams != null)
                    r.PHD2CustomAlgoParams = update.PHD2CustomAlgoParams;
                r.FilterOffsets = update.FilterOffsets ?? new();
                // Live-stack triggers (LSTR-2). Defensive null check
                // keeps old clients from clobbering the field.
                if (update.LiveStackTriggers != null)
                    r.LiveStackTriggers = update.LiveStackTriggers;
                // FW-1: Flat Wizard per-rig defaults. Same defensive
                // null-check, so a pre-FW client PUT-ing a rig keeps
                // the existing FlatWizard block untouched.
                if (update.FlatWizard != null)
                    r.FlatWizard = update.FlatWizard;
                // Attached Filter (fixed LP/narrowband filter when no
                // wheel). Non-null guard: new clients always send it
                // (including "" = None, which must be allowed to clear);
                // a pre-feature client omits it and JSON binds the
                // default "", so the worst case is a no-op.
                if (update.AttachedFilter != null)
                    r.AttachedFilter = update.AttachedFilter;
                // Mirror the filter-wheel slot names on the rig so they
                // survive a driver reset (some drivers revert FILTER_NAME
                // to "Filter N" on reconnect). Non-null guard: a pre-feature
                // client omits it and JSON binds the default empty array, so
                // the worst case is a no-op that doesn't wipe saved names.
                if (update.FilterNames != null && update.FilterNames.Length > 0)
                    r.FilterNames = update.FilterNames;
                // INDIROB-3: per-device pre-connect delays. Replace
                // wholesale when supplied (operator-driven full edit
                // of the table), preserve when null/missing so a
                // partial PUT from an older client doesn't wipe out
                // delays the user set. Strip zero-value entries server-
                // side so the stored dict only carries actual
                // configured delays.
                if (update.PreConnectDelayMsByDevice != null) {
                    r.PreConnectDelayMsByDevice = update.PreConnectDelayMsByDevice
                        .Where(kv => kv.Value > 0)
                        .ToDictionary(kv => kv.Key, kv => kv.Value);
                }
                // CLST-7: live-stack compute target override. "auto"
                // (default), "server", or "client". Empty/null from
                // old clients leaves the existing setting alone.
                // Per-rig mount-slew safety thresholds. Nullable: a PUT that
                // omits them (e.g. the compute-mode-only save) leaves the
                // stored value alone; an explicit value (incl. 0 = disable)
                // is clamped to a sane range and persisted.
                if (update.SlewConfirmDeg.HasValue)
                    r.SlewConfirmDeg = Math.Clamp(update.SlewConfirmDeg.Value, 0, 180);
                if (update.SlewFloorDeg.HasValue)
                    r.SlewFloorDeg = Math.Clamp(update.SlewFloorDeg.Value, 0, 90);
                if (update.FlipFloorDeg.HasValue)
                    r.FlipFloorDeg = Math.Clamp(update.FlipFloorDeg.Value, 0, 90);
                // VIDEO tab FOV / ROI persistence. -1 leaves the field
                // untouched (lets PUTs that only update other fields
                // skip ROI), 0 clears, positive sets. Mirrors the
                // nullable-int idiom we use elsewhere for partial PUTs.
                if (update.LastVideoRoiW.HasValue) r.LastVideoRoiW = Math.Max(0, update.LastVideoRoiW.Value);
                if (update.LastVideoRoiH.HasValue) r.LastVideoRoiH = Math.Max(0, update.LastVideoRoiH.Value);
                if (update.LastVideoRoiX.HasValue) r.LastVideoRoiX = Math.Max(0, update.LastVideoRoiX.Value);
                if (update.LastVideoRoiY.HasValue) r.LastVideoRoiY = Math.Max(0, update.LastVideoRoiY.Value);
                if (update.LastVideoRoiSize.HasValue) r.LastVideoRoiSize = Math.Max(0, update.LastVideoRoiSize.Value);
                if (!string.IsNullOrWhiteSpace(update.LastVideoRoiAspect))
                    r.LastVideoRoiAspect = update.LastVideoRoiAspect;
                // SNR-3: target signal-to-noise ratio for the LIVE
                // tab's ETA-to-target widget. nullable so a PUT that
                // doesn't include it (older client / form not yet
                // edited) doesn't clobber an existing target. Clamp
                // to 0..500 so a typo doesn't break the ETA math.
                if (update.TargetSnr.HasValue) {
                    var t = update.TargetSnr.Value;
                    r.TargetSnr = t > 0 && t <= 500 ? t : (double?)null;
                }
            });
            return ok ? Results.Ok(new { message = "Rig updated" })
                      : Results.NotFound(new { error = "Rig not found" });
        });

        group.MapPost("/rigs/{id}/activate", (string id, ProfileService profiles) => {
            var ok = profiles.ActivateEquipmentProfile(id);
            return ok ? Results.Ok(new { activeId = id })
                      : Results.NotFound(new { error = "Rig not found" });
        });

        group.MapDelete("/rigs/{id}", (string id, ProfileService profiles) => {
            var ok = profiles.DeleteEquipmentProfile(id);
            return ok ? Results.Ok(new { message = "Rig deleted" })
                      : Results.BadRequest(new { error = "Rig not found or last remaining" });
        });

        // ---- FIELD4-3: per-camera-id quirks (Bayer override + flip) ----
        //
        // Lives at the user-profile level, not on EquipmentProfile,
        // so a camera that ships across multiple rigs (same SVBONY
        // moved between a refractor and a guidescope, say) gets the
        // workaround once and follows the physical sensor. Keyed on
        // EquipmentProfile.Camera (INDI device name / Alpaca
        // host:port:dev / SDK serial).

        group.MapGet("/camera-quirks", (ProfileService profiles) => {
            // Surface every camera that has either a saved quirks
            // entry OR is referenced by any rig the operator
            // configured. That way the RIGS-tab table lists rows
            // for cameras the user has used even when both toggles
            // are still default (so they can be edited from one
            // place without having to "discover" the camera first).
            var map = new Dictionary<string, CameraQuirks>(
                profiles.ListCameraQuirks().ToDictionary(kv => kv.Key, kv => kv.Value));
            foreach (var rig in profiles.ListEquipmentProfiles()) {
                if (string.IsNullOrWhiteSpace(rig.Camera)) continue;
                if (!map.ContainsKey(rig.Camera))
                    map[rig.Camera] = new CameraQuirks();
            }
            return Results.Ok(new {
                activeCameraId = profiles.ActiveEquipmentProfile?.Camera,
                cameras = map.Select(kv => new {
                    cameraId = kv.Key,
                    bayerPatternOverride = kv.Value.BayerPatternOverride,
                    verticalFlipImage = kv.Value.VerticalFlipImage,
                    bayerOffsetX = kv.Value.BayerOffsetX,
                    bayerOffsetY = kv.Value.BayerOffsetY
                }).OrderBy(c => c.cameraId).ToList()
            });
        });

        group.MapPut("/camera-quirks/{cameraId}",
                (string cameraId, CameraQuirksUpdate update, ProfileService profiles) => {
            if (string.IsNullOrWhiteSpace(cameraId))
                return Results.BadRequest(new { error = "Camera id required" });
            profiles.UpdateSettings(p => {
                if (!p.CameraQuirks.TryGetValue(cameraId, out var q)) {
                    q = new CameraQuirks();
                    p.CameraQuirks[cameraId] = q;
                }
                // Empty/whitespace = "Auto" sentinel, store as null
                // so ResolveBayerOverride honours the driver. Other
                // values normalise to upper-case so RGGB / rggb /
                // RgGb all round-trip identically.
                q.BayerPatternOverride = string.IsNullOrWhiteSpace(update.BayerPatternOverride)
                    ? null : update.BayerPatternOverride.Trim().ToUpperInvariant();
                q.VerticalFlipImage = update.VerticalFlipImage;
                q.BayerOffsetX = Math.Clamp(update.BayerOffsetX, 0, 1);
                q.BayerOffsetY = Math.Clamp(update.BayerOffsetY, 0, 1);
            });
            var saved = profiles.GetOrCreateCameraQuirks(cameraId);
            return Results.Ok(new {
                cameraId,
                bayerPatternOverride = saved.BayerPatternOverride,
                verticalFlipImage = saved.VerticalFlipImage,
                bayerOffsetX = saved.BayerOffsetX,
                bayerOffsetY = saved.BayerOffsetY
            });
        });
    }

    public record CreateRigRequest(string Name);
    public record CloneRigRequest(string NewName);

    /// <summary>FIELD4-3: PUT body for /api/equipment/camera-quirks/{cameraId}.
    /// Either field may be omitted -- omitted bool defaults to false,
    /// omitted string defaults to null. The endpoint REPLACES the
    /// quirks entry wholesale rather than merging, which keeps the
    /// UI simple (table is the source of truth, no hidden state).</summary>
    public record CameraQuirksUpdate(
        string? BayerPatternOverride,
        bool VerticalFlipImage,
        int BayerOffsetX = 0,
        int BayerOffsetY = 0);
}