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

using NINA.Core.Enum;
using NINA.Guider.Portable;
using NINA.Image.Interfaces;
using PortableGuideStep = NINA.Guider.Portable.GuideStep;

namespace NINA.Polaris.Services;

// Part of the NativeGuider class — split from NativeGuider.cs for
// readability. See NativeGuider.cs for the type overview + fields.
public sealed partial class NativeGuider {
    // ----- Guide loop -----

    private enum LoopMode { Loop, Guide }

    private async Task StartLoopAsync(LoopMode mode) {
        await StopLoopAsync();
        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        _paused = false;
        _starLostCount = 0;
        _warned8Bit = false;
        SetAppState(mode == LoopMode.Guide ? "Guiding" : "Looping");
        _loopTask = Task.Run(() => LoopAsync(mode, token), token);
    }

    private async Task StopLoopAsync() {
        var cts = _loopCts;
        var task = _loopTask;
        if (cts == null) return;
        try { cts.Cancel(); } catch { }
        if (task != null) {
            try { await task.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        }
        _loopCts = null;
        _loopTask = null;
        IsSettling = false;
        IsDithering = false;
        _settleActive = false;
        if (AppState is "Guiding" or "Looping" or "Paused" or "LostLock") SetAppState("Stopped");
    }

    private async Task LoopAsync(LoopMode mode, CancellationToken ct) {
        var cam = _equipment.GuideCamera!;
        var mount = _equipment.Telescope;
        // Field-diagnostic: confirm the exposure/gain/bin the loop is
        // actually capturing with, so the debug log makes it obvious the
        // settings are being applied (and at what values).
        _logger.LogInformation(
            "Native guide loop ({Mode}) starting: exposure={ExpMs}ms gain={Gain} bin={Bin}",
            mode, Math.Max(50, Rig.NativeGuideExposureMs), Rig.NativeGuideGain, Rig.NativeGuideBin);
        try {
            while (!ct.IsCancellationRequested) {
                try {
                    if (mode == LoopMode.Loop) {
                        var limg = await CaptureFullAsync(cam, ct);
                        if (limg != null) {
                            _lastFrame = limg; _lastFrameOriginX = 0; _lastFrameOriginY = 0;
                            BuildView(double.NaN, double.NaN, 0, false);
                        }
                        continue;
                    }
                    if (_paused) {
                        await SettleAfterPulse(200, ct);
                        continue;
                    }
                    await GuideOnceAsync(cam, mount, ct);
                } catch (OperationCanceledException) {
                    break;
                } catch (Exception ex) {
                    // Never throw out of the loop. Log + continue.
                    _logger.LogError(ex, "Native guide loop iteration failed");
                    await SettleAfterPulse(500, ct);
                }
            }
        } finally {
            _logger.LogInformation("Native guide loop exited");
        }
    }

    private async Task GuideOnceAsync(ICamera cam, ITelescope? mount, CancellationToken ct) {
        int expMs = Math.Max(50, Rig.NativeGuideExposureMs);

        // React to a German-equatorial meridian flip (pier-side change) before
        // measuring, so this frame's correction uses the adjusted calibration.
        await HandlePierSideChangeAsync(cam, mount, ct);

        var (curX, curY, found, snr, hfd) = await MeasureGuideStarAsync(cam, ct);

        if (!found) {
            _starLostCount++;
            // Surface a distinct state the GUIDE UI already understands (PHD2
            // parity: StarLost -> "LostLock"). Without this the badge stayed
            // green "Guiding" through a whole cloud-out and the user had no
            // signal the star was gone. Skip the pulse; alert occasionally.
            if (AppState == "Guiding") SetAppState("LostLock");
            if (_starLostCount % 5 == 1) RaiseAlert("Guide star lost; skipping correction.");
            PushStep(new PortableGuideStep(NowMs(), 0, 0, 0, 0, 0, 0, snr, hfd, false));
            BuildView(curX, curY, snr, false);
            // Back off while the star is gone so a long cloud-out doesn't spin
            // the loop at full tilt hammering captures (and, on short exposures,
            // the shared INDI link). The dwell scales with the exposure period.
            int dwell = Math.Clamp(Math.Max(50, Rig.NativeGuideExposureMs), 200, 2000);
            try { await Task.Delay(dwell, ct); } catch (OperationCanceledException) { }
            return;
        }
        // Star reacquired: clear the lost-lock state so the UI goes back to
        // "Guiding" the moment a frame finds it again.
        if (_starLostCount > 0 && AppState == "LostLock") SetAppState("Guiding");
        _starLostCount = 0;

        double dx = curX - _lockX;
        double dy = curY - _lockY;
        var (raPx, decPx) = MountCoordTransform.CameraToMount(_calibration, dx, dy);

        // Frame interval for the (time-aware) predictive algorithm; reactive
        // algorithms ignore it. First frame falls back to the exposure period.
        long nowMs = NowMs();
        double dtSec = _lastGuideMs > 0 ? (nowMs - _lastGuideMs) / 1000.0 : expMs / 1000.0;
        _lastGuideMs = nowMs;

        // Per-axis algorithm: correction (pixels) to apply this frame.
        double raCorr = _raAlgo.Result(raPx, dtSec);
        double decCorr = _decAlgo.Result(decPx, dtSec);

        // Predicted next-frame error (pixels) when a predictive algorithm is
        // active, for the guide-chart overlay; 0 for reactive algorithms.
        double scaleP = PixelScale > 0 ? PixelScale : 1.0;
        double predRaAs = (_raAlgo as PredictiveAlgorithm)?.LastPredictedError * scaleP ?? 0.0;
        double predDecAs = (_decAlgo as PredictiveAlgorithm)?.LastPredictedError * scaleP ?? 0.0;

        // Rates: RA scaled for declination, Dec from calibration.
        double decRad = (mount != null && !double.IsNaN(mount.Declination))
            ? mount.Declination * Deg2Rad : _calibration.DeclinationRad;
        double raRate = MountCoordTransform.RaRateAtDec(_calibration, decRad);
        double decRate = _calibration.YRate;

        int minMoveRaMs = RateToMs(Rig.NativeMinMoveRaPx, raRate);
        int minMoveDecMs = RateToMs(Rig.NativeMinMoveDecPx, decRate);
        // Clamp each pulse to the smaller of the exposure period (so corrections
        // can't run past the next frame) and the per-axis Max Duration cap.
        int maxRaMs  = Math.Min(expMs, Math.Max(50, Rig.NativeMaxRaDurationMs));
        int maxDecMs = Math.Min(expMs, Math.Max(50, Rig.NativeMaxDecDurationMs));

        int raMs = MountCoordTransform.ComputeMoveDurationMs(raCorr, raRate, minMoveRaMs, maxRaMs);
        int decMs = MountCoordTransform.ComputeMoveDurationMs(decCorr, decRate, minMoveDecMs, maxDecMs);

        // Direction: correction moves the star back toward lock. PHD2
        // calibration measured WEST as +X-rate and SOUTH as +Y-rate, so a
        // positive RA error (star drifted +RA-px) is corrected by pulsing
        // EAST, positive Dec by NORTH.
        var raDir = raCorr >= 0 ? GuideDirections.guideEast : GuideDirections.guideWest;
        var decDir = decCorr >= 0 ? GuideDirections.guideNorth : GuideDirections.guideSouth;

        // Dec backlash compensation: on a direction reversal, add the measured
        // slack take-up, re-clamped to the runaway guard.
        if (decMs > 0) decMs = Math.Min(_backlashComp.Adjust(decDir, decMs), maxDecMs);

        if (mount != null && mount.IsConnected && mount.Capabilities.SupportsPulseGuide) {
            _mountLostCount = 0;
            try {
                if (raMs > 0) await mount.PulseGuideAsync(raDir, raMs, ct);
                if (decMs > 0) await mount.PulseGuideAsync(decDir, decMs, ct);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Pulse guide failed");
            }
        } else {
            // Mount went away mid-session: pulses are dropped, so the star drifts
            // and RMS climbs. Make that visible instead of failing silently.
            _mountLostCount++;
            if (_mountLostCount % 5 == 1)
                RaiseAlert("Mount not connected: guide pulses are being dropped.");
        }

        double scale = PixelScale > 0 ? PixelScale : 1.0;
        var step = new PortableGuideStep(
            NowMs(),
            raPx * scale, decPx * scale,
            raPx, decPx,
            raMs, decMs,
            snr, hfd, true,
            predRaAs, predDecAs);
        PushStep(step);
        BuildView(curX, curY, snr, true);

        // Settle progress (dither / start).
        if (_settler != null) {
            double totalErrPx = Math.Sqrt(raPx * raPx + decPx * decPx);
            long now = NowMs();
            var state = _settler.Update(totalErrPx, now);
            // Snapshot live progress for the WS/UI ASIAIR-style readout.
            _settleErrPx = totalErrPx;
            _settleBelowSec = _settler.BelowSeconds(now);
            _settleElapsedSec = _settler.ElapsedSeconds(now);
            if (state != GuidingSettler.State.Settling) {
                IsSettling = false;
                _settleActive = false;
                bool ok = state == GuidingSettler.State.Done;
                LastSettleStatus = ok ? "done" : "failed";
                // A dither just finished settling: drop the error history that
                // accumulated against the old lock so the RMS reflects only
                // post-dither guiding, not the dither excursion itself.
                if (IsDithering) _rms.Reset();
                IsDithering = false;
                Settled?.Invoke(new SettleResult {
                    Status = ok ? 0 : 1,
                    Error = ok ? null : "Settle timed out",
                    TotalFrames = 0,
                    DroppedFrames = 0
                });
                _settler = null;
            } else {
                IsSettling = true;
            }
        }

        // Delay so the loop cadence ≈ exposure period (capture already
        // consumed most of it; the camera blocks for the exposure).
        await Task.CompletedTask;
    }

    // ----- Capture + centroid helpers -----

    // Hard ceiling on how long a single guide/calibration capture may wait
    // for its BLOB. IndiCamera's own deadline is exposure + 60 s, sized for
    // big imaging downloads; for a guide cam that turns ONE dropped frame
    // into a 60 s+ stall (and, during calibration, aborts the whole run).
    // A guide BLOB is tiny, so exposure + this cushion is plenty even on a
    // Pi over USB + LAN, while failing fast enough to retry.
    private const int GuideCaptureCushionMs = 8000;

    // One-shot guard so we warn at most once per loop session (reset in
    // StartLoop) when the guide camera is actually delivering 8-bit frames.
    private bool _warned8Bit;

    /// <summary>Warn (once per session) when the guide frames come back
    /// effectively 8-bit despite the RAW16 enforcement in the camera adapter.
    /// An 8-bit FITS frame is decoded into the HIGH byte of each 16-bit sample
    /// (value &lt;&lt; 8), so every pixel's low byte is zero and only 256 grey
    /// levels exist — the preview posterizes ("totally pixelated", few B&amp;W
    /// nuances) even though guiding still works on the raw star peaks. The fix
    /// is to set the camera capture format to RAW16 (RIGS / INDI panel); this
    /// just tells the user that's what's happening.</summary>
    private void WarnIfEightBitOnce(IImageData img) {
        if (_warned8Bit) return;
        var d = img.Data;
        if (d == null || d.Length == 0) return;
        // Sample evenly across the frame: if every non-zero sample has a zero
        // low byte, the data is 8-bit shifted into the high byte. A genuine
        // 12/16-bit frame has varied low bytes, so this won't false-positive.
        int n = d.Length;
        int step = Math.Max(1, n / 4096);
        bool anyNonZero = false, anyLowByte = false;
        for (int i = 0; i < n; i += step) {
            ushort v = d[i];
            if (v == 0) continue;
            anyNonZero = true;
            if ((v & 0xFF) != 0) { anyLowByte = true; break; }
        }
        if (!anyNonZero) return;   // empty / dropped frame; re-check next time
        _warned8Bit = true;
        if (!anyLowByte) {
            RaiseAlert(
                "Guide camera is sending 8-bit (RAW8) frames — the preview will look " +
                "posterized/pixelated. Set the camera capture format to RAW16 " +
                "(RIGS or the INDI control panel) for smooth guide images.", "warn");
        }
    }

    private async Task<IImageData?> CaptureFullAsync(ICamera cam, CancellationToken ct) {
        int expMs = Math.Max(50, Rig.NativeGuideExposureMs);
        int bin = Math.Clamp(Rig.NativeGuideBin <= 0 ? 1 : Rig.NativeGuideBin, 1, 4);
        // Clamp the configured gain to the guide camera's real range. A stale
        // or out-of-range profile value (e.g. carried over from a different
        // camera) makes some SDKs wrap/saturate and the guide star brightness
        // jumps around erratically; clamping keeps it within [GainMin, GainMax].
        int? gain = null;
        if (Rig.NativeGuideGain > 0) {
            var g = Rig.NativeGuideGain;
            if (cam.GainMax > cam.GainMin && cam.GainMax > 0)
                g = Math.Clamp(g, cam.GainMin, cam.GainMax);
            gain = g;
        }
        var opts = new CaptureOptions(Gain: gain, BinX: bin, BinY: bin);
        // Bound the capture to a guide-sized budget. CaptureAsync honours the
        // token (it registers cancellation on its BLOB TCS), so a linked CTS
        // that fires our deadline unblocks the await without waiting on the
        // imaging-sized 60 s budget. A dropped BLOB is common at RA direction
        // reversal on USB guide cams (e.g. ASI120MM Mini) when motor inrush
        // glitches the camera/USB; here it fails in seconds so the caller can
        // re-capture instead of the whole sequence dying.
        int budgetMs = expMs + GuideCaptureCushionMs;
        using var capCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        capCts.CancelAfter(budgetMs);
        try {
            var img = await cam.CaptureAsync(expMs / 1000.0, opts, capCts.Token);
            // Apply the dark library / bad-pixel map (per NativeGuideCalibrationMode)
            // before any consumer sees the frame, so star detection, the multi-star
            // tracker, calibration and the live view all run on a calibrated frame.
            if (img?.Data != null) {
                ApplyCalibrationInPlace(img.Data, img.Properties.Width, img.Properties.Height);
                WarnIfEightBitOnce(img);
            }
            return img;
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            // Our budget elapsed, not a user Stop: a dropped/stalled frame.
            // Abort the exposure so the driver resets before the next attempt.
            _logger.LogWarning("Guide capture exceeded {Ms} ms budget (dropped BLOB?); aborting to recover", budgetMs);
            // Bound the abort: if the INDI link itself wedged (dropped BLOB at
            // RA reversal / cloud-out on a USB guide cam), an unbounded abort
            // hangs the loop forever -- Stop then times out and abandons a
            // zombie that still holds the guide camera, so reconnecting can't
            // recover and the user is forced over to external PHD2. Cap it so
            // the loop always stays responsive to Stop/Disconnect.
            try {
                using var abortCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await cam.AbortExposureAsync(abortCts.Token);
            } catch { }
            return null;
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Guide capture failed");
            return null;
        }
    }

    /// <summary>
    /// Detect a German-equatorial pier-side change (meridian flip) mid-session
    /// and react per the rig setting: "mirror" adjusts the existing calibration
    /// in place (PHD2-style: RA angle + 180 deg, optional Dec flip), avoiding a
    /// recalibration; "recalibrate" runs a fresh calibration; "off" ignores it.
    /// No-op when either the calibration side or the current side is unknown, so
    /// a driver that doesn't report SideOfPier never triggers a bogus flip.
    /// </summary>
    private async Task HandlePierSideChangeAsync(ICamera cam, ITelescope? mount, CancellationToken ct) {
        if (mount == null || !_calibration.IsValid) return;
        var mode = (Rig.NativePierSideHandling ?? "mirror").Trim().ToLowerInvariant();
        if (mode == "off") return;

        var nowSide = mount.SideOfPier;
        if (nowSide == PierSide.pierUnknown) return; // driver doesn't report it
        // Compare against the running baseline seeded at guiding start, NOT the
        // side stamped into the calibration. A freshly measured (or reconciled)
        // calibration is ground truth for the current side, so we must never
        // mirror it on the strength of a stale stamped side — we react only to a
        // pier side that CHANGES while guiding. (Lazily adopt the baseline here
        // if it was unknown at start, e.g. the driver only reported it later.)
        if (_loopPierBaseline == PierSide.pierUnknown) {
            _loopPierBaseline = nowSide;
            return;
        }
        if (nowSide == _loopPierBaseline) return; // no flip observed

        if (mode == "recalibrate") {
            RaiseAlert($"Pier side changed to {nowSide}; recalibrating.");
            // Force a fresh star pick + calibration on the new side.
            _haveLock = false;
            _multiStar.Clear();
            await CalibrateAsync(ct);
            if (_calibration.IsValid) {
                await BuildMultiStarAsync(ct);
                _raAlgo.Reset();
                _decAlgo.Reset();
                _backlashComp.Reset();
                _loopPierBaseline = nowSide;
                SetAppState("Guiding");
            } else {
                RaiseAlert("Recalibration after pier flip failed; guiding paused.");
            }
            return;
        }

        // Default: mirror the calibration in place.
        _calibration = MountCoordTransform
            .FlipForPierChange(_calibration, Rig.NativeReverseDecAfterFlip)
            with { CalibrationPierSide = nowSide };
        _loopPierBaseline = nowSide;
        _raAlgo.Reset();
        _decAlgo.Reset();
        _backlashComp.Reset();
        // The field also rotated 180 deg, so re-seed the multi-star set and the
        // lock star on the new side.
        _haveLock = false;
        await AutoSelectStarAsync(ct);
        if (_haveLock) {
            await BuildMultiStarAsync(ct);
            // AutoSelectStarAsync set the state to "Selected"; we're still
            // actively guiding on the new side, so restore "Guiding" —
            // otherwise the badge stays stuck on "Selected" after the flip
            // even though the loop keeps correcting (as the recalibrate branch
            // above already does on success).
            SetAppState("Guiding");
        }
        RaiseAlert($"Pier side changed to {nowSide}; calibration mirrored.");
    }

    /// <summary>Measure the guide-star field offset this frame. When multi-star
    /// is engaged (rig enabled + more than one star locked) it captures a full
    /// frame, recentres every tracked star and returns the robust combined
    /// offset expressed as an effective primary position (lock + offset), so the
    /// caller's <c>cur - lock</c> math is unchanged. Otherwise it falls back to
    /// the single-star ROI path.</summary>
    private async Task<(double x, double y, bool found, double snr, double hfd)>
            MeasureGuideStarAsync(ICamera cam, CancellationToken ct) {
        bool useMulti = Rig.NativeMultiStar && _multiStar.Count > 1;
        if (!useMulti) {
            return await FindStarDetailedAsync(cam, ct);
        }
        // Multi-star needs the whole field, so clear any ROI.
        try { await cam.SetSubframeAsync(0, 0, 0, 0, ct); } catch { }
        var img = await CaptureFullAsync(cam, ct);
        if (img == null) return (_lockX, _lockY, false, 0, 0);
        _lastFrame = img; _lastFrameOriginX = 0; _lastFrameOriginY = 0;
        var res = _multiStar.Update(img.Data, img.Properties.Width, img.Properties.Height);
        if (!res.Found) return (_lockX, _lockY, false, res.Snr, res.Hfd);
        return (_lockX + res.OffsetX, _lockY + res.OffsetY, true, res.Snr, res.Hfd);
    }

    /// <summary>Detect a primary + secondary guide stars on a fresh full frame
    /// and seed the multi-star tracker. The primary reference is the current
    /// lock; secondaries are the next-brightest interior, non-saturated stars
    /// kept a minimum distance apart. No-op (single-star) when disabled, the
    /// max is 1, or fewer than two suitable stars exist.</summary>
    private async Task BuildMultiStarAsync(CancellationToken ct) {
        _multiStar.Clear();
        if (!Rig.NativeMultiStar) return;
        int maxStars = Math.Clamp(Rig.NativeMaxGuideStars, 1, 12);
        if (maxStars <= 1 || !_haveLock) return;

        var cam = _equipment.GuideCamera!;
        try { await cam.SetSubframeAsync(0, 0, 0, 0, ct); } catch { }
        var img = await CaptureFullAsync(cam, ct);
        if (img == null) return;

        int w = img.Properties.Width, h = img.Properties.Height;
        var detector = new NINA.Image.ImageAnalysis.StarDetector();
        var stars = detector.Detect(img.Data, w, h);

        int margin = SearchRegion + 5;
        double satGuard = (1 << Math.Max(1, img.Properties.BitDepth)) - 1;
        double minSep = SearchRegion * 3.0;

        var refs = new List<(double x, double y)> { (_lockX, _lockY) };
        foreach (var s in stars
                     .Where(s => s.X >= margin && s.Y >= margin &&
                                 s.X <= w - margin && s.Y <= h - margin)
                     .Where(s => !(satGuard > 1 && s.Peak >= satGuard * 0.95))
                     .OrderByDescending(s => s.Flux)) {
            if (refs.Count >= maxStars) break;
            bool near = refs.Any(r => (r.x - s.X) * (r.x - s.X) +
                                      (r.y - s.Y) * (r.y - s.Y) < minSep * minSep);
            if (near) continue;
            refs.Add((s.X, s.Y));
        }

        if (refs.Count > 1) {
            _multiStar.Reset(refs);
            _logger.LogInformation("Native multi-star: tracking {N} stars", refs.Count);
        } else {
            _logger.LogInformation("Native multi-star: only the primary star found; single-star guiding");
        }
    }

    private async Task<(double x, double y, bool found)> FindStarAsync(ICamera cam, CancellationToken ct,
            int? searchRegion = null) {
        var (x, y, found, _, _) = await FindStarDetailedAsync(cam, ct, searchRegion);
        return (x, y, found);
    }

    /// <summary>Capture + locate the guide star with a few retries. A single
    /// dropped INDI BLOB (common at RA direction reversal on USB guide cams
    /// such as the ASI120MM Mini, where motor inrush glitches the camera)
    /// must never abort the whole calibration, so re-capture (no extra pulse)
    /// a few times before giving up. Returns found=false only if every
    /// attempt fails.</summary>
    private async Task<(double x, double y, bool found)> FindStarWithRetryAsync(
            ICamera cam, CancellationToken ct, int? searchRegion = null, int attempts = 3) {
        for (int a = 1; a <= attempts; a++) {
            ct.ThrowIfCancellationRequested();
            var (x, y, found) = await FindStarAsync(cam, ct, searchRegion);
            if (found) {
                if (a > 1) _logger.LogInformation("Calibration capture recovered on attempt {A}/{N}", a, attempts);
                return (x, y, true);
            }
            if (a < attempts) {
                _logger.LogWarning("Calibration capture attempt {A}/{N} found no star (dropped frame?); retrying", a, attempts);
                _calProgress = $"Recovering dropped frame ({a}/{attempts})...";
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { }
            }
        }
        return (_lockX, _lockY, false);
    }

    private async Task<(double x, double y, bool found, double snr, double hfd)>
            FindStarDetailedAsync(ICamera cam, CancellationToken ct, int? searchRegion = null) {
        // Always capture the full frame: GuideStar.Find already searches only a
        // small window around the lock, so a hardware ROI buys little and has two
        // downsides we hit in practice -- the GUIDE view then showed a tiny
        // cropped, dark thumbnail, and SetSubframe mutates the (possibly shared)
        // INDI device's frame state, which leaked into the imaging camera.
        try { await cam.SetSubframeAsync(0, 0, 0, 0, ct); } catch { }
        var img = await CaptureFullAsync(cam, ct);
        if (img == null) return (_lockX, _lockY, false, 0, 0);
        _lastFrame = img; _lastFrameOriginX = 0; _lastFrameOriginY = 0;

        int w = img.Properties.Width, h = img.Properties.Height;
        int sr = searchRegion ?? SearchRegion;
        var result = GuideStar.Find(img.Data, w, h, _lockX, _lockY, sr);
        if (!result.Found) {
            return (_lockX, _lockY, false, result.Snr, result.Hfd);
        }
        return (result.X, result.Y, true, result.Snr, result.Hfd);
    }

    // ----- Internals -----

}
