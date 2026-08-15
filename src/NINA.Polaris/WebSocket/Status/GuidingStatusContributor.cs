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

namespace NINA.Polaris.WebSocket.Status;

/// <summary>
/// The guider block, and the five precomputed payloads that feed it.
///
/// Blocks owned: guider.
/// </summary>
public sealed class GuidingStatusContributor : IStatusContributor {
    private readonly EquipmentManager _equip;
    private readonly ActiveGuiderProvider _guiders;
    private readonly PHD2CalibrationOrchestrator _phd2Calibration;
    private readonly Phd2GuiSessionService _phd2Gui;
    private readonly Phd2VncSessionService _phd2Vnc;
    private readonly PHD2ProfileSyncService _profileSync;

    private readonly GuideRunawayGuard _guard;

    public GuidingStatusContributor(EquipmentManager equip, ActiveGuiderProvider guiders, PHD2CalibrationOrchestrator phd2Calibration, Phd2GuiSessionService phd2Gui, Phd2VncSessionService phd2Vnc, PHD2ProfileSyncService profileSync, GuideRunawayGuard guard) {
        _guard = guard;
        _equip = equip;
        _guiders = guiders;
        _phd2Calibration = phd2Calibration;
        _phd2Gui = phd2Gui;
        _phd2Vnc = phd2Vnc;
        _profileSync = profileSync;
    }

    public IReadOnlyCollection<string> Keys { get; } = new[] { "guider" };

    public void Contribute(StatusTick tick) {
        var equip = _equip;
        var guiders = _guiders;
        var phd2Calibration = _phd2Calibration;
        var phd2Gui = _phd2Gui;
        var phd2Vnc = _phd2Vnc;
        var profileSync = _profileSync;

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
                // Min move + Dec guide mode ride along with aggression:
                // the client replaces its whole `guider` object on every
                // tick, so anything missing here resets the sliders.
                minMoveRaPx = activeGuider.MinMoveRaPx,
                minMoveDecPx = activeGuider.MinMoveDecPx,
                decGuideMode = activeGuider.DecGuideMode,
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
                activity = activeGuider.Activity,
                activityExposureMs = activeGuider.ActivityExposureMs,
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
                vncSession = vncSessionPayload,
                // How guiding is doing against its OWN normal for this session,
                // plus what the runaway guard has had to do. Advisory: the UI
                // shows it, nothing acts on it.
                health = new {
                    degraded = _guard.Degraded,
                    degradedSinceUtc = _guard.DegradedSinceUtc,
                    baselineRms = _guard.BaselineRmsArcsec,
                    currentRms = _guard.CurrentRmsArcsec,
                    restarts = _guard.RestartsThisSession,
                    lastRestartUtc = _guard.LastRestartUtc,
                    budgetExhausted = _guard.BudgetExhausted
                }
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

            tick.Blocks["guider"] = guiderPayload;

    }

}
