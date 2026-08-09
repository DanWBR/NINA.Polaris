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
/// Live stacking: the accumulator, its triggers and its pre-processing counters.
///
/// Blocks owned: liveStack.
/// </summary>
public sealed class LiveStackStatusContributor : IStatusContributor {
    private readonly LiveStackingService _liveStack;
    private readonly LiveStackTriggersService _liveStackTriggers;
    private readonly ProfileService _profile;
    private readonly RefocusSuggestionService _refocusSuggest;

    public LiveStackStatusContributor(LiveStackingService liveStack, LiveStackTriggersService liveStackTriggers, ProfileService profile, RefocusSuggestionService refocusSuggest) {
        _liveStack = liveStack;
        _liveStackTriggers = liveStackTriggers;
        _profile = profile;
        _refocusSuggest = refocusSuggest;
    }

    public IReadOnlyCollection<string> Keys { get; } = new[] { "liveStack" };

    public void Contribute(StatusTick tick) {
        var liveStack = _liveStack;
        var liveStackTriggers = _liveStackTriggers;
        var profile = _profile;
        var refocusSuggest = _refocusSuggest;

            tick.Blocks["liveStack"] = new {
                isRunning = liveStack.GetStatus().IsRunning,
                frameCount = liveStack.GetStatus().FrameCount,
                width = liveStack.GetStatus().Width,
                height = liveStack.GetStatus().Height,
                referenceStarCount = liveStack.GetStatus().ReferenceStarCount,
                lastFrameHfr = liveStack.LastFrameMedianHfr,
                lastFrameStarCount = liveStack.LastFrameStarCount,
                lastFrameMean = liveStack.LastFrameMean,
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
                // Colour (OSC debayer → RGB) stacking: the EFFECTIVE decision
                // (auto-detected from the camera unless overridden), plus
                // whether it's actually engaged this session (wanted + the
                // reference frame was Bayered). Reporting the raw override
                // here was misleading once the LIVE tab toggle was removed:
                // it read False on every rig because nothing ever set it.
                colorStacking = liveStack.ColourWanted,
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
                // Per-channel bins on the same scale, so the panel can draw the
                // three RGB curves an OSC stack should show. Without these it
                // fell back to the luminance array and drew one white line.
                colorHistogramR = liveStack.ColorActive ? liveStack.ColorHistogramR : null,
                colorHistogramG = liveStack.ColorActive ? liveStack.ColorHistogramG : null,
                colorHistogramB = liveStack.ColorActive ? liveStack.ColorHistogramB : null,
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
            };

    }

}
