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

public static class CameraEndpoints {
    public static void MapCameraEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/camera");

        group.MapPost("/capture", async (EquipmentManager equip, ImageRelayService relay,
            LiveStackingService liveStack, ImageWriterService imageWriter,
            CaptureProgressService captureProgress, ProfileService profileSvc,
            ILoggerFactory loggerFactory,
            CaptureRequest request) => {
            // Diag log -- helps trace 'preview frame landing on live
            // canvas' bug reports. Shows what JSON arrived from the
            // client side AS THE SERVER DESERIALIZED IT, including
            // whether feedLiveStack made it across the wire as the
            // expected false. If feedLiveStack is null here when the
            // client thinks it sent false, it means the JS the browser
            // is running predates the FeedLiveStack field -- typically
            // a stale cached app.js. Force-refresh fixes it.
            var capLogger = loggerFactory.CreateLogger("Polaris.Capture");
            capLogger.LogInformation(
                "POST /capture: exposure={Exp:F2}s gain={Gain} binning={Bin} kind={Kind} feedLiveStack={Feed} saveToDisk={Save} (liveStackRunning={Running})",
                request.Exposure, request.Gain, request.Binning,
                request.Kind ?? "(null=live)", request.FeedLiveStack,
                request.SaveToDisk, liveStack.IsRunning);
            // Aux-camera capture (FOCUS-manual on the auxiliary camera). A
            // self-contained path: capture through the dedicated aux gate, relay
            // for the FOCUS canvas, return HFR/star stats. Never feeds the live
            // stack or saves here (the aux save loop owns archiving).
            if (string.Equals(request.CameraSource, "aux", StringComparison.OrdinalIgnoreCase)) {
                if (equip.AuxCamera == null || !equip.AuxCamera.IsConnected)
                    return Results.BadRequest(new { error = "No aux camera connected" });
                try {
                    if (request.Binning > 0)
                        await equip.AuxCamera.SetBinningAsync(request.Binning, request.Binning);
                    var auxImg = await AuxCameraCaptureGate.RunAsync(async () => {
                        using (captureProgress.Begin("aux", request.Exposure))
                            return await equip.AuxCamera.CaptureAsync(request.Exposure,
                                new NINA.Image.Interfaces.CaptureOptions(
                                    Gain: request.Gain > 0 ? request.Gain : null, ImageType: "SNAP"));
                    }, acquireTimeout: TimeSpan.FromSeconds(Math.Max(request.Exposure, 1) + 60));
                    // Route to the PREVIEW canvas when the caller asks for it
                    // (kind=preview), else keep the legacy FOCUS-canvas target.
                    await relay.RelayImageAsync(auxImg!,
                        string.IsNullOrEmpty(request.Kind) ? FrameKind.Focus : ParseFrameKind(request.Kind));
                    var st = ComputeFocusStats(auxImg!);
                    // PREVIEW opt-in disk save. Aux frames go to their own aux/
                    // tree carrying the aux optics' focal length, matching the
                    // AuxCaptureService archiving layout.
                    bool auxSaved = false;
                    if (request.SaveToDisk && auxImg != null) {
                        var saved = imageWriter.SaveImage(auxImg, targetName: request.TargetName ?? "snap",
                            imageType: "AUX", gain: request.Gain,
                            focalLengthMmOverride: profileSvc.ActiveEquipmentProfile?.AuxFocalLengthMm);
                        auxSaved = saved != null;
                    }
                    return Results.Ok(new { status = "captured", stats = st, saved = auxSaved });
                } catch (Exception ex) {
                    return Results.Json(new { error = ex.Message }, statusCode: 500);
                }
            }

            // Guide-scope focusing: one-shot from the guide camera, relayed to
            // the FOCUS canvas. No live-stack / disk save. The guide camera is
            // normally owned by the guider loop, so this is meant for when the
            // user is NOT guiding (e.g. focusing the guide scope before a run).
            if (string.Equals(request.CameraSource, "guide", StringComparison.OrdinalIgnoreCase)) {
                if (equip.GuideCamera == null || !equip.GuideCamera.IsConnected)
                    return Results.BadRequest(new { error = "No guide camera connected" });
                try {
                    if (request.Binning > 0)
                        await equip.GuideCamera.SetBinningAsync(request.Binning, request.Binning);
                    using (captureProgress.Begin("guide", request.Exposure)) {
                        var gImg = await equip.GuideCamera.CaptureAsync(request.Exposure,
                            new NINA.Image.Interfaces.CaptureOptions(
                                Gain: request.Gain > 0 ? request.Gain : null, ImageType: "SNAP"));
                        await relay.RelayImageAsync(gImg!,
                            string.IsNullOrEmpty(request.Kind) ? FrameKind.Focus : ParseFrameKind(request.Kind));
                        var st = ComputeFocusStats(gImg!);
                        // PREVIEW opt-in disk save. Guide-scope snaps go to the
                        // snaps/ tree with a "guide" target marker (so they're
                        // distinct from the main camera's snaps) and carry the
                        // guide scope's focal length for correct FOV metadata.
                        bool guideSaved = false;
                        if (request.SaveToDisk && gImg != null) {
                            var baseName = string.IsNullOrWhiteSpace(request.TargetName) ? "snap" : request.TargetName;
                            var saved = imageWriter.SaveImage(gImg, targetName: baseName + " guide",
                                imageType: "SNAP", gain: request.Gain,
                                focalLengthMmOverride: profileSvc.ActiveEquipmentProfile?.GuiderFocalLengthMm);
                            guideSaved = saved != null;
                        }
                        return Results.Ok(new { status = "captured", stats = st, saved = guideSaved });
                    }
                } catch (Exception ex) {
                    return Results.Json(new { error = ex.Message }, statusCode: 500);
                }
            }

            if (equip.Camera == null)
                return Results.BadRequest(new { error = "No camera selected" });

            try {
                if (request.Binning > 0)
                    await equip.Camera.SetBinningAsync(request.Binning, request.Binning);

                // Optional pre-capture filter swap. Honour only when the
                // request carried a non-empty string AND the wheel is
                // actually connected, otherwise silently keep whatever
                // filter is already in place.
                // Optional pre-capture filter swap. FilterWheel is null
                // when not selected/connected, same convention used
                // throughout EquipmentManager. We only swap on a non-empty
                // string in the request, so passing null/"" keeps the
                // wheel where it is.
                if (!string.IsNullOrWhiteSpace(request.Filter)
                    && equip.FilterWheel != null) {
                    try {
                        await equip.FilterWheel.SetFilterByNameAsync(request.Filter);
                    } catch {
                        // Don't fail the whole capture on a filter swap
                        // error, the user sees the wrong filter in
                        // stats and can abort if it matters.
                    }
                }

                // Track the in-flight exposure so the capture button shows a
                // server-driven "Xs of Ys" countdown that survives a reconnect.
                // A null/"live" kind is the LIVE continuous-capture loop; any
                // other kind (preview/focus snap) maps to the PREVIEW shutter.
                var captureSource = string.IsNullOrEmpty(request.Kind)
                    || request.Kind.Equals("live", StringComparison.OrdinalIgnoreCase)
                    ? "live" : "snap";
                NINA.Image.Interfaces.IImageData imageData;
                // Serialize against every other main-camera capture (LIVE loop,
                // FOCUS-manual loop, a second browser tab, autofocus, sequence,
                // video, …). Concurrent native CaptureAsync on one camera handle
                // crashes the driver and takes the server down.
                //
                // captureProgress.Begin() drives the shutter countdown, so it
                // MUST start only AFTER we hold the gate — otherwise, while
                // queued behind another capture, the shutter would tick down to
                // 0 and sit there (the bug: "preview shutter stuck at 0"). The
                // acquire timeout (running exposure + 60s slack) frees the UI if
                // the holder wedges in the driver instead of hanging forever.
                imageData = await CameraCaptureGate.RunAsync(async () => {
                    using (captureProgress.Begin(captureSource, request.Exposure))
                        return await equip.Camera.CaptureAsync(request.Exposure);
                }, acquireTimeout: TimeSpan.FromSeconds(Math.Max(request.Exposure, 1) + 60));

                // PREVIEW tab: opt-in disk save under {rig}/snaps/.
                // ImageWriterService is a no-op when ImageOutputDir is
                // empty so we don't need to gate on profile state here.
                if (request.SaveToDisk && imageData != null) {
                    if (!string.IsNullOrEmpty(request.Filter))
                        imageData.MetaData.Exposure.Filter = request.Filter;
                    imageWriter.SaveImage(imageData,
                        targetName: request.TargetName ?? "snap",
                        imageType: "SNAP",
                        gain: request.Gain);
                }

                // FeedLiveStack=false from the caller forces relay-only
                // even if the live stack is currently running — that's
                // how PREVIEW / FOCUS-Manual / Flat Wizard say "this
                // is a one-off test shot, don't pollute the stack +
                // don't trigger the auto-recenter reference solve".
                // Null = legacy behaviour (feed if running).
                var feedStack = request.FeedLiveStack ?? true;
                capLogger.LogInformation(
                    "Capture path decision: feedStack={Feed} && liveStackRunning={Running} -> {Path}",
                    feedStack, liveStack.IsRunning,
                    (feedStack && liveStack.IsRunning)
                        ? "AddFrameAsync (LIVE stacker)"
                        : $"Relay(kind={ParseFrameKind(request.Kind)})");
                if (feedStack && liveStack.IsRunning) {
                    // SNR: keep the ETA's frame-to-time conversion
                    // honest by feeding the actual exposure (which
                    // can vary mid-session, e.g. user switches from
                    // 60 s to 120 s subs without resetting the stack).
                    if (request.Exposure > 0) liveStack.AverageExposureSec = request.Exposure;
                    await liveStack.AddFrameAsync(imageData!);
                } else {
                    // Route the broadcast to the correct panel via the
                    // FrameKind tag in the stream header. PREVIEW snaps
                    // land only on previewCanvas, FOCUS-Manual on
                    // focusCanvas, etc. Without this every snap
                    // overwrites every visible canvas on the page.
                    var kind = ParseFrameKind(request.Kind);
                    await relay.RelayImageAsync(imageData!, kind);
                }

                var stats = imageData!.Statistics;
                // ImageStatistics.Create only fills mean/median/MAD/etc;
                // StarCount + HFR stay zero unless someone explicitly
                // runs StarDetector. The live-stack path does it (via
                // LiveStackingService), but a PREVIEW snap that bypasses
                // the stacker would surface HFR=0 / Stars=0 in the UI
                // even on a frame full of obvious stars. Run the
                // detector inline so the snap response always carries
                // the real numbers.
                if (stats.StarCount == 0 && stats.HFR == 0) {
                    try {
                        // FIELD2-1: match the AutoFocusService detector
                        // tune so the manual focus snap and live
                        // tracking case agree on whether a heavily
                        // defocused field has stars. The same operator
                        // who's manually focusing will iterate IN/OUT
                        // past best focus; if the detector rejects
                        // donuts the HFR readout drops to 0 mid-sweep
                        // and they can't tell which side is closer.
                        var detector = new NINA.Image.ImageAnalysis.StarDetector {
                            MaxStarSize = 2000, MaxHfr = 100
                        };
                        var detected = detector.Detect(
                            imageData.Data,
                            imageData.Properties.Width,
                            imageData.Properties.Height);
                        if (stats is NINA.Image.ImageData.ImageStatistics mutable) {
                            mutable.StarCount = detected.Count;
                            if (detected.Count > 0) {
                                var sorted = detected.Select(s => s.HFR)
                                    .OrderBy(h => h).ToList();
                                mutable.HFR = sorted[sorted.Count / 2];
                            }
                        }
                    } catch {
                        /* defensive: leave 0/0 if the detector throws */
                    }
                }
                // MFOC-1: Laplacian variance sharpness metric. Cheap
                // 3x3 convolution over a 256 px ROI in the centre of
                // the frame (FrameQualityAnalyzer caps the ROI to keep
                // this < 5 ms even on Pi 4). Consumed by the FOCUS
                // tab's Manual subtab loop, where it's a secondary
                // focus indicator for sparse / no-star scenes (lunar,
                // Bahtinov-on-a-bright-star) that defeat HFR.
                double laplacianVar = 0;
                try {
                    laplacianVar = NINA.Polaris.Services.Planetary
                        .FrameQualityAnalyzer.LaplacianVariance(
                            imageData.Data, imageData.Properties.Width,
                            imageData.Properties.Height, roiSize: 256);
                } catch { /* defensive: never fail the capture for a
                             secondary stats field */ }
                return Results.Ok(new {
                    status = "complete",
                    width = imageData.Properties.Width,
                    height = imageData.Properties.Height,
                    saved = request.SaveToDisk,
                    stats = new {
                        mean = stats.Mean,
                        median = stats.Median,
                        stdev = stats.StDev,
                        starCount = stats.StarCount,
                        hfr = stats.HFR,
                        // Background SNR populated by ImageStatistics.Create
                        // in the same pass that fills mean/median/MAD.
                        // Surfaces in PREVIEW + LIVE + AUTORUN displays.
                        snr = stats.SNR,
                        laplacianVar = laplacianVar,
                        min = stats.Min,
                        max = stats.Max
                    }
                });
            } catch (OperationCanceledException) {
                return Results.Ok(new { status = "cancelled" });
            } catch (Exception ex) {
                return Results.Problem(ex.Message);
            }
        });

