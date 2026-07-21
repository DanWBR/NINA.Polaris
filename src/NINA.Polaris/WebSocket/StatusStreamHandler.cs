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

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NINA.Polaris.Services;

namespace NINA.Polaris.WebSocket;

public static class StatusStreamHandler {
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOpts = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // PERF #365: serialize the status payload at most once per tick and
    // share the resulting bytes across all connected clients, instead of
    // every client loop building + serializing the full ~50-100 KB payload
    // independently. The only per-client part of the old payload was the
    // debugLog cursor; it becomes a shared cursor here (the frontend dedups
    // log entries by id, so shared delivery is safe — a client that misses
    // a window backfills via GET /api/logs). Keyed by 1-second tick so two
    // clients within the same second reuse one serialization.
    private static readonly object _statusCacheLock = new();
    private static byte[]? _statusCacheBytes;
    private static long _statusCacheTick = -1;
    private static long _sharedDebugCursor;

    public static async Task Handle(HttpContext context) {
        if (!context.WebSockets.IsWebSocketRequest) {
            context.Response.StatusCode = 400;
            return;
        }

        var equip = context.RequestServices.GetRequiredService<EquipmentManager>();
        var cameraStream = context.RequestServices.GetRequiredService<CameraStreamService>();
        var videoRecording = context.RequestServices.GetRequiredService<NINA.Polaris.Services.Planetary.VideoRecordingService>();
        var videoStacker = context.RequestServices.GetRequiredService<NINA.Polaris.Services.Planetary.PlanetaryStackerService>();
        var keepCentered = context.RequestServices.GetRequiredService<NINA.Polaris.Services.Planetary.KeepCenteredService>();
        var slewPreview = context.RequestServices.GetRequiredService<SlewPreviewService>();
        var liveStackTriggers = context.RequestServices.GetRequiredService<LiveStackTriggersService>();
        var refocusSuggest = context.RequestServices.GetRequiredService<RefocusSuggestionService>();
        var flatWizard = context.RequestServices.GetRequiredService<FlatWizardService>();
        var liveStack = context.RequestServices.GetRequiredService<LiveStackingService>();
        var sequence = context.RequestServices.GetRequiredService<SequenceEngine>();
        var planRunner = context.RequestServices.GetRequiredService<NINA.Polaris.Services.Plan.PlanRunnerService>();
        var advEngine = context.RequestServices.GetRequiredService<NINA.Polaris.Services.Sequencer.AdvancedSequenceEngine>();
        var phd2 = context.RequestServices.GetRequiredService<PHD2Client>();
        var guiders = context.RequestServices.GetRequiredService<ActiveGuiderProvider>();
        var profileSync = context.RequestServices.GetRequiredService<PHD2ProfileSyncService>();
        var phd2Calibration = context.RequestServices.GetRequiredService<PHD2CalibrationOrchestrator>();
        var phd2Gui = context.RequestServices.GetRequiredService<Phd2GuiSessionService>();
        var phd2Vnc = context.RequestServices.GetRequiredService<Phd2VncSessionService>();
        var autoFocus = context.RequestServices.GetRequiredService<AutoFocusService>();
        var meridianFlip = context.RequestServices.GetRequiredService<MeridianFlipService>();
        var safetyGuard = context.RequestServices.GetRequiredService<MountSafetyGuardService>();
        var profile = context.RequestServices.GetRequiredService<ProfileService>();
        var hostMetrics = context.RequestServices.GetRequiredService<HostMetricsService>();
        var clockSync = context.RequestServices.GetRequiredService<ClockSyncService>();
        var usbWatcher = context.RequestServices.GetRequiredService<UsbDriveWatcherService>();
        var siril = context.RequestServices
            .GetRequiredService<NINA.Polaris.Services.External.SirilService>();
        var graxpert = context.RequestServices
            .GetRequiredService<NINA.Polaris.Services.External.GraXpertService>();
        var simulator = context.RequestServices
            .GetRequiredService<NINA.Polaris.Services.Simulator.SimulatorService>();
        var network = context.RequestServices.GetRequiredService<NetworkManagerService>();
        var storagePush = context.RequestServices.GetRequiredService<StoragePushService>();
        var benchmark = context.RequestServices.GetRequiredService<BenchmarkService>();
        var sensorAnalysis = context.RequestServices.GetRequiredService<SensorAnalysisService>();
        var notifications = context.RequestServices.GetRequiredService<NotificationService>();
        var polarAlign = context.RequestServices.GetRequiredService<PolarAlignmentService>();
        var plateSolveProgress = context.RequestServices
            .GetRequiredService<NINA.Polaris.Services.PlateSolving.PlateSolveProgressService>();
        var captureProgress = context.RequestServices.GetRequiredService<CaptureProgressService>();
        var deconProgress = context.RequestServices.GetRequiredService<DeconProgressService>();
        var coolingRamp = context.RequestServices.GetRequiredService<CoolingRampService>();
        var liveCapture = context.RequestServices.GetRequiredService<LiveCaptureService>();
        var auxCapture = context.RequestServices.GetRequiredService<AuxCaptureService>();
        var logService = context.RequestServices.GetRequiredService<NINA.Polaris.Services.Logging.LogService>();
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();

        using var ws = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext {
            KeepAliveInterval = PingInterval
        });

        using var cts = new CancellationTokenSource();

        try {
            await SendJsonAsync(ws, new { type = "connected", stream = "status" }, cts.Token);
        } catch {
            return;
        }

