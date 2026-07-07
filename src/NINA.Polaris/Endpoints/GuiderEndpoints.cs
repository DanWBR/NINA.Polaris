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

public static class GuiderEndpoints {
    public static void MapGuiderEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/guider");

        group.MapGet("/status", (ActiveGuiderProvider guiders, PHD2Client phd2, ProfileService profiles) => {
            var g = guiders.Active;
            var rig = profiles.ActiveEquipmentProfile;
            if (!g.IsConnected)
                return Results.Ok(new {
                    backend = g.Backend,
                    connected = false,
                    appState = "Stopped",
                    raAggression = rig?.NativeRaAggression ?? 0.70,
                    decAggression = rig?.NativeDecAggression ?? 0.70
                });

            return Results.Ok(new {
                backend = g.Backend,
                connected = true,
                appState = g.AppState,
                guiding = g.IsGuiding,
                calibrating = g.IsCalibrating,
                paused = g.IsPaused,
                looping = g.IsLooping,
                settling = g.IsSettling,
                dithering = g.IsDithering,
                pixelScale = g.PixelScale,
                rmsRA = g.RmsRA,
                rmsDec = g.RmsDec,
                rmsTotal = g.RmsTotal,
                peakRA = g.PeakRA,
                peakDec = g.PeakDec,
                stepCount = g.SnapshotSteps().Count,
                lastAlert = g.LastAlert,
                lastAlertAt = g.LastAlertAt,
                lastSettleStatus = g.LastSettleStatus,
                // RA/Dec aggression (0..2) so the UI can show + edit the
                // ASIAIR-style 10..150% sliders. Profile-backed, applies live.
                raAggression = rig?.NativeRaAggression ?? 0.70,
                decAggression = rig?.NativeDecAggression ?? 0.70,
                // PHD2-only calibration snapshot (null when native).
                calibration = g.Backend == "phd2" ? phd2.Calibration : null
            });
        });

        // RA/Dec aggression (0.1..1.5 typical; clamp 0..2). Persisted on the
        // active rig and applied live to the native guider's algorithms.
        group.MapPut("/settings/aggression", (AggressionDto dto,
                ActiveGuiderProvider guiders, ProfileService profiles) => {
            var rig = profiles.ActiveEquipmentProfile;
            if (rig == null) return Results.BadRequest(new { error = "No active rig." });
            double ra = Math.Clamp(dto.Ra, 0.0, 2.0);
            double dec = Math.Clamp(dto.Dec, 0.0, 2.0);
            profiles.UpdateEquipmentProfile(rig.Id, r => {
                r.NativeRaAggression = ra;
                r.NativeDecAggression = dec;
            });
            // Apply to the running native guider without a restart.
            (guiders.Active as NativeGuider)?.ApplyAlgorithmSettings();
            return Results.Ok(new { raAggression = ra, decAggression = dec });
        });

        // Predictive (PE + drift) tuning for the native guider: worm period
        // (s; 0 = auto-estimate), history window (samples), feed-forward blend
        // (0..1). Persisted on the active rig + applied live.
        group.MapPut("/settings/predictive", (PredictiveDto dto,
                ActiveGuiderProvider guiders, ProfileService profiles) => {
            var rig = profiles.ActiveEquipmentProfile;
            if (rig == null) return Results.BadRequest(new { error = "No active rig." });
            double worm = Math.Max(0.0, dto.WormPeriodSec);
            int window = Math.Clamp(dto.WindowSamples, 32, 4096);
            double blend = Math.Clamp(dto.Blend, 0.0, 1.0);
            profiles.UpdateEquipmentProfile(rig.Id, r => {
                r.NativePredictiveWormPeriodSec = worm;
                r.NativePredictiveWindowSamples = window;
                r.NativePredictiveBlend = blend;
            });
            (guiders.Active as NativeGuider)?.ApplyAlgorithmSettings();
            return Results.Ok(new { wormPeriodSec = worm, windowSamples = window, blend });
        });

        // ----- Native dark library / bad-pixel map -----
        // Per-rig calibration mode: off | dark | bpm | both. Persists on the
        // active rig and reloads the in-memory artifacts on the running guider.
        group.MapPost("/calibration/mode", (ModeBody body,
                ActiveGuiderProvider guiders, ProfileService profiles) => {
            var mode = (body.Mode ?? "off").Trim().ToLowerInvariant();
            if (mode is not ("off" or "dark" or "bpm" or "both"))
                return Results.BadRequest(new { error = "mode must be off | dark | bpm | both" });
            var rig = profiles.ActiveEquipmentProfile;
            profiles.UpdateEquipmentProfile(rig.Id, r => r.NativeGuideCalibrationMode = mode);
            (guiders.Active as NativeGuider)?.ReloadGuideCalibration();
            return Results.Ok(new { mode });
        });