        // ----- Native-SDK camera controls (dynamic config panel) -----
        // Lists the driver/SDK controls the selected camera exposes (gain,
        // offset, cooler, gamma, WB, USB/FPS limit, flip, …) with live values +
        // ranges. `which` = main (default) | guide. Empty for INDI/Alpaca/ASCOM
        // (they use their own property trees).
        group.MapGet("/controls", (EquipmentManager equip, string? which) => {
            var cam = (which == "guide") ? equip.GuideCamera : equip.Camera;
            if (cam == null || !cam.IsConnected)
                return Results.Ok(new {
                    supported = false, which = which ?? "main",
                    controls = System.Array.Empty<object>()
                });
            var controls = cam.GetControls();
            return Results.Ok(new {
                supported = controls.Count > 0,
                which = which ?? "main",
                camera = cam.DeviceName,
                controls,
            });
        });

        group.MapPost("/controls/{id}", (string id, SetControlRequest body, EquipmentManager equip,
                                         NativeCameraControlStore controlStore) => {
            var cam = (body?.Which == "guide") ? equip.GuideCamera : equip.Camera;
            if (cam == null || !cam.IsConnected)
                return Results.BadRequest(new { error = "Camera not connected" });
            var auto = body?.Auto ?? false;
            var ok = cam.SetControl(id, body?.Value ?? 0, auto);
            if (!ok) return Results.BadRequest(new { error = $"Control '{id}' is not writable or unsupported" });
            // Return the fresh control so the UI reflects clamping/rounding.
            var updated = cam.GetControls().FirstOrDefault(c => c.Id == id);
            // Persist per physical camera so the value survives reconnect/restart.
            // Store the SDK-clamped value when we have it, else the requested one.
            controlStore.Set(cam.DeviceName, id, updated?.Value ?? body?.Value ?? 0, updated?.Auto ?? auto);
            return Results.Ok(new { status = "ok", control = updated });
        });

