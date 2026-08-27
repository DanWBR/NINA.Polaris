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

namespace NINA.Polaris.WebSocket.Status;

/// <summary>
/// Everything that opens the shutter, plus the cooler ramp that gates it.
///
/// Blocks owned: capture, liveCapture, auxCapture, cooling, videoRecording, videoStack, slewPreview.
/// </summary>
public sealed class CaptureStatusContributor : IStatusContributor {
    private readonly AuxCaptureService _auxCapture;
    private readonly CaptureProgressService _captureProgress;
    private readonly CoolingRampService _coolingRamp;
    private readonly LiveCaptureService _liveCapture;
    private readonly SlewPreviewService _slewPreview;
    private readonly NINA.Polaris.Services.Planetary.VideoRecordingService _videoRecording;
    private readonly NINA.Polaris.Services.Planetary.PlanetaryStackerService _videoStacker;
    private readonly NINA.Polaris.Services.Timelapse.MediaEncodeService _mediaEncode;
    private readonly NINA.Polaris.Services.StarTrail.StarTrailService _starTrail;
    private readonly DitherBarrier _ditherBarrier;
    private readonly MultiImagerCaptureService _multiImager;

    public CaptureStatusContributor(AuxCaptureService auxCapture, CaptureProgressService captureProgress, CoolingRampService coolingRamp, LiveCaptureService liveCapture, SlewPreviewService slewPreview, NINA.Polaris.Services.Planetary.VideoRecordingService videoRecording, NINA.Polaris.Services.Planetary.PlanetaryStackerService videoStacker, NINA.Polaris.Services.Timelapse.MediaEncodeService mediaEncode, NINA.Polaris.Services.StarTrail.StarTrailService starTrail, DitherBarrier ditherBarrier, MultiImagerCaptureService multiImager) {
        _auxCapture = auxCapture;
        _captureProgress = captureProgress;
        _coolingRamp = coolingRamp;
        _liveCapture = liveCapture;
        _slewPreview = slewPreview;
        _videoRecording = videoRecording;
        _videoStacker = videoStacker;
        _mediaEncode = mediaEncode;
        _starTrail = starTrail;
        _ditherBarrier = ditherBarrier;
        _multiImager = multiImager;
    }

    public IReadOnlyCollection<string> Keys { get; } = new[] { "capture", "liveCapture", "auxCapture", "cooling", "videoRecording", "videoStack", "mediaEncode", "starTrail", "slewPreview", "ditherSync", "multiImager" };