        var sendTask = Task.Run(async () => {
            while (!cts.Token.IsCancellationRequested && ws.State == WebSocketState.Open) {
                try {
                    // PERF #365: reuse this tick's already-serialized payload
                    // if another client (or this one) built it within the
                    // current 1-second window.
                    long tick = DateTime.UtcNow.Ticks / StatusInterval.Ticks;
                    byte[]? payload = null;
                    long localDebugCursor = 0;
                    lock (_statusCacheLock) {
                        if (_statusCacheTick == tick && _statusCacheBytes != null)
                            payload = _statusCacheBytes;
                        else
                            localDebugCursor = _sharedDebugCursor;
                    }
                    if (payload == null) {
                    var seqStatus = sequence.GetStatus();

                    // Compact summaries of PH2X-3/4/6 services, surface
                    // as sub-objects on the guider block so UI can read
                    // sync/calibrate/embed status without polling endpoints.
                    var profileSyncPayload = new {
                        phase = profileSync.CurrentStatus.Phase,
                        rigId = profileSync.CurrentStatus.RigId,
                        rigName = profileSync.CurrentStatus.RigName,
                        profileId = profileSync.CurrentStatus.ProfileId,
                        profileMissing = profileSync.CurrentStatus.ProfileMissing,
                        error = profileSync.CurrentStatus.Error,
                        at = profileSync.CurrentStatus.At
                    };
                    var calibrateJobPayload = phd2Calibration.CurrentJob == null ? null : new {
                        id = phd2Calibration.CurrentJob.Id,
                        phase = phd2Calibration.CurrentJob.State.ToString(),
                        stepMs = phd2Calibration.CurrentJob.CalibrationStepMs,
                        pixelScale = phd2Calibration.CurrentJob.PixelScale,
                        error = phd2Calibration.CurrentJob.Error,
                        warnings = phd2Calibration.CurrentJob.Warnings,
                        done = phd2Calibration.CurrentJob.State == CalibrationPhase.Ok
                            || phd2Calibration.CurrentJob.State == CalibrationPhase.Fail
                    };
                    var guiSessionPayload = new {
                        supportedOs = phd2Gui.IsSupportedOs,
                        supportedArch = phd2Gui.IsSupportedArch,
                        unsupportedReason = phd2Gui.UnsupportedReason,
                        xpraInstalled = phd2Gui.XpraInstalled,
                        xpraVersion = phd2Gui.XpraVersion,
                        running = phd2Gui.SessionRunning,
                        port = phd2Gui.BindPort,
                        lastError = phd2Gui.LastError
                    };
                    // PH2VNC-4: Windows-side embed status. Mirrors
                    // guiSession's shape so the UI tab can switch
                    // backends by OS without divergent state code.
                    var vncSessionPayload = new {
                        supportedOs = phd2Vnc.IsSupportedOs,
                        unsupportedReason = phd2Vnc.UnsupportedReason,
                        tightVncInstalled = phd2Vnc.TightVncInstalled,
                        tightVncVersion = phd2Vnc.TightVncVersion,
                        serviceInstalled = phd2Vnc.ServiceInstalled,
                        serviceRunning = phd2Vnc.ServiceRunning,
                        listening = phd2Vnc.Listening,
                        port = phd2Vnc.Port,
                        lastError = phd2Vnc.LastError
                    };

                    // Compact guider payload: last 60 samples for inline
                    // chart. Generic fields are sourced from the active
                    // guider (PHD2 or native) so the GUIDE tab works
                    // unchanged for both backends; the PHD2-only sub-
                    // objects (profileSync/guiSession/vncSession/
                    // calibrateJob) stay PHD2-sourced and the frontend
                    // ignores them when backend == "native".
                    var activeGuider = guiders.Active;
                    object? guiderPayload;
                    if (activeGuider.IsConnected) {
                        var steps = activeGuider.SnapshotSteps();
                        var tail = steps.Skip(Math.Max(0, steps.Count - 60));
                        guiderPayload = new {
                            backend = activeGuider.Backend,
                            connected = true,
                            appState = activeGuider.AppState,
                            guiding = activeGuider.IsGuiding,
                            calibrating = activeGuider.IsCalibrating,
                            paused = activeGuider.IsPaused,
                            looping = activeGuider.IsLooping,
                            settling = activeGuider.IsSettling,
                            dithering = activeGuider.IsDithering,
                            settleProgress = activeGuider.SettleProgress,
                            raAggression = activeGuider.RaAggression,
                            decAggression = activeGuider.DecAggression,
                            pixelScale = activeGuider.PixelScale,
                            rmsRA = activeGuider.RmsRA,
                            rmsDec = activeGuider.RmsDec,
                            rmsTotal = activeGuider.RmsTotal,
                            peakRA = activeGuider.PeakRA,
                            peakDec = activeGuider.PeakDec,
                            stepCount = steps.Count,
                            lastAlert = activeGuider.LastAlert,
                            lastAlertSeverity = activeGuider.LastAlertSeverity,
                            lastSettleStatus = activeGuider.LastSettleStatus,
                            calProgress = activeGuider.CalibrationProgress,
                            calDetails = activeGuider.CalibrationDetails,
                            exposureMs = activeGuider.ExposureMs,
                            recentSteps = tail.Select(s => new {
                                t = ((DateTimeOffset)s.Timestamp).ToUnixTimeMilliseconds(),
                                ra = s.RaArcsec,
                                dec = s.DecArcsec,
                                raPx = s.RaPixels,
                                decPx = s.DecPixels,
                                snr = s.SNR,
                                mass = s.Mass,
                                raDur = s.RaDuration,
                                decDur = s.DecDuration,
                                raDir = s.RaDirection,
                                decDir = s.DecDirection,
                                // Predicted next-frame error (arcsec) from the native
                                // predictive algorithm; 0 for PHD2 / reactive algos.
                                predRa = s.PredRaArcsec,
                                predDec = s.PredDecArcsec,
                                // True while this step was recorded during a dither/settle,
                                // so the guide charts can hatch the dither region.
                                dither = s.Dither
                            }),
                            // Live guide-frame view (native backend only; null for PHD2).
                            view = activeGuider.ViewState,
                            // Native dark library / bad-pixel-map status (null for PHD2).
                            darkCalibration = activeGuider.DarkCalibration,
                            // Native guide-camera connection state (its own connect switch).
                            guideCameraConnected = equip.GuideCamera?.IsConnected ?? false,
                            guideCameraName = equip.GuideCamera?.DeviceName,
                            profileSync = profileSyncPayload,
                            calibrateJob = calibrateJobPayload,
                            guiSession = guiSessionPayload,
                            vncSession = vncSessionPayload
                        };
                    } else {
                        guiderPayload = new {
                            backend = activeGuider.Backend,
                            connected = false, appState = "Stopped",
                            exposureMs = activeGuider.ExposureMs,
                            guideCameraConnected = equip.GuideCamera?.IsConnected ?? false,
                            guideCameraName = equip.GuideCamera?.DeviceName,
                            profileSync = profileSyncPayload,
                            calibrateJob = calibrateJobPayload,
                            guiSession = guiSessionPayload,
                            vncSession = vncSessionPayload
                        };
                    }

                    // Meridian flip live status (LST + time-to-meridian for the current mount RA)
                    double? lstHours = null, hourAngleHours = null, timeToMeridianHours = null;
                    double? timeToFlipHours = null, timeToSetHours = null;
                    if (equip.Telescope != null && equip.Telescope.IsConnected) {
                        var raHours = equip.Telescope.RightAscension;
                        if (!double.IsNaN(raHours)) {
                            lstHours = MeridianFlipService.ComputeLstHours(DateTime.UtcNow, profile.Active.Longitude);
                            var ha = lstHours.Value - raHours;
                            while (ha > 12) ha -= 24;
                            while (ha < -12) ha += 24;
                            hourAngleHours = ha;
                            timeToMeridianHours = MeridianFlipService.HoursUntilMeridian(
                                raHours, DateTime.UtcNow, profile.Active.Longitude);
                            // Time until the flip point (HA = MinutesAfterMeridian);
                            // drives the LIVE-tab countdown.
                            timeToFlipHours = MeridianFlipService.HoursUntilFlip(
                                raHours, DateTime.UtcNow, profile.Active.Longitude,
                                meridianFlip.Settings.MinutesAfterMeridian);
                            // Time until the target sets below the horizon. For a
                            // target already past the meridian (descending west),
                            // this is the useful countdown — "time until meridian"
                            // is meaningless there (it already crossed). Needs Dec.
                            var decDeg = equip.Telescope.Declination;
                            if (!double.IsNaN(decDeg)) {
                                timeToSetHours = AltitudeService.HoursUntilSet(
                                    raHours, decDeg, DateTime.UtcNow,
                                    profile.Active.Latitude, profile.Active.Longitude);
                            }
                        }
                    }

                    var meridianPayload = new {
                        state = meridianFlip.State.ToString().ToLowerInvariant(),
                        settings = meridianFlip.Settings,
                        flipsCompleted = meridianFlip.FlipsCompleted,
                        lastFlipAt = meridianFlip.LastFlipAt,
                        lastFlipError = meridianFlip.LastFlipError,
                        lstHours,
                        hourAngleHours,
                        timeToMeridianHours,
                        timeToMeridianMinutes = timeToMeridianHours * 60,
                        timeToFlipHours,
                        timeToFlipMinutes = timeToFlipHours * 60,
                        timeToSetHours,
                        timeToSetMinutes = timeToSetHours * 60,
                        safetyTripped = safetyGuard.Tripped,
                        safetyReason = safetyGuard.TripReason,
                        safetyTrippedAt = safetyGuard.TrippedAt
                    };

                    var autoFocusPayload = new {
                        state = autoFocus.State.ToString().ToLowerInvariant(),
                        currentSampleIndex = autoFocus.Progress.CurrentSampleIndex,
                        steps = autoFocus.Progress.Steps,
                        lastHfr = autoFocus.Progress.LastHfr,
                        lastStarCount = autoFocus.Progress.LastStarCount,
                        points = autoFocus.Progress.Points,
                        bestPosition = autoFocus.LastResult?.BestPosition,
                        bestHfr = autoFocus.LastResult?.BestPredictedHfr,
                        success = autoFocus.LastResult?.Success,
                        mode = autoFocus.Progress.Mode,
                        // AFPORT (additive): live fit parameters + method +
                        // attempt so the chart draws the hyperbola/trendlines
                        // while the sweep grows.
                        method = autoFocus.Progress.Method,
                        attempt = autoFocus.Progress.Attempt,
                        fits = autoFocus.Progress.Fits
                    };

                    // Compact summaries for the activity bar. Full job
                    // detail (lights paths, results, etc) lives on the
                    // per-tool endpoints, only the surface needed for
                    // chips makes it into the broadcast.
                    var sirilJobsPayload = siril.ActiveJobs.Select(j => new {
                        j.JobId, j.ScriptName, j.TargetName, j.Stage, j.PercentDone
                    }).ToList();
                    var graXpertJobsPayload = graxpert.ActiveJobs.Select(j => new {
                        j.JobId,
                        operation = j.Operation.ToString(),
                        j.Done, j.Total, j.Failed
                    }).ToList();

                    var status = new {
                        type = "status",
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        equipment = equip.GetEquipmentStatus(),
                        // Auxiliary camera capture loop status (running + frames
                        // saved this session + a no-output-folder warning).
                        auxCapture = new {
                            running = auxCapture.IsRunning,
                            frameCount = auxCapture.FrameCount,
                            lastError = auxCapture.LastError,
                            noOutputDir = auxCapture.NoOutputDir
                        },
                        // Stack status + triggers (LSTR-4). Triggers sub-object
                        // carries last-action timestamps + reference RA/Dec +
                        // executing flag so the UI banner + status lines can
                        // render without a separate poll.
                        liveStack = new {
                            isRunning = liveStack.GetStatus().IsRunning,
                            frameCount = liveStack.GetStatus().FrameCount,
                            width = liveStack.GetStatus().Width,
                            height = liveStack.GetStatus().Height,
                            referenceStarCount = liveStack.GetStatus().ReferenceStarCount,
                            lastFrameHfr = liveStack.LastFrameMedianHfr,
                            lastFrameStarCount = liveStack.LastFrameStarCount,
                            lastFrameMean = liveStack.LastFrameMean,
                            // CLST-1/CLST-4: "full" (server-side accumulator) or
                            // "metricsonly" (client owns the accumulator via WASM).
                            // The client only routes raw frames through its WASM
                            // stacker when this is "metricsonly", otherwise the
                            // raw frames the server relays ARE the accumulated
                            // stack and re-stacking would compound.
                            mode = liveStack.GetStatus().Mode,
                            // Per-frame-to-disk toggle + count of frames
                            // actually written this session. Drives the
                            // LIVE tab checkbox state + the "(N saved)"
                            // counter rendered next to it.
                            saveFramesToDisk = liveStack.SaveFramesToDisk,
                            framesSavedToDisk = liveStack.FramesSavedToDisk,
                            // True when "save frames" is on but no output folder
                            // is configured, so frames are silently dropped. The
                            // LIVE tab shows a warning to set a folder.
                            saveFramesNoDir = liveStack.SaveFramesNoOutputDir,
                            // Colour (OSC debayer → RGB) stacking toggle +
                            // whether it's actually engaged this session (ON
                            // + the reference frame was Bayered). Drives the
                            // LIVE tab colour checkbox + a "(colour)" hint.
                            colorStacking = liveStack.ColorStacking,
                            colorActive = liveStack.ColorActive,
                            // Part B: how many meridian flips the stacker
                            // re-oriented and kept stacking through.
                            meridianFlipsHandled = liveStack.MeridianFlipsHandled,
                            // Continuous-stack + duration cap. UI uses
                            // these to render the elapsed counter, the
                            // "stack complete" badge once the cap fires,
                            // and the max-duration input value.
                            maxDurationSeconds = liveStack.MaxDurationSeconds,
                            startedAt = liveStack.StartedAt,
                            elapsedSeconds = liveStack.ElapsedSeconds,
                            durationCapReached = liveStack.DurationCapReached,
                            // SNR-2: signal-to-noise + ETA payload.
                            // lastFrameSnr is the snap quality of the
                            // most-recent integrated frame; cumulativeSnr
                            // is the SNR of the running-mean accumulator
                            // (grows ~√N). targetSnr / etaFrames /
                            // etaSeconds drive the LIVE-tab "stack
                            // quality" widget. etaConfidence (R² of the
                            // log-log fit) is null when the ETA is
                            // null — UI shows "—" instead.
                            lastFrameSnr = liveStack.LastFrameSnr,
                            cumulativeSnr = liveStack.CumulativeSnr,
                            targetSnr = liveStack.TargetSnr,
                            etaFrames = liveStack.LastEta?.RemainingFrames,
                            etaSeconds = liveStack.LastEta?.RemainingSeconds,
                            etaConfidence = liveStack.LastEta?.Confidence,
                            // Stacking activity + dropped-frame visibility: true
                            // while a frame is being integrated, plus the running
                            // dropped count and the reason/time of the last drop.
                            isStacking = liveStack.IsStacking,
                            rejectedFrames = liveStack.RejectedFrames,
                            lastRejectReason = liveStack.LastRejectReason,
                            lastRejectAt = liveStack.LastRejectAt,
                            // 16-bit luminance histogram + stats of the colour
                            // stack (the broadcast frame is an 8-bit JPEG, so the
                            // client can't compute these itself). Null when not
                            // colour-stacking. Lets the LIVE histogram panel show
                            // real 16-bit min/max/mean/std + bars.
                            colorHistogram = liveStack.ColorActive ? liveStack.ColorHistogram : null,
                            colorHistMin = liveStack.ColorHistMin,
                            colorHistMax = liveStack.ColorHistMax,
                            colorHistMean = liveStack.ColorHistMean,
                            colorHistStd = liveStack.ColorHistStd,
                            triggers = liveStackTriggers.CurrentStatus,
                            // REFSUG-1: trend-based advisory. Always
                            // emitted so the UI can decide whether to
                            // render the chip / callout without polling.
                            refocusSuggestion = refocusSuggest.CurrentStatus,
                            // LSPP-3: per-frame pre-processing status.
                            // Counters update on every frame; master
                            // names hold across the session until the
                            // operator overrides (cache reset). UI in
                            // the LIVE tab consumes these for the
                            // "X calibrated / Y fallback" badges.
                            preProc = new {
                                calibration = new {
                                    enabled = profile.ActiveEquipmentProfile?.LiveStackPreProcessing?.CalibrationEnabled ?? false,
                                    masterDarkName = liveStack.PreProcStatus.MasterDarkUsed,
                                    masterFlatName = liveStack.PreProcStatus.MasterFlatUsed,
                                    masterBiasName = liveStack.PreProcStatus.MasterBiasUsed,
                                    framesCalibrated = liveStack.PreProcStatus.FramesCalibrated,
                                    framesFallback = liveStack.PreProcStatus.FramesCalibrationFallback,
                                    framesNoMatch = liveStack.PreProcStatus.FramesCalibrationNoMatch,
                                    lastError = liveStack.PreProcStatus.LastCalibrationError
                                },
                                bge = new {
                                    enabled = profile.ActiveEquipmentProfile?.LiveStackPreProcessing?.BgeEnabled ?? false,
                                    supportedThisSession = liveStack.BgeSupported,
                                    framesProcessed = liveStack.PreProcStatus.FramesBgeProcessed,
                                    framesFallback = liveStack.PreProcStatus.FramesBgeFallback,
                                    lastError = liveStack.PreProcStatus.LastBgeError,
                                    smoothing = profile.ActiveEquipmentProfile?.LiveStackPreProcessing?.BgeSmoothing ?? 1.0,
                                    correction = profile.ActiveEquipmentProfile?.LiveStackPreProcessing?.BgeCorrection ?? "Subtraction"
                                }
                            }
                        },
                        guider = guiderPayload,
                        autoFocus = autoFocusPayload,
                        meridianFlip = meridianPayload,
                        plan = planRunner.GetStatus(),
                        // Advanced sequencer runs entirely on the host; put
                        // its state on the 1 Hz stream so the activity-bar
                        // chip works from ANY tab and survives a client that
                        // disconnects and reconnects hours later (the ADV
                        // tab's 2 s poll only runs while that tab is open).
                        advSeq = new {
                            state = advEngine.State.ToString(),
                            framesCompleted = advEngine.FramesCompleted,
                            lastError = advEngine.LastError,
                            startedAt = advEngine.StartedAt
                        },
                        sequence = new {
                            state = seqStatus.State,
                            currentItemIndex = seqStatus.CurrentItemIndex,
                            currentFrameInItem = seqStatus.CurrentFrameInItem,
                            totalFrames = seqStatus.TotalFrames,
                            totalFramesCompleted = seqStatus.TotalFramesCompleted,
                            elapsedSeconds = seqStatus.ElapsedSeconds,
                            estimatedRemainingSeconds = seqStatus.EstimatedRemainingSeconds,
                            lastError = seqStatus.LastError,
                            items = seqStatus.Items,
                            dithersIssued = seqStatus.DithersIssued,
                            framesSinceDither = seqStatus.FramesSinceDither,
                            dither = seqStatus.Dither
                        },
                        // Camera video-stream lifecycle (PREVIEW tab Stream button).
                        // Always present so the UI button can read mode/fps even
                        // while idle.
                        cameraStream = new {
                            running = cameraStream.IsRunning,
                            mode = cameraStream.Mode,
                            exposure = cameraStream.ExposureSeconds,
                            gain = cameraStream.Gain,
                            frames = cameraStream.FrameCount,
                            fps = cameraStream.Fps,
                            captureFps = cameraStream.Fps,
                            transmitFps = cameraStream.TransmitFps,
                            lastError = cameraStream.LastError,
                            supportsNative = equip.Camera?.Capabilities.SupportsVideoStream ?? false
                        },
                        // KC-1: Keep Centered control loop. Top-level
                        // sibling of cameraStream so the VIDEO sidebar
                        // toggle can read phase + offset readout every
                        // tick without an extra REST poll. running=false
                        // when idle; phase cycles idle->calibrating->
                        // locked (with occasional lost in poor seeing).
                        keepCentered = new {
                            running = keepCentered.IsRunning,
                            phase = keepCentered.Phase,
                            lastOffsetPx = keepCentered.LastOffsetPx,
                            lastCorrectionMs = keepCentered.LastCorrectionMs
                        },
                        // Planetary recording lifecycle (VIDEO tab Capture).
                        videoRecording = new {
                            recording = videoRecording.IsRecording,
                            path = videoRecording.OutputPath,
                            frames = videoRecording.FrameCount,
                            bytes = videoRecording.BytesWritten,
                            durationSec = videoRecording.Duration.TotalSeconds,
                            droppedFrames = videoRecording.DroppedFrames,
                            lastError = videoRecording.LastError
                        },
                        // Planetary stack job (VIDEO tab Process). Null when idle.
                        videoStack = videoStacker.CurrentJob == null ? null : new {
                            id = videoStacker.CurrentJob.Id,
                            phase = videoStacker.CurrentJob.Phase.ToString(),
                            totalFrames = videoStacker.CurrentJob.TotalFrames,
                            framesAnalyzed = videoStacker.CurrentJob.FramesAnalyzed,
                            framesPicked = videoStacker.CurrentJob.FramesPicked,
                            framesAligned = videoStacker.CurrentJob.FramesAligned,
                            framesStacked = videoStacker.CurrentJob.FramesStacked,
                            outputPath = videoStacker.CurrentJob.OutputPath,
                            error = videoStacker.CurrentJob.Error,
                            done = videoStacker.CurrentJob.Phase
                                is NINA.Polaris.Services.Planetary.StackPhase.Ok
                                or NINA.Polaris.Services.Planetary.StackPhase.Fail
                        },
                        // FW-1: Flat Wizard state (AUTORUN → Flat Wizard
                        // sub-tab). Always emitted so the UI shutter +
                        // progress section can render "idle" without
                        // a separate poll. progress is null when the
                        // wizard never ran; populated by the service
                        // while running and after the last filter
                        // completes (FilterResults accumulates).
                        flatWizard = new {
                            state = flatWizard.State.ToString().ToLowerInvariant(),
                            lastError = flatWizard.LastError,
                            progress = flatWizard.State == FlatWizardState.Idle
                                       && flatWizard.Progress.TotalFilters == 0
                                ? null
                                : (object)new {
                                    startedAt = flatWizard.Progress.StartedAt,
                                    totalFilters = flatWizard.Progress.TotalFilters,
                                    currentFilterIndex = flatWizard.Progress.CurrentFilterIndex,
                                    currentFilter = flatWizard.Progress.CurrentFilter,
                                    phase = flatWizard.Progress.Phase,
                                    searchAttempt = flatWizard.Progress.SearchAttempt,
                                    currentExposure = flatWizard.Progress.CurrentExposure,
                                    lastMedian = flatWizard.Progress.LastMedian,
                                    totalFramesPerFilter = flatWizard.Progress.TotalFramesPerFilter,
                                    framesCaptured = flatWizard.Progress.FramesCaptured,
                                    filterResults = flatWizard.Progress.FilterResults
                                }
                        },
                        // Auto-slew-preview state (SKY tab inset card).
                        slewPreview = new {
                            enabled = slewPreview.Enabled,
                            active = slewPreview.IsPreviewActive,
                            slewing = slewPreview.LastDecision_Slewing,
                            captureIdle = slewPreview.LastDecision_CaptureIdle,
                            lastCheckedAt = slewPreview.LastCheckedAt,
                            lastError = slewPreview.LastError
                        },
                        // New blocks powering the bottom activity bar.
                        host = hostMetrics.Latest,
                        sirilJobs = sirilJobsPayload,
                        graXpertJobs = graXpertJobsPayload,
                        // CLOCK-1: serverUtcNow lets the client compute
                        // wall-clock skew against its own Date.now()
                        // every tick. When |skew| > 30s the activity
                        // bar shows a "Clock N off" chip and the
                        // Settings card surfaces a Sync button.
                        server = new {
                            utcNow = DateTime.UtcNow.ToString("o"),
                            clockSyncSupported = clockSync.IsSupported
                        },
                        // Server-pushed toasts (auto-connect outcomes,
                        // simulator events, etc.). Client de-dups by id
                        //, see toast pump in app.js.
                        notifications = notifications.Snapshot(),
                        // SIM-4: built-in equipment simulator status
                        // (which backend is active, is it installed,
                        // is the stack running, which devices). UI
                        // shows a green/amber chip + the Settings
                        // panel binds to these fields.
                        simulator = simulator.GetStatus(),
                        // WIFI-3: host WiFi state (mode + ssid + ip +
                        // signal). 501-class platforms (Windows /
                        // macOS / no nmcli / no wifi iface) still send
                        // the block, the supportedOs/nmcliInstalled/
                        // hasWifi flags + unsupportedReason tell the
                        // UI which banner to show.
                        network = new {
                            supportedOs       = network.IsSupportedOs,
                            nmcliInstalled    = network.NmcliInstalled,
                            hasWifi           = network.HasWifiInterface,
                            wifiInterface     = network.WifiInterface,
                            mode              = network.CurrentMode.ToString().ToLowerInvariant(),
                            ssid              = network.CurrentSsid,
                            ip                = network.CurrentIp,
                            signal            = network.SignalStrength,
                            hotspotSsid       = network.HotspotSsid,
                            lastError         = network.LastError,
                            unsupportedReason = network.UnsupportedReason,
                            lastRefreshAt     = network.LastRefreshAt,
                            // Auto AP fallback: when the rig is carried
                            // out of range of every saved network the
                            // watchdog starts the hotspot so it stays
                            // reachable. fallbackEngaged tells the UI to
                            // show "Hotspot started automatically".
                            autoHotspotFallback = network.AutoHotspotFallback,
                            fallbackEngaged     = network.HotspotFallbackEngaged
                        },
                        // Auto-push of saved images to network storage
                        // (SMB / SFTP / mounted path). Drives the Settings
                        // card's live status line. Password is never exposed.
                        storagePush = new {
                            enabled       = storagePush.Enabled,
                            kind          = storagePush.Kind,
                            connected     = storagePush.Connected,
                            queued        = storagePush.Queued,
                            uploaded      = storagePush.Uploaded,
                            failed        = storagePush.Failed,
                            currentFile   = storagePush.CurrentFile,
                            lastError     = storagePush.LastError,
                            lastUploadUtc = storagePush.LastUploadUtc?.ToString("o")
                        },
                        // A removable USB drive plugged in at runtime, awaiting the
                        // user's yes/no to move the capture home onto it. null when
                        // nothing is pending. See UsbDriveWatcherService.
                        usbDrive = usbWatcher.Pending is { } usb ? new {
                            path       = usb.Path,
                            label      = usb.Label,
                            freeBytes  = usb.FreeBytes,
                            totalBytes = usb.TotalBytes
                        } : null,
                        // The drive holding the capture home was unplugged, offering
                        // a revert to the default folder. null when nothing pending.
                        usbRemoved = usbWatcher.RevertPending is { } rp ? new {
                            label       = rp.RemovedLabel,
                            defaultPath = rp.DefaultPath
                        } : null,
                        // BENCH: compact progress for the Settings card.
                        // Full results are fetched over REST.
                        benchmark = new {
                            state    = benchmark.State,
                            progress = benchmark.Progress,
                            phase    = benchmark.Phase
                        },
                        // Sensor analysis (e/ADU, read noise, full well vs
                        // gain). Compact progress here; full result via REST.
                        sensorAnalysis = new {
                            state    = sensorAnalysis.State,
                            progress = sensorAnalysis.Progress,
                            phase    = sensorAnalysis.Phase
                        },
                        // PA-4: TPPA orchestrator state. CurrentJob is
                        // null until the user clicks Start; serialise a
                        // null-shaped object so the front-end can bind
                        // without null checks.
                        polarAlignment = polarAlign.CurrentJob == null ? null : new {
                            jobId = polarAlign.CurrentJob.Id,
                            phase = polarAlign.CurrentJob.Phase.ToString(),
                            mode = polarAlign.CurrentJob.Mode,
                            isActive = polarAlign.CurrentJob.IsActive,
                            points = polarAlign.CurrentJob.Points,
                            azErrorArcsec = polarAlign.CurrentJob.AzErrorArcsec,
                            altErrorArcsec = polarAlign.CurrentJob.AltErrorArcsec,
                            totalErrorArcsec = polarAlign.CurrentJob.TotalErrorArcsec,
                            lastError = polarAlign.CurrentJob.LastError,
                            startedAt = polarAlign.CurrentJob.StartedAt,
                            completedAt = polarAlign.CurrentJob.CompletedAt,
                            // True only while the CONTINUOUS refine loop runs
                            // (not during a single-shot manual Refresh) — the
                            // POLAR tab's Auto toggle mirrors this.
                            refineLoop = polarAlign.RefineLoopActive,
                            // RDPA-2: rudimentary-mode fields. Null in TPPA
                            // mode (the frontend gates on mode==='rudimentary'
                            // before reading these). Includes target +
                            // last solved + iteration sparkline data.
                            targetRaHours = polarAlign.CurrentJob.TargetRaHours,
                            targetDecDeg = polarAlign.CurrentJob.TargetDecDeg,
                            targetName = polarAlign.CurrentJob.TargetName,
                            solvedRaHours = polarAlign.CurrentJob.SolvedRaHours,
                            solvedDecDeg = polarAlign.CurrentJob.SolvedDecDeg,
                            iterationCount = polarAlign.CurrentJob.History.Count,
                            history = polarAlign.CurrentJob.History
                        },
                        // DBGLOG-5: ship new log entries since last tick
                        // (max 50 per tick). truncated=true if the
                        // cursor fell behind the ring-buffer head so the
                        // client knows it missed entries and should
                        // refetch via GET /api/logs.
                        debugLog = BuildDebugLogPayload(logService, ref localDebugCursor),
                        // Live plate-solve console output (STUDIO/FILES),
                        // streamed so the UI can show the solver running
                        // the same way the GraXpert local run does.
                        plateSolve = BuildPlateSolvePayload(plateSolveProgress),
                        // Server-authoritative current-exposure progress so
                        // every capture button's "Xs of Ys" countdown survives
                        // a reconnect. startedUtc + the server block's utcNow
                        // let the client compute elapsed without trusting its
                        // own (possibly skewed / freshly reloaded) clock.
                        capture = BuildCapturePayload(captureProgress),
                        decon = BuildDeconPayload(deconProgress),
                        // Opt-in server-owned LIVE loop state. running=true means
                        // the server is driving the LIVE session (the client only
                        // offloads stacking); the LIVE shutter binds to this so a
                        // reconnecting browser sees the session is still going.
                        liveCapture = new {
                            running = liveCapture.IsRunning,
                            exposure = liveCapture.ExposureSeconds,
                            gain = liveCapture.Gain,
                            binX = liveCapture.BinX,
                            frames = liveCapture.FrameCount,
                            lastError = liveCapture.LastError
                        },
                        // COOLRAMP: in-flight cooler ramps, keyed by slot
                        // ("main"/"aux"). A ramp takes ~14 min at the default
                        // 2°C/min, so the UI needs to show that the setpoint is
                        // still walking — otherwise the sensor sitting at 12°C
                        // with a -10°C target looks like a broken cooler.
                        cooling = BuildCoolingPayload(coolingRamp)
                    };

                    payload = JsonSerializer.SerializeToUtf8Bytes(status, JsonOpts);
                    lock (_statusCacheLock) {
                        _statusCacheBytes = payload;
                        _statusCacheTick = tick;
                        // Advance the shared cursor monotonically; if two
                        // clients raced this tick they both built, take the
                        // furthest. Long read/write under the lock is also
                        // what keeps the cursor torn-free on 32-bit ARM.
                        if (localDebugCursor > _sharedDebugCursor)
                            _sharedDebugCursor = localDebugCursor;
                    }
                    }

                    await SendBytesAsync(ws, payload!, cts.Token);
                    await Task.Delay(StatusInterval, cts.Token);
                } catch (OperationCanceledException) {
                    break;
                } catch (WebSocketException) {
                    break;
                } catch (Exception ex) {
                    logger.LogWarning(ex, "Status stream send error");
                    break;
                }
            }
        }, cts.Token);