        group.MapPost("/abort", async (EquipmentManager equip) => {
            if (equip.Camera == null)
                return Results.BadRequest(new { error = "No camera selected" });

            await equip.Camera.AbortExposureAsync();
            return Results.Ok(new { status = "aborted" });
        });

        group.MapGet("/status", (EquipmentManager equip) => {
            if (equip.Camera == null)
                return Results.Ok(new {
                    connected = false,
                    state = "disconnected",
                    temperature = (double?)null,
                    coolerOn = false,
                    binX = 0, binY = 0,
                    driver = (string?)null,
                    deviceName = (string?)null
                });

            return Results.Ok(new {
                connected = equip.Camera.IsConnected,
                state = equip.Camera.State.ToString(),
                temperature = NanToNull(equip.Camera.Temperature),
                coolerOn = equip.Camera.CoolerOn,
                binX = equip.Camera.BinX,
                binY = equip.Camera.BinY,
                maxX = equip.Camera.MaxX,
                maxY = equip.Camera.MaxY,
                pixelSizeX = NanToNull(equip.Camera.PixelSizeX),
                pixelSizeY = NanToNull(equip.Camera.PixelSizeY),
                bitDepth = equip.Camera.BitDepth,
                whiteBalanceR = equip.Camera.WhiteBalanceR,
                whiteBalanceB = equip.Camera.WhiteBalanceB,
                whiteBalanceMin = equip.Camera.WhiteBalanceMin,
                whiteBalanceMax = equip.Camera.WhiteBalanceMax,
                // Gain (analogue amplification, the astro-camera analogue of a
                // DSLR's ISO) + its driver-reported range, and the ISO list for
                // DSLRs. The UI shows an ISO dropdown when isoOptions is
                // non-empty, otherwise the numeric Gain control.
                gain = equip.Camera.Gain,
                gainMin = equip.Camera.GainMin,
                gainMax = equip.Camera.GainMax,
                selectedIso = equip.Camera.SelectedIso,
                isoOptions = equip.Camera.IsoOptions,
                // Report which driver + device is currently bound so the
                // frontend can reconcile its dropdown state on page reload
                // (the cameraDriver Alpine state defaults from the saved
                // rig profile, but the actual EquipmentManager.Camera
                // might already be something else from a manual switch /
                // auto-connect that didn't persist the rig).
                driver = equip.CameraDriver,
                deviceName = equip.Camera.DeviceName,
                capabilities = new {
                    cooler = equip.Camera.Capabilities.SupportsCooler,
                    binning = equip.Camera.Capabilities.SupportsBinning,
                    roi = equip.Camera.Capabilities.SupportsRoi,
                    iso = equip.Camera.Capabilities.SupportsIso,
                    bulb = equip.Camera.Capabilities.SupportsBulb,
                    videoStream = equip.Camera.Capabilities.SupportsVideoStream,
                    whiteBalance = equip.Camera.Capabilities.SupportsWhiteBalance,
                    // Driver-reported exposure bounds, in seconds. Null when the
                    // backend doesn't report a range and the UI should fall back
                    // to its own defaults.
                    minExposureSec = equip.Camera.MinExposureSeconds,
                    maxExposureSec = equip.Camera.MaxExposureSeconds
                }
            });
        });