        // Optional: number of darks to average on the next build.
        group.MapPost("/calibration/frames", (FramesBody body, ProfileService profiles) => {
            int n = Math.Clamp(body.Frames, 1, 100);
            var rig = profiles.ActiveEquipmentProfile;
            profiles.UpdateEquipmentProfile(rig.Id, r => r.NativeGuideDarkFrames = n);
            return Results.Ok(new { frames = n });
        });

        // Capture darks + derive the bad-pixel map for the current
        // exposure/gain/bin. Native backend only; runs in the background and
        // reports via the WS guider.darkCalibration block.
        group.MapPost("/calibration/build", async (ActiveGuiderProvider guiders) => {
            if (guiders.Active is not NativeGuider ng)
                return Results.BadRequest(new { error = "Dark library is only available on the native guider." });
            await ng.StartBuildCalibrationAsync();
            return Results.Accepted("/api/guider/calibration/build", new { started = true });
        });

        group.MapPost("/calibration/cancel", (ActiveGuiderProvider guiders) => {
            (guiders.Active as NativeGuider)?.CancelBuildCalibration();
            return Results.Ok(new { cancelled = true });
        });

        group.MapPost("/calibration/clear", (ActiveGuiderProvider guiders) => {
            if (guiders.Active is not NativeGuider ng)
                return Results.BadRequest(new { error = "Dark library is only available on the native guider." });
            ng.ClearGuideCalibration();
            return Results.Ok(new { cleared = true });
        });

        group.MapGet("/equipment", async (PHD2Client phd2) => {
            if (!phd2.IsConnected)
                return Results.Ok(new { connected = false });
            var equip = await phd2.GetCurrentEquipmentAsync();
            return Results.Ok(new {
                connected = true,
                camera = equip?.Camera,
                mount = equip?.Mount,
                auxMount = equip?.AuxMount,
                ao = equip?.AO
            });
        });

        group.MapGet("/steps", (ActiveGuiderProvider guiders, int? limit) => {
            var snapshot = guiders.Active.SnapshotSteps();
            var take = limit.HasValue && limit.Value > 0 ? Math.Min(limit.Value, snapshot.Count) : snapshot.Count;
            var slice = snapshot.Skip(Math.Max(0, snapshot.Count - take)).Select(s => new {
                t = ((DateTimeOffset)s.Timestamp).ToUnixTimeMilliseconds(),
                ra = s.RaArcsec,
                dec = s.DecArcsec,
                snr = s.SNR
            });
            return Results.Ok(new { count = snapshot.Count, steps = slice });
        });

        // Latest native guide-camera frame as an auto-stretched JPEG for the
        // PHD2-style camera view. 404 for the PHD2 backend (it renders its own
        // GUI) or before the first frame is captured.
        group.MapGet("/frame.jpg", (ActiveGuiderProvider guiders, int? max, int? q, double? gamma) => {
            if (guiders.Active is not NativeGuider ng)
                return Results.NotFound();
            var bytes = ng.EncodeViewJpeg(
                max is > 0 ? Math.Clamp(max.Value, 128, 2048) : 600,
                q is > 0 ? Math.Clamp(q.Value, 30, 95) : 75,
                // PHD2-style display gamma (0.10–3.00, 1.0 = linear default).
                gamma is > 0 ? Math.Clamp(gamma.Value, 0.10, 3.00) : 1.0);
            if (bytes == null) return Results.NotFound();
            return Results.File(bytes, "image/jpeg");
        });