        try {
            var buffer = new byte[256];
            while (ws.State == WebSocketState.Open) {
                using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                recvCts.CancelAfter(PingInterval * 3);

                var result = await ws.ReceiveAsync(buffer, recvCts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        } catch (OperationCanceledException) {
            logger.LogDebug("Status WebSocket receive timed out (client likely disconnected)");
        } catch (WebSocketException) {
            // Client disconnected abruptly
        }

        cts.Cancel();
        try { await sendTask; } catch { }

        await CloseGracefully(ws);
    }

    /// <summary>DBGLOG-5: build the per-tick <c>debugLog</c> sub-object.
    /// Mutates <paramref name="cursor"/> in place so the caller's
    /// per-connection state advances. Returns null when there's nothing
    /// new (caller can skip serialising) but actually serialises as the
    /// `null` field so the client sees consistent shape.</summary>
    private static object BuildDebugLogPayload(
        NINA.Polaris.Services.Logging.LogService svc, ref long cursor) {
        try {
            var snap = svc.SnapshotSince(cursor, max: 50);
            if (snap.Entries.Count > 0) cursor = snap.Cursor;
            return new {
                entries = snap.Entries,
                cursor = snap.Cursor,
                truncated = snap.Truncated,
                currentCursor = svc.CurrentId,
                oldestRetained = svc.OldestId
            };
        } catch {
            // Never let the debug-log subsystem take down the WS tick.
            return new {
                entries = System.Array.Empty<object>(),
                cursor,
                truncated = false,
                currentCursor = 0L,
                oldestRetained = 0L
            };
        }
    }

    /// <summary>Current-exposure progress sub-object. <c>active=false</c> with
    /// a null start when nothing is exposing. The client derives elapsed =
    /// serverNow - startedUtc and remaining = exposureSeconds - elapsed.</summary>
    /// <summary>COOLRAMP: per-slot cooler ramp state, e.g.
    /// <c>{ main: { running, target, setpoint, rate, source } }</c>. Slots with no
    /// ramp history are absent, so an empty object means "nothing ramping" and the
    /// UI can just hide the hint. Wrapped in try/catch to match the other builders:
    /// a status tick must never die over a cosmetic block.</summary>
    private static object BuildCoolingPayload(CoolingRampService svc) {
        try {
            var all = svc.SnapshotAll();
            var result = new Dictionary<string, object>();
            foreach (var (slot, s) in all) {
                result[slot] = new {
                    running = s.Running,
                    source = s.Source,
                    startC = Math.Round(s.StartC, 2),
                    targetC = Math.Round(s.TargetC, 2),
                    setpointC = Math.Round(s.SetpointC, 2),
                    rate = s.RatePerMinute
                };
            }
            return result;
        } catch {
            return new Dictionary<string, object>();
        }
    }

    private static object BuildCapturePayload(CaptureProgressService svc) {
        try {
            var s = svc.Snapshot();
            return new {
                runId = s.RunId,
                active = s.Active,
                source = s.Source,
                exposureSeconds = s.ExposureSeconds,
                startedUtc = s.StartedUtc?.ToString("o")
            };
        } catch {
            return new { runId = 0L, active = false, source = (string?)null,
                         exposureSeconds = 0.0, startedUtc = (string?)null };
        }
    }

    private static object BuildDeconPayload(DeconProgressService svc) {
        try {
            var s = svc.Snapshot();
            return new {
                runId = s.RunId,
                active = s.Active,
                phase = s.Phase,
                fraction = s.Fraction,
                elapsedSeconds = s.ElapsedSeconds,
                etaSeconds = s.EtaSeconds
            };
        } catch {
            return new { runId = 0L, active = false, phase = (string?)null,
                         fraction = 0.0, elapsedSeconds = 0.0, etaSeconds = (double?)null };
        }
    }

    private static object BuildPlateSolvePayload(
        NINA.Polaris.Services.PlateSolving.PlateSolveProgressService svc) {
        try {
            var s = svc.Snapshot();
            return new {
                runId = s.RunId,
                active = s.Active,
                source = s.Source,
                seq = s.Seq,
                truncated = s.Truncated,
                lines = s.Lines
            };
        } catch {
            return new { runId = 0L, active = false, source = (string?)null,
                         seq = 0L, truncated = false, lines = System.Array.Empty<string>() };
        }
    }

    private static async Task SendJsonAsync(System.Net.WebSockets.WebSocket ws, object data, CancellationToken ct) {
        var json = JsonSerializer.Serialize(data, JsonOpts);
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sendCts.CancelAfter(SendTimeout);
        await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, sendCts.Token);
    }

    /// <summary>PERF #365: send already-serialized UTF-8 JSON bytes (the
    /// per-tick shared status payload) without re-serializing per client.</summary>
    private static async Task SendBytesAsync(System.Net.WebSockets.WebSocket ws, byte[] utf8Json, CancellationToken ct) {
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sendCts.CancelAfter(SendTimeout);
        await ws.SendAsync(utf8Json, WebSocketMessageType.Text, true, sendCts.Token);
    }

    private static async Task CloseGracefully(System.Net.WebSockets.WebSocket ws) {
        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived) {
            try {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", closeCts.Token);
            } catch { }
        }
    }
}