        // Live R/B white-balance writes for OSC color cameras (ZWO/QHY
        // expose WB_R + WB_B under CCD_CONTROLS). 501 surfaces clearly
        // when the active camera doesn't expose WB, so the UI can hide
        // the slider when SupportsWhiteBalance is false instead of
        // showing it and silently failing on writes.
        group.MapPost("/white-balance", async (EquipmentManager equip, WhiteBalanceRequest req) => {
            if (equip.Camera == null)
                return Results.BadRequest(new { error = "No camera selected" });
            if (!equip.Camera.Capabilities.SupportsWhiteBalance)
                return Results.Json(new { error = "Camera does not support white balance" },
                    statusCode: 501);
            await equip.Camera.SetWhiteBalanceAsync(req.Red, req.Blue);
            return Results.Ok(new { red = req.Red, blue = req.Blue });
        });

        // DSLR ISO selection (indi_gphoto CCD_ISO). 501 when the active camera
        // doesn't expose ISO so the UI keeps the numeric Gain control instead.
        group.MapPost("/iso", async (EquipmentManager equip, IsoRequest req) => {
            if (equip.Camera == null)
                return Results.BadRequest(new { error = "No camera selected" });
            if (!equip.Camera.Capabilities.SupportsIso)
                return Results.Json(new { error = "Camera does not support ISO" },
                    statusCode: 501);
            await equip.Camera.SetIsoAsync(req.Iso);
            return Results.Ok(new { iso = req.Iso });
        });