        // ----- Native guide-camera selection + connection -----
        // Mirrors the imaging-camera select/connect/disconnect so the guide
        // camera has its own connect switch independent of starting guiding.
        group.MapPost("/camera/select/{deviceName}", (EquipmentManager equip, string deviceName, string? driver) => {
            try {
                equip.SelectGuideCamera(driver ?? "indi", deviceName);
                return Results.Ok(new { selected = deviceName, driver = driver ?? "indi" });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/camera/connect", async (EquipmentManager equip) => {
            if (equip.GuideCamera == null)
                return Results.BadRequest(new { error = "No guide camera selected. Use POST /api/guider/camera/select/{name} first" });
            await equip.GuideCamera.ConnectAsync();
            return Results.Ok(new { status = "connected", device = equip.GuideCamera.DeviceName });
        });

        group.MapPost("/camera/disconnect", async (EquipmentManager equip) => {
            if (equip.GuideCamera == null)
                return Results.Ok(new { status = "disconnected" });
            await equip.GuideCamera.DisconnectAsync();
            return Results.Ok(new { status = "disconnected" });
        });

        // ----- Guide-scope focuser selection + connection + manual jog -----
        // Some setups motorise the guide scope. Mirrors the aux focuser surface
        // (see AuxEndpoints); a separate slot from the imaging + aux focusers.
        group.MapGet("/focuser/discover", (EquipmentManager equip, string? driver)
            => Results.Ok(equip.GetDiscoveredFocusersFor(driver ?? "indi")));

        group.MapPost("/focuser/select/{deviceName}", (EquipmentManager equip,
                string deviceName, string? driver) => {
            try {
                equip.SelectGuideFocuser(driver ?? "indi", deviceName);
                return Results.Ok(new { selected = deviceName, driver = driver ?? "indi" });
            } catch (NotSupportedException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/focuser/connect", async (EquipmentManager equip) => {
            if (equip.GuideFocuser == null)
                return Results.BadRequest(new { error = "No guide focuser selected" });
            await equip.GuideFocuser.ConnectAsync();
            return Results.Ok(new { status = "connected", device = equip.GuideFocuser.DeviceName });
        });

        group.MapPost("/focuser/disconnect", async (EquipmentManager equip) => {
            if (equip.GuideFocuser == null)
                return Results.Ok(new { status = "disconnected" });
            await equip.GuideFocuser.DisconnectAsync();
            return Results.Ok(new { status = "disconnected" });
        });

        group.MapPost("/focuser/move/absolute", async (EquipmentManager equip,
                FocuserEndpoints.MoveAbsoluteRequest request) => {
            if (equip.GuideFocuser == null)
                return Results.BadRequest(new { error = "No guide focuser selected" });
            await equip.GuideFocuser.MoveAbsoluteAsync(request.Position);
            return Results.Ok(new { status = "moving", target = request.Position });
        });

        group.MapPost("/focuser/move/relative", async (EquipmentManager equip,
                FocuserEndpoints.MoveRelativeRequest request) => {
            if (equip.GuideFocuser == null)
                return Results.BadRequest(new { error = "No guide focuser selected" });
            await equip.GuideFocuser.MoveRelativeAsync(request.Steps);
            return Results.Ok(new { status = "moving", steps = request.Steps });
        });

        group.MapPost("/focuser/abort", async (EquipmentManager equip) => {
            if (equip.GuideFocuser == null)
                return Results.BadRequest(new { error = "No guide focuser selected" });
            await equip.GuideFocuser.AbortAsync();
            return Results.Ok(new { status = "stopped" });
        });

        // Lock the guide star nearest a clicked point (native guider only).
        group.MapPost("/select-star", async (ActiveGuiderProvider guiders, SelectStarRequest req) => {
            if (guiders.Active is not NativeGuider ng)
                return Results.BadRequest(new { error = "Click-to-select is only available on the native guider." });
            try {
                await ng.SelectStarNearAsync(req.X, req.Y);
                return Results.Ok(new { ok = true });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/connect", async (ActiveGuiderProvider guiders, ConnectGuiderRequest? request) => {
            var g = guiders.Active;
            var host = string.IsNullOrWhiteSpace(request?.Host) ? "localhost" : request!.Host!;
            var port = request?.Port is > 0 ? request.Port!.Value : 4400;
            try {
                await g.ConnectAsync(host, port);
                return Results.Ok(new { status = "connected", backend = g.Backend, host, port, appState = g.AppState });
            } catch (Exception ex) {
                return Results.Problem($"Guider connect failed: {ex.Message}");
            }
        });

        group.MapPost("/disconnect", async (ActiveGuiderProvider guiders) => {
            await guiders.Active.DisconnectAsync();
            return Results.Ok(new { status = "disconnected" });
        });

        group.MapPost("/guide", (ActiveGuiderProvider guiders,
                Microsoft.Extensions.Logging.ILoggerFactory lf, GuideRequest? request) => {
            var g = guiders.Active;
            if (!g.IsConnected) return Results.BadRequest(new { error = "Guider not connected" });
            // Calibration + settle routinely takes well over a minute, far longer
            // than the client's request timeout. Awaiting it here makes the POST
            // abort client-side ("Request timed out" / AbortError) even though the
            // server is fine. Kick it off in the background and return immediately;
            // progress and the final result surface over the status WebSocket
            // (appState / calProgress / calDetails / lastAlert). /stop cancels it.
            _ = Task.Run(async () => {
                try {
                    await g.StartGuidingAsync(
                        settlePixels: request?.SettlePixels ?? 1.5,
                        settleTime: request?.SettleTime ?? 10,
                        settleTimeout: request?.SettleTimeout ?? 40,
                        recalibrate: request?.Recalibrate ?? false);
                } catch (Exception ex) {
                    lf.CreateLogger("Guider").LogWarning(ex, "Background StartGuiding failed");
                }
            });
            return Results.Ok(new { status = "guide_starting" });
        });

        group.MapPost("/stop", async (ActiveGuiderProvider guiders) => {
            var g = guiders.Active;
            if (!g.IsConnected) return Results.BadRequest(new { error = "Guider not connected" });
            try {
                await g.StopAsync();
                return Results.Ok(new { status = "stopped" });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/loop", async (ActiveGuiderProvider guiders) => {
            var g = guiders.Active;
            if (!g.IsConnected) return Results.BadRequest(new { error = "Guider not connected" });
            try {
                await g.LoopAsync();
                return Results.Ok(new { status = "looping" });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/pause", async (ActiveGuiderProvider guiders) => {
            var g = guiders.Active;
            if (!g.IsConnected) return Results.BadRequest(new { error = "Guider not connected" });
            try {
                await g.PauseAsync();
                return Results.Ok(new { status = "paused" });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/resume", async (ActiveGuiderProvider guiders) => {
            var g = guiders.Active;
            if (!g.IsConnected) return Results.BadRequest(new { error = "Guider not connected" });
            try {
                await g.ResumeAsync();
                return Results.Ok(new { status = "resumed" });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/dither", async (ActiveGuiderProvider guiders, DitherRequest? request) => {
            var g = guiders.Active;
            if (!g.IsConnected) return Results.BadRequest(new { error = "Guider not connected" });
            try {
                await g.DitherAsync(
                    pixels: request?.Pixels ?? 5.0,
                    raOnly: request?.RaOnly ?? false,
                    settlePixels: request?.SettlePixels ?? 1.5,
                    settleTime: request?.SettleTime ?? 10,
                    settleTimeout: request?.SettleTimeout ?? 40);
                return Results.Ok(new { status = "dither_requested" });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/exposure/{ms:int}", async (int ms, ActiveGuiderProvider guiders) => {
            var g = guiders.Active;
            if (!g.IsConnected) return Results.BadRequest(new { error = "Guider not connected" });
            try {
                await g.SetExposureAsync(ms);
                return Results.Ok(new { exposure = ms });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/find-star", async (ActiveGuiderProvider guiders) => {
            var g = guiders.Active;
            if (!g.IsConnected) return Results.BadRequest(new { error = "Guider not connected" });
            try {
                await g.AutoSelectStarAsync();
                return Results.Ok(new { status = "find_star" });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/clear-calibration", async (ActiveGuiderProvider guiders) => {
            var g = guiders.Active;
            if (!g.IsConnected) return Results.BadRequest(new { error = "Guider not connected" });
            try {
                await g.ClearCalibrationAsync();
                return Results.Ok(new { status = "calibration_cleared" });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/flip-calibration", async (ActiveGuiderProvider guiders) => {
            var g = guiders.Active;
            if (!g.IsConnected) return Results.BadRequest(new { error = "Guider not connected" });
            try {
                await g.FlipCalibrationAsync();
                return Results.Ok(new { status = "calibration_flipped" });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/clear-history", (ActiveGuiderProvider guiders) => {
            guiders.Active.ClearStepHistory();
            return Results.Ok(new { status = "cleared" });
        });

        // ---- Profile management ----

        group.MapGet("/profiles", async (PHD2Client phd2) => {
            if (!phd2.IsConnected) {
                // Match the connected-but-empty shape so the UI doesn't
                // have to special-case "not connected", empty list +
                // null current is the right thing to show.
                return Results.Ok(new {
                    current = (PHD2Profile?)null,
                    profiles = System.Array.Empty<PHD2Profile>(),
                    warning = "PHD2 not connected"
                });
            }
            try {
                var profiles = await phd2.GetProfilesAsync();
                var current = await phd2.GetCurrentProfileAsync();
                return Results.Ok(new { current, profiles });
            } catch (Exception ex) {
                // PHD2 can transiently reject get_profile{,s} when busy
                // (mid-calibration, equipment in flux). Don't turn that
                // into a 500, the frontend polls this on every WS
                // false→true transition + on user actions, and a 500
                // surfaces as a scary "Failed to load PHD2 profiles"
                // toast even though the connection is fine. Return
                // empty + warning instead so the dropdown shows blank
                // and self-heals on the next successful fetch.
                return Results.Ok(new {
                    current = (PHD2Profile?)null,
                    profiles = System.Array.Empty<PHD2Profile>(),
                    warning = ex.Message
                });
            }
        });

        group.MapPost("/profile/{id:int}", async (int id, PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            try {
                await phd2.SetProfileAsync(id);
                return Results.Ok(new { status = "profile_switched", profileId = id });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        // ---- Equipment connect/disconnect (PHD2's own equipment) ----

        group.MapGet("/equipment/connected", async (PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.Ok(new { connected = false });
            try { return Results.Ok(new { connected = await phd2.GetConnectedAsync() }); }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/equipment/connect", async (PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            try { await phd2.SetConnectedAsync(true); return Results.Ok(new { connected = true }); }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/equipment/disconnect", async (PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            try { await phd2.SetConnectedAsync(false); return Results.Ok(new { connected = false }); }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        // ---- Exposure ----

        group.MapGet("/exposure", async (PHD2Client phd2) => {
            // "Not connected" is a normal state for a status probe (e.g. the
            // native guider is active, or PHD2 hasn't been launched yet), not a
            // client error — return 200 so it doesn't spam the debug log.
            if (!phd2.IsConnected) return Results.Ok(new { connected = false });
            try {
                var current = await phd2.GetExposureAsync();
                var available = await phd2.GetExposureDurationsAsync();
                return Results.Ok(new { current, available });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/exposure/set/{ms:int}", async (int ms, PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            try {
                await phd2.SetExposureMsAsync(ms);
                return Results.Ok(new { exposure = ms });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        // ---- Dec guide mode ----

        group.MapGet("/dec-mode", async (PHD2Client phd2) => {
            // Status probe — benign when PHD2 isn't connected (see /exposure).
            if (!phd2.IsConnected) return Results.Ok(new { connected = false });
            try { return Results.Ok(new { mode = await phd2.GetDecGuideModeAsync() }); }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/dec-mode/{mode}", async (string mode, PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            if (mode is not ("Auto" or "North" or "South" or "Off"))
                return Results.BadRequest(new { error = "mode must be Auto/North/South/Off" });
            try { await phd2.SetDecGuideModeAsync(mode); return Results.Ok(new { mode }); }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        // ---- Process lifecycle (launch / shutdown PHD2 itself) ----

        group.MapGet("/process/status", async (PHD2ProcessManager pm) => Results.Ok(new {
            executableConfigured = pm.ExecutableConfigured,
            executablePath = pm.ExecutablePath,
            running = await pm.IsRunningAsync(),
            weStartedIt = pm.WeStartedIt
        }));

        // Detected install info, the UI uses this on startup to either show
        // "PHD2 detected at <path>" or "PHD2 not installed, download here".
        group.MapGet("/install-info", (PHD2ProcessManager pm, IConfiguration config) => {
            var configured = config.GetValue<string?>("PHD2:ExecutablePath");
            var resolved = pm.ExecutablePath;
            var installed = pm.ExecutableConfigured;
            var os = OperatingSystem.IsWindows() ? "windows"
                  : OperatingSystem.IsMacOS() ? "macos"
                  : "linux";
            return Results.Ok(new {
                installed,
                resolvedPath = resolved,
                configuredPath = configured,
                autoStart = config.GetValue("PHD2:AutoStart", false),
                host = pm.DefaultHost,
                port = pm.DefaultPort,
                instanceNumber = pm.InstanceNumber,
                downloadUrl = PHD2ProcessManager.GetDownloadUrl(),
                os,
                searchedPaths = PHD2ProcessManager.EnumerateCandidatePaths().ToArray()
            });
        });

        // Toggle PHD2:AutoStart at runtime. Persisted via ProfileService so
        // the choice survives restarts. Takes effect on next app start (and
        // the user can /process/launch right now for the current session).
        group.MapPost("/auto-start/{enabled:bool}", (bool enabled, ProfileService profiles) => {
            profiles.Active.PHD2AutoStart = enabled;
            profiles.Save();
            return Results.Ok(new { autoStart = enabled });
        });

        group.MapPost("/process/launch", async (PHD2ProcessManager pm) => {
            try {
                var ok = await pm.LaunchAsync();
                return Results.Ok(new { launched = ok, running = await pm.IsRunningAsync() });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/process/shutdown", async (PHD2ProcessManager pm, PHD2Client phd2) => {
            try {
                var ok = await pm.ShutdownAsync(phd2);
                return Results.Ok(new { stopped = ok });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        // ----- PH2X-3: rig <-> PHD2 profile sync -----

        // Force a sync of a specific rig (or the active rig if rigId
        // omitted) to PHD2. Used by the UI button "Sync now" and as a
        // manual recovery after the user creates a profile in PHD2's GUI.
        group.MapPost("/profile/sync", async (SyncProfileRequest? req,
                                              PHD2ProfileSyncService sync,
                                              ProfileService profiles) => {
            var rigId = req?.RigId;
            var rig = string.IsNullOrEmpty(rigId)
                ? profiles.ActiveEquipmentProfile
                : profiles.ListEquipmentProfiles().FirstOrDefault(r => r.Id == rigId);
            if (rig == null) return Results.NotFound(new { error = "Rig not found" });
            var result = await sync.SyncRigToProfileAsync(rig, CancellationToken.None);
            return Results.Ok(result);
        });

        // Read-only, what's the last sync status? UI shows the indicator
        // chip (ok/error/missing-profile) based on this.
        group.MapGet("/profile/sync/status", (PHD2ProfileSyncService sync) =>
            Results.Ok(sync.CurrentStatus));

        // ----- PH2X-4: Smart calibration orchestrator -----

        // Kick a fresh PHD2 calibration. Body is SmartCalibrateOptions
        // (all fields optional with sensible defaults, see record).
        group.MapPost("/calibrate/smart", (SmartCalibrateOptions? opts,
                                          PHD2CalibrationOrchestrator orch) => {
            var job = orch.StartJob(opts ?? new SmartCalibrateOptions());
            return Results.Accepted($"/api/guider/calibrate/smart/{job.Id}",
                new { jobId = job.Id, phase = job.State.ToString() });
        });

        group.MapGet("/calibrate/smart/{jobId}", (string jobId,
                                                  PHD2CalibrationOrchestrator orch) => {
            var job = orch.GetJob(jobId);
            if (job == null) return Results.NotFound(new { error = "Job not found" });
            return Results.Ok(new {
                id = job.Id,
                phase = job.State.ToString(),
                pixelScale = job.PixelScale,
                calibrationStepMs = job.CalibrationStepMs,
                calibration = job.Calibration,
                error = job.Error,
                lastAlert = job.LastAlert,
                warnings = job.Warnings,
                startedAt = job.StartedAt,
                completedAt = job.CompletedAt,
                done = job.State == CalibrationPhase.Ok || job.State == CalibrationPhase.Fail
            });
        });

        group.MapPost("/calibrate/smart/{jobId}/abort", (string jobId,
                                                         PHD2CalibrationOrchestrator orch) => {
            orch.Abort(jobId);
            return Results.Ok(new { aborted = true });
        });

        // ----- PH2X-5: Algorithm presets + live param tuning -----

        // Built-in presets table (Default / Reactive / Smooth). UI populates
        // the preset pill from this. "Custom" is implicit, a rig with a
        // populated PHD2CustomAlgoParams bag.
        group.MapGet("/algo-presets", () => Results.Ok(new {
            names = PHD2AlgoPresets.BuiltinNames,
            presets = PHD2AlgoPresets.BuiltinNames.Select(n => {
                var p = PHD2AlgoPresets.GetBuiltin(n)!;
                return new {
                    name = p.Name,
                    description = p.Description,
                    @params = p.Params.Select(x => new { axis = x.Axis, name = x.Name, value = x.Value })
                };
            })
        }));

        // Apply a preset to the live PHD2 + persist as the active rig's
        // PHD2AlgoPreset. Works even when PHD2 is mid-guiding (preset
        // tweaks take effect on the next correction).
        group.MapPost("/algo-preset/{name}", async (string name,
                                                    PHD2Client phd2,
                                                    ProfileService profiles) => {
            if (!phd2.IsConnected)
                return Results.BadRequest(new { error = "PHD2 not connected" });
            var rig = profiles.ActiveEquipmentProfile;
            // "Custom" → apply the per-rig bag; built-in → apply curated table.
            var warnings = new List<string>();
            if (string.Equals(name, PHD2AlgoPresets.CustomPresetName, StringComparison.OrdinalIgnoreCase)) {
                foreach (var kv in rig.PHD2CustomAlgoParams) {
                    var sep = kv.Key.IndexOf(':');
                    if (sep <= 0) continue;
                    var ok = await phd2.SetAlgoParamAsync(kv.Key[..sep], kv.Key[(sep + 1)..], kv.Value);
                    if (!ok) warnings.Add($"Skipped {kv.Key}");
                }
            } else {
                var preset = PHD2AlgoPresets.GetBuiltin(name);
                if (preset == null) return Results.BadRequest(new { error = $"Unknown preset '{name}'" });
                foreach (var p in preset.Params) {
                    var ok = await phd2.SetAlgoParamAsync(p.Axis, p.Name, p.Value);
                    if (!ok) warnings.Add($"Skipped {p.Axis}/{p.Name}");
                }
            }
            profiles.UpdateEquipmentProfile(rig.Id, r => r.PHD2AlgoPreset = name);
            return Results.Ok(new { applied = name, warnings });
        });

        // Read live algorithm-parameter values from PHD2 for both axes.
        // UI's Advanced disclosure lists these knobs with current values.
        group.MapGet("/algo-params", async (PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.Ok(new { connected = false });
            var axes = new[] { "ra", "dec" };
            var result = new Dictionary<string, Dictionary<string, double?>>();
            foreach (var axis in axes) {
                var names = await phd2.GetAlgoParamNamesAsync(axis);
                var bag = new Dictionary<string, double?>();
                foreach (var n in names) bag[n] = await phd2.GetAlgoParamAsync(axis, n);
                result[axis] = bag;
            }
            return Results.Ok(new { connected = true, axes = result });
        });

        // Set a single live algo param + persist into the rig's custom bag
        // (and flip preset to "Custom" so the user knows they've diverged
        // from a built-in).
        group.MapPut("/algo-params", async (AlgoParamRequest req,
                                            PHD2Client phd2,
                                            ProfileService profiles) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            if (string.IsNullOrEmpty(req.Axis) || string.IsNullOrEmpty(req.Name))
                return Results.BadRequest(new { error = "axis + name required" });
            var ok = await phd2.SetAlgoParamAsync(req.Axis, req.Name, req.Value);
            if (!ok) return Results.BadRequest(new {
                error = $"PHD2 rejected {req.Axis}/{req.Name}, algorithm may not expose it" });
            var rig = profiles.ActiveEquipmentProfile;
            profiles.UpdateEquipmentProfile(rig.Id, r => {
                r.PHD2CustomAlgoParams[$"{req.Axis}:{req.Name}"] = req.Value;
                r.PHD2AlgoPreset = PHD2AlgoPresets.CustomPresetName;
            });
            return Results.Ok(new { applied = true });
        });

        // ----- PH2X-6: xpra-hosted PHD2 GUI session lifecycle -----

        group.MapGet("/gui-session/status", (Phd2GuiSessionService gui) => Results.Ok(new {
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            supportedOs = gui.IsSupportedOs,
            // Field names MUST match the /ws/status guider.guiSession payload
            // (running/port/supportedArch/unsupportedReason). The frontend
            // and the WS both populate the same phd2GuiSession object, so a
            // divergent REST shape here clobbered supportedArch/running with
            // undefined on every poll, making the panel flip between
            // "Not supported on this CPU architecture" and "session not
            // running". Canonical names below; legacy aliases kept too.
            supportedArch = gui.IsSupportedArch,
            unsupportedReason = gui.UnsupportedReason,
            xpraInstalled = gui.XpraInstalled,
            xpraVersion = gui.XpraVersion,
            xpraPath = gui.XpraPath,
            running = gui.SessionRunning,
            port = gui.BindPort,
            phd2Running = gui.Phd2Running,
            displayNumber = gui.DisplayNumber,
            lastHealthCheckAt = gui.LastHealthCheckAt,
            lastError = gui.LastError,
            // Legacy aliases (older callers / tests).
            sessionRunning = gui.SessionRunning,
            bindPort = gui.BindPort,
            // Hint URL the UI iframes, points to the Polaris reverse-proxy
            // so it stays same-origin (sessionStorage works there).
            embedUrl = "/phd2-gui/"
        }));

        group.MapPost("/gui-session/start", async (Phd2GuiSessionService gui) => {
            if (!gui.IsSupportedOs)
                return Results.Json(new { error = "Embedded PHD2 GUI requires Linux + xpra" },
                    statusCode: 501);
            if (!gui.XpraInstalled)
                return Results.Json(new { error = "xpra not installed. Run: sudo apt install xpra xserver-xorg-video-dummy" },
                    statusCode: 501);
            var ok = await gui.StartSessionAsync();
            return Results.Ok(new { running = ok, error = ok ? null : gui.LastError });
        });

        group.MapPost("/gui-session/stop", async (Phd2GuiSessionService gui) => {
            if (!gui.IsSupportedOs || !gui.XpraInstalled)
                return Results.Json(new { error = "Not supported" }, statusCode: 501);
            var ok = await gui.StopSessionAsync();
            return Results.Ok(new { stopped = ok, error = ok ? null : gui.LastError });
        });

        group.MapPost("/gui-session/restart", async (Phd2GuiSessionService gui) => {
            if (!gui.IsSupportedOs || !gui.XpraInstalled)
                return Results.Json(new { error = "Not supported" }, statusCode: 501);
            var ok = await gui.RestartSessionAsync();
            return Results.Ok(new { running = ok, error = ok ? null : gui.LastError });
        });

        // Relaunch PHD2 inside the existing xpra session without
        // tearing down xpra. UI surfaces this as the "Relaunch PHD2"
        // button when xpra is up but the phd2 process is missing
        // (most commonly because xpra's '--start=phd2' failed silently
        // at session-start time on a host where PHD2 was not yet
        // installed, or because PHD2 crashed mid-session).
        group.MapPost("/gui-session/relaunch-phd2", async (Phd2GuiSessionService gui) => {
            if (!gui.IsSupportedOs || !gui.XpraInstalled)
                return Results.Json(new { error = "Not supported" }, statusCode: 501);
            var ok = await gui.RelaunchPhd2Async();
            return Results.Ok(new { phd2Running = ok, error = ok ? null : gui.LastError });
        });

        // ----- PH2VNC-4: Windows TightVNC + noVNC bridge lifecycle -----
        // Sibling of /gui-session/* above. Same shape, different
        // backend: xpra forwards an X display on Linux, TightVNC's
        // Windows service captures the desktop and we bridge its
        // RFB stream to a noVNC HTML5 client.

        group.MapGet("/vnc-session/status", (Phd2VncSessionService vnc) => Results.Ok(new {
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            supportedOs = vnc.IsSupportedOs,
            unsupportedReason = vnc.UnsupportedReason,
            tightVncInstalled = vnc.TightVncInstalled,
            tightVncVersion = vnc.TightVncVersion,
            tightVncPath = vnc.TightVncPath,
            serviceInstalled = vnc.ServiceInstalled,
            serviceRunning = vnc.ServiceRunning,
            listening = vnc.Listening,
            port = vnc.Port,
            lastHealthCheckAt = vnc.LastHealthCheckAt,
            lastError = vnc.LastError,
            // Hint URL the UI iframes; lives under the Polaris
            // listener so the AuthMiddleware (Bearer / cookie) covers
            // it the same way it covers /phd2-gui/.
            embedUrl = "/phd2-vnc/",
            // Download link surfaced in the "not installed" banner.
            // Pinned to the canonical project page so the user
            // grabs the official MSI, not a mirror.
            downloadUrl = "https://www.tightvnc.com/download.php"
        }));

        // Re-run detection on demand. UI fires this from the
        // Settings card's "Re-detect" button after the user
        // installs / uninstalls TightVNC without restarting Polaris.
        group.MapPost("/vnc-session/redetect", async (Phd2VncSessionService vnc) => {
            await vnc.RefreshDetectionAsync();
            return Results.Ok(new {
                supportedOs = vnc.IsSupportedOs,
                tightVncInstalled = vnc.TightVncInstalled,
                tightVncVersion = vnc.TightVncVersion,
                serviceRunning = vnc.ServiceRunning,
                listening = vnc.Listening,
                lastError = vnc.LastError
            });
        });

        group.MapPost("/vnc-session/start-service", async (Phd2VncSessionService vnc) => {
            if (!vnc.IsSupportedOs)
                return Results.Json(new { error = vnc.UnsupportedReason ?? "Not supported" },
                    statusCode: 501);
            if (!vnc.TightVncInstalled)
                return Results.Json(new {
                    error = "TightVNC not installed. Download from " +
                            "https://www.tightvnc.com/download.php and run the installer."
                }, statusCode: 501);
            var ok = await vnc.StartServiceAsync();
            return Results.Ok(new { serviceRunning = ok, error = ok ? null : vnc.LastError });
        });

        group.MapPost("/vnc-session/stop-service", async (Phd2VncSessionService vnc) => {
            if (!vnc.IsSupportedOs || !vnc.TightVncInstalled)
                return Results.Json(new { error = "Not supported" }, statusCode: 501);
            var ok = await vnc.StopServiceAsync();
            return Results.Ok(new { serviceRunning = !ok, error = ok ? null : vnc.LastError });
        });
    }

    public record ConnectGuiderRequest(string? Host, int? Port);
    public record GuideRequest(double? SettlePixels, int? SettleTime, int? SettleTimeout, bool? Recalibrate);
    public record DitherRequest(double? Pixels, bool? RaOnly, double? SettlePixels, int? SettleTime, int? SettleTimeout);
    public record SelectStarRequest(double X, double Y);
    public record SyncProfileRequest(string? RigId);
    public record AlgoParamRequest(string Axis, string Name, double Value);
    public record AggressionDto(double Ra, double Dec);
    public record PredictiveDto(double WormPeriodSec, int WindowSamples, double Blend);
    public record ModeBody(string? Mode);
    public record FramesBody(int Frames);
}