    public void Contribute(StatusTick tick) {
        var auxCapture = _auxCapture;
        var captureProgress = _captureProgress;
        var coolingRamp = _coolingRamp;
        var liveCapture = _liveCapture;
        var slewPreview = _slewPreview;
        var videoRecording = _videoRecording;
        var videoStacker = _videoStacker;

            tick.Blocks["capture"] = BuildCapturePayload(captureProgress);

            tick.Blocks["liveCapture"] = new {
                running = liveCapture.IsRunning,
                exposure = liveCapture.ExposureSeconds,
                gain = liveCapture.Gain,
                binX = liveCapture.BinX,
                frames = liveCapture.FrameCount,
                lastError = liveCapture.LastError
            };

            // COOLRAMP: in-flight cooler ramps, keyed by slot
            // ("main"/"aux"). A ramp takes ~14 min at the default
            // 2°C/min, so the UI needs to show that the setpoint is
            // still walking — otherwise the sensor sitting at 12°C
            // with a -10°C target looks like a broken cooler.
            tick.Blocks["auxCapture"] = new {
                running = auxCapture.IsRunning,
                frameCount = auxCapture.FrameCount,
                lastError = auxCapture.LastError,
                noOutputDir = auxCapture.NoOutputDir
            };

            // Multi-camera synchronized dither: whether the barrier is
            // coordinating (>=2 imaging cameras active), and whether a
            // synchronized dither round is in flight right now.
            tick.Blocks["ditherSync"] = new {
                active = _ditherBarrier.OwnsDither,
                waiting = _ditherBarrier.RoundActive,
                dithering = _ditherBarrier.Dithering,
                enabled = _ditherBarrier.Enabled,
                participants = _ditherBarrier.ActiveParticipants,
                owner = _ditherBarrier.CadenceOwner,
                strategy = _ditherBarrier.CurrentStrategy
            };

            // STAGE2: per-extra-imager capture loops (index 2+). Empty list when
            // no additional imaging cameras are running, so the UI can hide the
            // section. The main + aux slots stay on "capture"/"auxCapture".
            tick.Blocks["multiImager"] = _multiImager.Snapshot()
                .Select(s => new {
                    index = s.Index,
                    role = s.Role,
                    frameCount = s.FrameCount,
                    lastError = s.LastError
                }).ToList();

            // Stack status + triggers (LSTR-4). Triggers sub-object
            // carries last-action timestamps + reference RA/Dec +
            // executing flag so the UI banner + status lines can
            // render without a separate poll.
            tick.Blocks["cooling"] = BuildCoolingPayload(coolingRamp);

            tick.Blocks["videoRecording"] = new {
                recording = videoRecording.IsRecording,
                path = videoRecording.OutputPath,
                frames = videoRecording.FrameCount,
                bytes = videoRecording.BytesWritten,
                durationSec = videoRecording.Duration.TotalSeconds,
                droppedFrames = videoRecording.DroppedFrames,
                lastError = videoRecording.LastError
            };

            // Planetary stack job (VIDEO tab Process). Null when idle.
            tick.Blocks["videoStack"] = videoStacker.CurrentJob == null ? null : new {
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
            };

            // Time-lapse / SER->MP4 encode job (VIDEO tab). Null when idle.
            var enc = _mediaEncode.CurrentJob;
            tick.Blocks["mediaEncode"] = enc == null ? null : new {
                id = enc.Id,
                phase = enc.Phase.ToString(),
                totalFrames = enc.TotalFrames,
                framesRendered = enc.FramesRendered,
                encodedFrames = enc.EncodedFrames,
                gifDone = enc.GifDone,
                mp4Done = enc.Mp4Done,
                outputPathGif = enc.OutputPathGif,
                outputPathMp4 = enc.OutputPathMp4,
                error = enc.Error,
                done = enc.Phase is NINA.Polaris.Services.Timelapse.EncodePhase.Ok
                    or NINA.Polaris.Services.Timelapse.EncodePhase.Fail
            };

            // Star-trails capture + MAX composite (VIDEO tab). Null when idle.
            // The growing composite itself rides the image stream (FrameKind
            // StarTrail); this block is just the job counters.
            var st = _starTrail.CurrentJob;
            tick.Blocks["starTrail"] = st == null ? null : new {
                id = st.Id,
                phase = st.Phase.ToString(),
                framesCaptured = st.FramesCaptured,
                exposureSec = st.ExposureSeconds,
                elapsedSec = ((st.CompletedAt ?? DateTime.UtcNow) - st.StartedAt).TotalSeconds,
                trackingOff = st.TrackingOff,
                outputPathFits = st.OutputPathFits,
                outputPathJpg = st.OutputPathJpg,
                error = st.Error,
                done = st.Phase is NINA.Polaris.Services.StarTrail.StarTrailPhase.Ok
                    or NINA.Polaris.Services.StarTrail.StarTrailPhase.Fail
            };

            // FW-1: Flat Wizard state (AUTORUN → Flat Wizard
            // sub-tab). Always emitted so the UI shutter +
            // progress section can render "idle" without
            // a separate poll. progress is null when the
            // wizard never ran; populated by the service
            // while running and after the last filter
            // completes (FilterResults accumulates).
            tick.Blocks["slewPreview"] = new {
                enabled = slewPreview.Enabled,
                active = slewPreview.IsPreviewActive,
                slewing = slewPreview.LastDecision_Slewing,
                captureIdle = slewPreview.LastDecision_CaptureIdle,
                lastCheckedAt = slewPreview.LastCheckedAt,
                lastError = slewPreview.LastError
            };

            // New blocks powering the bottom activity bar.
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
}