        // VIDEO tab FOV / ROI selection. Planetary capture needs the
        // sensor cropped to a tight box around the planet (Jupiter is
        // ~50 arcsec at typical focal lengths so a 640 x 480 box at
        // sensor center is plenty), so the SER file stays small + fps
        // climbs. width=0 / height=0 clears the ROI and returns to
        // the full sensor.
        group.MapPost("/subframe", async (EquipmentManager equip, SubframeRequest req) => {
            if (equip.Camera == null)
                return Results.BadRequest(new { error = "No camera selected" });
            if (!equip.Camera.Capabilities.SupportsRoi)
                return Results.Json(new { error = "Camera does not support ROI / subframe" },
                    statusCode: 501);
            await equip.Camera.SetSubframeAsync(req.X, req.Y, req.Width, req.Height);
            return Results.Ok(new {
                x = req.X, y = req.Y,
                width = req.Width, height = req.Height,
                full = req.Width <= 0 || req.Height <= 0
            });
        });

        group.MapPost("/cooler", (EquipmentManager equip, ProfileService profiles,
                                  CoolingRampService ramp, CoolerRequest request) => {
            if (equip.Camera == null)
                return Results.BadRequest(new { error = "No camera selected" });

            // Rate is per-rig; a request may override it. 0 = no ramp (write once).
            var rate = request.RampDegPerMinute
                       ?? profiles.ActiveEquipmentProfile?.CoolerRampDegPerMinute ?? 2.0;

            // COOLRAMP: both directions ramp, and both go through the one service.
            // ON used to write the setpoint raw (TEC straight to 100%, ~3.7°C/min
            // measured in the field), and OFF just cut the cooler dead — leaving a
            // 0°C sensor to race back to ambient, which is the textbook way to
            // condense water on the window. OFF now walks the setpoint up first and
            // powers the TEC down only on arrival.
            //
            // The old "don't write the target while disabling" hazard still holds:
            // on SVBony, writing SVB_TARGET_TEMPERATURE flips SVB_COOLER_ENABLE back
            // on, so a naive disable would bounce the cooler straight back. The ramp
            // respects that by construction — during a warm-up the cooler is MEANT to
            // stay on (it's what paces the rise), and SetCoolerAsync(false) runs once
            // at the end, after the final setpoint write. Never before.
            //
            // Returns as soon as the ramp STARTS: a 27→0°C ramp at 2°C/min is ~14
            // minutes, which no HTTP request should hold open. Progress rides the WS
            // `cooling` block.
            if (request.Enabled) {
                var target = request.TargetTemperature
                             ?? profiles.ActiveEquipmentProfile?.CoolerTargetTemperature ?? -10;
                ramp.Start(equip.Camera, target, rate,
                           coolerOnFirst: true, coolerOffWhenDone: false, source: "UI cooldown");
                return Results.Ok(new { coolerOn = true, target, ramping = rate > 0, rate });
            } else {
                // Warm-up destination. Ambient would be ideal but we can't count on a
                // sensor for it; 20°C matches the WarmCameraInstruction default.
                var warmTo = request.WarmTargetC ?? 20;
                ramp.Start(equip.Camera, warmTo, rate,
                           coolerOnFirst: false, coolerOffWhenDone: true, source: "UI warm-up");
                return Results.Ok(new { coolerOn = false, target = warmTo, ramping = rate > 0, rate });
            }
        });

        group.MapPost("/select/{deviceName}", (EquipmentManager equip, string deviceName, string? driver) => {
            // Default driver is "indi" so existing clients (which only
            // pass the device name) keep working untouched. DSLR / Alpaca
            // callers add ?driver=canon-edsdk etc.
            try {
                equip.SelectCamera(driver ?? "indi", deviceName);
                return Results.Ok(new {
                    selected = deviceName,
                    driver = driver ?? "indi"
                });
            } catch (NotSupportedException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Available camera driver kinds for the current host. Always
        // includes 'indi'; vendor SDK entries are listed only on the
        // platforms that can support them, with an `available` flag
        // for whether the native dependency is actually present.
        group.MapGet("/drivers", (EquipmentManager equip)
            => Results.Ok(equip.GetAvailableCameraDrivers()));

        // Per-driver camera discovery. For INDI: the device-name
        // list from the active connection. For vendor SDKs: the
        // SDK-specific enumeration call. Empty list when no cameras
        // are connected (or when the driver isn't supported on this
        // OS), never throws.
        group.MapGet("/discover", (EquipmentManager equip, string? driver)
            => Results.Ok(equip.GetDiscoveredCamerasFor(driver ?? "indi")));

        group.MapPost("/connect", async (EquipmentManager equip, ProfileService profileSvc,
                                         NativeCameraControlStore controlStore, ILoggerFactory loggerFactory) => {
            if (equip.Camera == null)
                return Results.BadRequest(new { error = "No camera selected. Use POST /api/camera/select/{name} first" });

            await DeviceConnectGuard.BoundedAsync("connect", equip.Camera.DeviceName,
                ct => equip.Camera.ConnectAsync(ct));
            // Re-apply the native-SDK controls the user tuned last time (gain,
            // offset, cooler, gamma, USB speed, …). The SDK forgets everything
            // on close, so this is what makes the config panel "persist".
            try {
                var n = controlStore.ApplySaved(equip.Camera);
                if (n > 0)
                    loggerFactory.CreateLogger("Polaris.Camera")
                        .LogInformation("Re-applied {N} saved native control(s) to {Dev}",
                            n, equip.Camera.DeviceName);
            } catch (Exception ex) {
                loggerFactory.CreateLogger("Polaris.Camera")
                    .LogDebug(ex, "Re-apply of saved native controls skipped (non-fatal)");
            }
            // Per-rig pixel-size fallback. indi_gphoto (DSLR) leaves CCD_INFO
            // pixel size at 0; push the rig's configured value into the driver
            // so PixelSizeX/Y — and therefore FOV, the plate-solve scale hint,
            // and the post-solve focal-length auto-update — get a real number.
            // Only when the rig has a value AND the camera actually reports 0
            // (don't override a camera that knows its own pixel pitch).
            try {
                var rig = profileSvc.ActiveEquipmentProfile;
                if (rig != null && equip.Camera.MaxX <= 0
                    && rig.CameraMaxX > 0 && rig.CameraMaxY > 0 && rig.CameraPixelSizeUm > 0
                    && equip.Camera is NINA.INDI.Devices.IndiCamera indiCam) {
                    await indiCam.TrySetCcdInfoAsync(rig.CameraMaxX, rig.CameraMaxY,
                        rig.CameraPixelSizeUm, rig.CameraBitDepth);
                    loggerFactory.CreateLogger("Polaris.Camera")
                        .LogInformation("Pushed rig CCD_INFO into {Dev}: {X}x{Y} px, {P}µm, {B}-bit " +
                            "(camera reported 0 — DSLR bootstrap)", equip.Camera.DeviceName,
                            rig.CameraMaxX, rig.CameraMaxY, rig.CameraPixelSizeUm, rig.CameraBitDepth);
                }
            } catch (Exception ex) {
                loggerFactory.CreateLogger("Polaris.Camera")
                    .LogDebug(ex, "CCD_INFO push on connect skipped (non-fatal)");
            }
            // The camera's ROI (CCD_FRAME for INDI) is retained by the driver
            // across browser sessions and even reconnects — the INDI server on
            // the SBC keeps running. So a planetary ROI set in a prior VIDEO
            // session would otherwise leak into PREVIEW / LIVE / sequence
            // forever. Assert the full sensor on connect; VIDEO re-applies its
            // own ROI when that tab is opened. Best-effort: backends that don't
            // support ROI just no-op here.
            try {
                await equip.Camera.SetSubframeAsync(0, 0, 0, 0);
            } catch (Exception ex) {
                loggerFactory.CreateLogger("Polaris.Camera")
                    .LogDebug(ex, "Full-frame reset on connect skipped (non-fatal)");
            }
            return Results.Ok(new { status = "connected", device = equip.Camera.DeviceName });
        });

        group.MapPost("/disconnect", async (EquipmentManager equip) => {
            if (equip.Camera == null)
                return Results.BadRequest(new { error = "No camera selected" });

            return await DeviceConnectGuard.RunAsync(
                "disconnect", equip.Camera.DeviceName,
                ct => equip.Camera.DisconnectAsync(ct),
                () => Results.Ok(new { status = "disconnected" }));
        });

        // ----- Video stream (continuous frame feed) -----
        // Auto-picks native CCD_VIDEO_STREAM mode when the camera
        // supports it; falls back to a tight server-side capture loop
        // for any other backend. Frames bypass FITS save + stats and go
        // straight to the existing /ws/image-stream channel.

        group.MapPost("/stream/start", (EquipmentManager equip,
                                        CameraStreamService stream,
                                        StreamStartRequest? request) => {
            if (equip.Camera == null)
                return Results.BadRequest(new { error = "No camera connected" });
            try {
                stream.Start(new StreamConfig(
                    ExposureSeconds: request?.Exposure ?? 0.1,
                    Gain: request?.Gain,
                    BinX: request?.Binning ?? 1,
                    BinY: request?.Binning ?? 1,
                    ForceLoop: request?.ForceLoop ?? false));
                return Results.Ok(new {
                    running = true,
                    mode = stream.Mode,
                    supportsNative = equip.Camera.Capabilities.SupportsVideoStream
                });
            } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapPost("/stream/stop", async (CameraStreamService stream) => {
            await stream.StopAsync();
            return Results.Ok(new { running = false, frames = stream.FrameCount });
        });

        // PLAN8-2: drop the running maximum the focus bar is measured against.
        // The metric is only comparable within one target at one exposure and
        // gain, so after moving to another planet the old best is a yardstick
        // for nothing and would peg the bar low forever.
        group.MapPost("/stream/sharpness/reset", (CameraStreamService stream) => {
            stream.ResetSharpness();
            return Results.Ok(new { reset = true });
        });

        // Live exposure/gain tweak for a running stream so the VIDEO controls
        // stay usable while streaming (ASIAIR-style). Loop mode picks the new
        // values up on the next frame; native mode is restarted to apply them.
        group.MapPost("/stream/params", async (CameraStreamService stream, StreamParamsRequest req) => {
            await stream.UpdateLiveAsync(req.Exposure, req.Gain);
            return Results.Ok(new { running = stream.IsRunning, exposure = stream.ExposureSeconds, gain = stream.Gain });
        });

        group.MapGet("/stream/status", (CameraStreamService stream, EquipmentManager equip) => Results.Ok(new {
            running = stream.IsRunning,
            mode = stream.Mode,
            exposure = stream.ExposureSeconds,
            gain = stream.Gain,
            binX = stream.BinX,
            binY = stream.BinY,
            frames = stream.FrameCount,
            fps = stream.Fps,             // capture FPS (back-compat alias)
            captureFps = stream.Fps,      // frames produced by the camera
            transmitFps = stream.TransmitFps, // JPEG frames sent to clients
            startedAt = stream.IsRunning ? stream.StartedAt : (DateTime?)null,
            lastFrameAt = stream.IsRunning ? stream.LastFrameAt : (DateTime?)null,
            lastError = stream.LastError,
            supportsNative = equip.Camera?.Capabilities.SupportsVideoStream ?? false,
            // Diagnostics for slow-stream triage:
            //   captureMs  = per-frame CaptureAsync time (loop mode only)
            //   frameWidth/Height + rawMBPerFrame = on-wire payload size;
            //     RAW streaming sends W*H*2 bytes/frame before LZ4, so a
            //     full-frame OSC at BIN1 is bandwidth-bound. Shrink with
            //     ROI + binning.
            captureMs = Math.Round(stream.LastCaptureMs, 1),
            frameWidth = stream.LastFrameWidth,
            frameHeight = stream.LastFrameHeight,
            rawMBPerFrame = Math.Round(stream.LastFrameRawBytes / (1024.0 * 1024.0), 2)
        }));
    }

    static double? NanToNull(double v) => double.IsNaN(v) ? null : v;

    /// <summary>
    /// Capture-request body. <see cref="SaveToDisk"/> + <see cref="TargetName"/>
    /// are the PREVIEW-tab additions: when SaveToDisk is true the
    /// handler also runs ImageWriterService.SaveImage with imageType
    /// = "SNAP" (which BuildSubDir routes into {rig}/snaps/{filter}_{date}/).
    /// </summary>
    public record CaptureRequest(
        double Exposure = 1.0,
        int Gain = 100,
        int Binning = 1,
        string? Filter = null,
        bool SaveToDisk = false,
        string? TargetName = null,
        // null = legacy "feed if running" (LIVE tab default).
        // true  = always feed (sequence engine, explicit live capture).
        // false = never feed (PREVIEW snap, FOCUS Manual test shot,
        //         Bahtinov / flat-wizard / anything that's not a
        //         science frame). Without this, with the always-on
        //         stacking we now default to, a tap on PREVIEW would
        //         silently push a junk frame into the live stack and
        //         fire the auto-recenter plate solve.
        bool? FeedLiveStack = null,
        // Which panel originated the request. Encoded in the WS frame
        // header as FrameKind so the browser routes the bitmap to that
        // panel's canvas only. Values: "live" (default), "preview",
        // "focus", "video", "slew-preview". Anything else → "live".
        string? Kind = null,
        // Which camera to capture from: "main" (default) or "aux". The aux
        // path is used by FOCUS-manual to focus the auxiliary camera; it
        // captures through the separate aux gate and never feeds the stack
        // or saves to disk here.
        string? CameraSource = null);

    /// <summary>Run the same star-detector + laplacian-variance pass the main
    /// capture path uses, for the aux-camera FOCUS snap. Returns the stats shape
    /// the manual-focus loop consumes (hfr/starCount/laplacianVar/width/height).</summary>
    private static object ComputeFocusStats(NINA.Image.Interfaces.IImageData img) {
        int starCount = 0; double hfr = 0, laplacianVar = 0;
        try {
            var detector = new NINA.Image.ImageAnalysis.StarDetector { MaxStarSize = 2000, MaxHfr = 100 };
            var detected = detector.Detect(img.Data, img.Properties.Width, img.Properties.Height);
            starCount = detected.Count;
            if (detected.Count > 0) {
                var sorted = detected.Select(s => s.HFR).OrderBy(h => h).ToList();
                hfr = sorted[sorted.Count / 2];
            }
        } catch { }
        try {
            laplacianVar = NINA.Polaris.Services.Planetary.FrameQualityAnalyzer.LaplacianVariance(
                img.Data, img.Properties.Width, img.Properties.Height, roiSize: 256);
        } catch { }
        return new {
            starCount, hfr, laplacianVar,
            width = img.Properties.Width, height = img.Properties.Height
        };
    }

    private static FrameKind ParseFrameKind(string? raw) => raw?.ToLowerInvariant() switch {
        "preview"      => FrameKind.Preview,
        "focus"        => FrameKind.Focus,
        "manual-focus" => FrameKind.Focus,
        "video"        => FrameKind.Video,
        "slew-preview" => FrameKind.SlewPreview,
        _              => FrameKind.Live
    };
    /// <summary>Body for POST /api/camera/cooler. <c>TargetTemperature</c> and
    /// <c>RampDegPerMinute</c> both fall back to the active rig when omitted, so
    /// existing clients that only send <c>Enabled</c> keep working and simply get
    /// the rig's ramp. <c>RampDegPerMinute</c>=0 forces the old write-once
    /// behaviour. <c>WarmTargetC</c> is where a cooler-OFF ramps up to before the
    /// TEC is powered down.</summary>
    public record CoolerRequest(bool Enabled, double? TargetTemperature = null,
                                double? RampDegPerMinute = null, double? WarmTargetC = null);

    /// <summary>White-balance body. Range is driver-specific, ZWO/QHY
    /// typically 0..100 with 50 = neutral; UI bounds the slider to that
    /// per default and lets the user push outside.</summary>
    public record WhiteBalanceRequest(double Red, double Blue);
    public record SubframeRequest(int X, int Y, int Width, int Height);
    public record IsoRequest(int Iso);

    /// <summary>Start-stream body. ForceLoop=true skips native streaming
    /// even when the camera supports it (debugging the fallback).</summary>
    public record StreamStartRequest(
        double? Exposure = null,
        int? Gain = null,
        int? Binning = null,
        bool? ForceLoop = null);

    public record StreamParamsRequest(double? Exposure = null, int? Gain = null);

    /// <summary>Body for POST /api/camera/controls/{id}. <c>Which</c> = main
    /// (default) | guide. <c>Auto</c> hands the value to the driver.</summary>
    public record SetControlRequest(double Value, bool Auto = false, string? Which = null);
}