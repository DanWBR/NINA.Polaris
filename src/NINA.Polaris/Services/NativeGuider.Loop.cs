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
        _slewHold = false;
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
        // Keep what the predictive algorithm learned about this mount's worm
        // before the run's state goes away. Here rather than per frame: one
        // profile write per session instead of one per guide frame.
        PersistPredictiveModel();
        IsSettling = false;
        IsDithering = false;
        _settleActive = false;
        SetActivity(null);
        if (AppState is "Guiding" or "Looping" or "Paused" or "LostLock") SetAppState("Stopped");
        // MEMOPT2: guiding retains almost nothing but churns ~2-3 full guide
        // frames + a preview JPEG per capture; under Workstation GC those freed
        // LOH segments sit as a high plateau (hundreds of MB) until the next
        // collection. Stopping the loop is a user-paced action, so compact once
        // here to hand that memory back to the OS on the SBC. Never per frame.
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private async Task LoopAsync(LoopMode mode, CancellationToken ct) {
        var cam = _equipment.GuideCamera!;
        var mount = _equipment.Telescope;
        // Field-diagnostic: confirm the exposure/gain/bin the loop is
        // actually capturing with, so the debug log makes it obvious the
        // settings are being applied (and at what values).
        _logger.LogInformation(
            "Native guide loop ({Mode}) starting: exposure={ExpMs}ms gain={Gain} bin={Bin}",
            mode, Math.Max(50, Rig.NativeGuideExposureMs), EffectiveGuideGain, Rig.NativeGuideBin);
        try {
            while (!ct.IsCancellationRequested) {
                long iterStart = NowMs();
                try {
                    if (mode == LoopMode.Loop) {
                        var limg = await CaptureFullAsync(cam, ct, "Exposing");
                        if (limg != null) {
                            _lastFrame = limg; _lastFrameOriginX = 0; _lastFrameOriginY = 0;
                            BuildView(double.NaN, double.NaN, 0, false);
                        }
                    } else if (_paused) {
                        await SettleAfterPulse(200, ct);
                    } else if (MountIsSlewing(mount)) {
                        // A GoTo / slew-and-center is moving the mount under the
                        // guide loop: the locked star is leaving the frame. Hold —
                        // never pulse on a moving star — and remember a slew
                        // happened so we re-acquire once it stops.
                        if (!_slewHold) {
                            _slewHold = true;
                            SetAppState("Slewing");
                            _logger.LogInformation("Native guiding: mount slewing, holding");
                        }
                        await SettleAfterPulse(300, ct);
                    } else if (_slewHold) {
                        // Slew finished: settle mechanically, drop the stale lock,
                        // pick a FRESH star on the new field and resume guiding —
                        // exactly what the operator expects after any slew.
                        _slewHold = false;
                        try {
                            await Task.Delay(TimeSpan.FromSeconds(2), ct);
                            _haveLock = false;
                            await AutoSelectStarAsync(ct);
                            if (_haveLock) {
                                _raAlgo?.Reset(); _decAlgo?.Reset();
                                await BuildMultiStarAsync(ct);
                                SetAppState("Guiding");
                                _logger.LogInformation("Native guiding re-acquired a star after the slew");
                            } else {
                                RaiseAlert("Guiding paused after slew: no guide star found; select one manually.");
                            }
                        } catch (OperationCanceledException) { throw; }
                        catch (Exception ex) {
                            _logger.LogWarning(ex, "Post-slew guide re-acquire failed");
                        }
                    } else {
                        await GuideOnceAsync(cam, mount, ct);
                    }
                } catch (OperationCanceledException) {
                    break;
                } catch (Exception ex) {
                    // Never throw out of the loop. Log + continue.
                    _logger.LogError(ex, "Native guide loop iteration failed");
                    await SettleAfterPulse(500, ct);
                }
                // MEMOPT2: cadence floor. The loop was paced only by the camera
                // blocking for the exposure; a cam that returns faster than the
                // nominal exposure (short exposures, cached/streamed frames, or a
                // capture that fails fast) spun the loop and churned a full guide
                // frame + preview per iteration — the +500 MB GC plateau. Sleep
                // the remainder of the exposure period so the loop runs at ~1
                // frame / exposure. The star-lost + error paths already dwell,
                // so their elapsed >= period and this adds nothing there.
                int period = Math.Max(50, Rig.NativeGuideExposureMs);
                int rest = period - (int)(NowMs() - iterStart);
                if (rest > 0) {
                    try { await Task.Delay(rest, ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
        } finally {
            _logger.LogInformation("Native guide loop exited");
        }
    }

    /// <summary>True when the mount reports it is slewing. Guarded: a driver that
    /// throws on the IsSlewing read must never break the guide loop (it just means
    /// "not slewing" for this frame).</summary>
    private static bool MountIsSlewing(ITelescope? mount) {
        if (mount == null || !mount.IsConnected) return false;
        try { return mount.IsSlewing; }
        catch { return false; }
    }

    private async Task GuideOnceAsync(ICamera cam, ITelescope? mount, CancellationToken ct) {
        int expMs = Math.Max(50, Rig.NativeGuideExposureMs);

        // React to a German-equatorial meridian flip (pier-side change) before
        // measuring, so this frame's correction uses the adjusted calibration.
        if (await HandleMountSlewAsync(cam, mount, ct)) return;

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

            // RE-ACQUISITION. Widening the search (see RecoverySearchRegionFor)
            // handles a star that merely drifted; it can't help once the star has
            // left even the widened window. Then the ONLY thing that recovered
            // guiding was the user doing stop → loop → start by hand, which
            // re-detects on the full frame. Field report: "a guiagem nativa não
            // sobrevive a alguns instantes de nuvens — não consegue pegar a estrela
            // de volta e realinhar. Tenho que dar stop, loop e depois start guiding
            // manualmente. Isso é crítico para processos de captura longa."
            // Do that automatically instead, once, after the widened search has had
            // its chance.
            if (_starLostCount == ReacquireAfterLostFrames) {
                await TryReacquireAsync(cam, ct);
            }

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

        // Declination guide mode: applied here, at the move stage, so the
        // algorithm still sees every error.
        if (SuppressesDecPulse(Rig.NativeDecGuideMode, decDir)) decMs = 0;

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

    private async Task<IImageData?> CaptureFullAsync(ICamera cam, CancellationToken ct, string? phase = null) {
        int expMs = Math.Max(50, Rig.NativeGuideExposureMs);
        int bin = Math.Clamp(Rig.NativeGuideBin <= 0 ? 1 : Rig.NativeGuideBin, 1, 4);
        // Gain comes from the single resolver (EffectiveGuideGain) so the
        // capture, the dark library and the log can never disagree about
        // which gain the sensor is running at.
        int? gain = Rig.NativeGuideGain > 0 ? EffectiveGuideGain : null;
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
        // Surface the wait to the GUIDE UI so Loop / Auto-select aren't silent.
        if (phase != null) SetActivity(phase, expMs);
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
        } finally {
            // The capture wait is over; the caller sets the next phase
            // ("Selecting", etc.) if there is one.
            if (phase != null) SetActivity(null);
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
            // Widen the window while the star is missing so a star that merely
            // drifted during a cloud is found again instead of being lost forever.
            return await FindStarDetailedAsync(cam, ct,
                RecoverySearchRegionFor(_starLostCount, SearchRegion, MaxRecoverySearchRegion));
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
        var img = await CaptureFullAsync(cam, ct, "Exposing");
        if (img == null) return;

        SetActivity("Selecting");
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
        SetActivity(null);
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
                int region = searchRegion ?? SearchRegion;
                var reason = CalibrationRetryReason(_lastFindStatus, _lastFindSnr, _lastFindHfd, region);
                _logger.LogWarning("Calibration capture attempt {A}/{N}: {Reason} (search +/-{Region} px around {X:F0},{Y:F0}); retrying",
                    a, attempts, reason, region, _lockX, _lockY);
                _calProgress = $"{reason}, retrying ({a}/{attempts})...";
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { }
            }
        }
        return (_lockX, _lockY, false);
    }

    /// <summary>Why a calibration capture did not yield a usable star. A null
    /// status means no frame arrived (capture budget elapsed, dropped BLOB or a
    /// capture error); any other status is the detector's verdict on a frame
    /// that DID arrive, which is a star/window problem, not a camera one.</summary>
    internal static string CalibrationRetryReason(GuideStarStatus? status, double snr, double hfd, int searchRegion) {
        return status switch {
            null => "No frame from the guide camera",
            GuideStarStatus.LowMass or GuideStarStatus.LowSnr =>
                $"Star too faint in the search window (SNR {snr:F1})",
            GuideStarStatus.HighHfd => $"Star too bloated (HFD {hfd:F1} px), out of focus or trailing",
            GuideStarStatus.LowHfd => $"Only a hot pixel found (HFD {hfd:F1} px)",
            GuideStarStatus.Error => $"Search window of {searchRegion} px left the frame",
            _ => $"Star not usable ({status})",
        };
    }

    /// <summary>How wide to search for the guide star, given how many consecutive
    /// frames have already lost it.
    ///
    /// While the star is missing the mount keeps tracking with NO corrections, so
    /// the star drifts — periodic error plus polar-alignment drift. On a typical
    /// guide scale (2-5"/px) a few minutes of cloud is easily 20-40 px. The fixed
    /// 15 px window meant that once the star drifted out of it, it was never found
    /// again even after the sky cleared and it sat there blazing 30 px away: the
    /// loop stayed in LostLock all night and only a manual stop/loop/start (which
    /// re-detects on the full frame) brought guiding back.
    ///
    /// Widen one base-width every 2 lost frames, capped. The cap is deliberate and
    /// NOT generous: GuideStar.Find returns the BRIGHTEST peak in the window, so an
    /// over-wide search will happily lock a neighbouring star, and the resulting
    /// jump in dx/dy would be applied as a correction — walking the target out of
    /// frame. Beyond the cap, re-acquisition (TryReacquireAsync) takes over, which
    /// picks by PROXIMITY instead and can afford to look further.</summary>
    internal static int RecoverySearchRegionFor(int starLostCount, int baseRegion, int maxRegion) {
        if (starLostCount <= 1) return baseRegion;
        var widened = baseRegion * (1 + starLostCount / 2);
        return Math.Min(widened, maxRegion);
    }

    /// <summary>Pick the star to re-lock onto after the star was lost: the one
    /// NEAREST the old lock, within <paramref name="maxRadius"/>, ignoring
    /// saturated stars and those too close to the frame edge.
    ///
    /// Nearest, deliberately — not brightest, which is what AutoSelectStarAsync
    /// uses when starting fresh. After a cloud-out the original star is the one
    /// that drifted a little; the brightest star in a 150 px radius may be a
    /// different star entirely, and re-locking onto it would silently re-frame the
    /// target mid-session. Nearest keeps the lock as close to the original as the
    /// sky allows, so the pointing the user set up survives.</summary>
    internal static (double X, double Y)? PickReacquireStar(
            IReadOnlyList<NINA.Image.ImageAnalysis.DetectedStar> stars,
            double lockX, double lockY, double maxRadius,
            int width, int height, int margin, double satGuard) {
        (double X, double Y)? best = null;
        double bestDist = double.MaxValue;
        foreach (var s in stars) {
            if (s.X < margin || s.Y < margin || s.X > width - margin || s.Y > height - margin) continue;
            if (satGuard > 1 && s.Peak >= satGuard * 0.95) continue;
            var dx = s.X - lockX;
            var dy = s.Y - lockY;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > maxRadius) continue;
            if (dist < bestDist) { bestDist = dist; best = (s.X, s.Y); }
        }
        return best;
    }

    /// <summary>Last-resort recovery: detect on the full frame and re-lock onto the
    /// star nearest the old lock. This is the automatic version of the stop → loop
    /// → start dance the user had to do by hand after every cloud.
    ///
    /// Re-locking MOVES the lock point, so the correction that follows won't drag
    /// the star back to where it was — the small residual drift is kept instead of
    /// fought. That's the right trade: a few px of pointing shift costs one frame's
    /// worth of alignment (the stacker handles it), whereas the alternative on the
    /// table was guiding staying dead for the rest of the night.</summary>
    private async Task TryReacquireAsync(ICamera cam, CancellationToken ct) {
        try {
            _logger.LogInformation(
                "Native guide: star lost for {N} frames; re-acquiring on the full frame", _starLostCount);
            RaiseAlert("Guide star lost — re-acquiring…");
            try { await cam.SetSubframeAsync(0, 0, 0, 0, ct); } catch { }
            var img = await CaptureFullAsync(cam, ct);
            if (img == null) return;
            int w = img.Properties.Width, h = img.Properties.Height;
            var stars = new NINA.Image.ImageAnalysis.StarDetector().Detect(img.Data, w, h);
            double satGuard = (1 << Math.Max(1, img.Properties.BitDepth)) - 1;
            var pick = PickReacquireStar(stars, _lockX, _lockY, ReacquireRadiusPx,
                                         w, h, SearchRegion + 5, satGuard);
            if (pick == null) {
                _logger.LogInformation(
                    "Native guide: re-acquire found no star within {R}px of the lock; still clouded?",
                    ReacquireRadiusPx);
                return;
            }
            var moved = Math.Sqrt(Math.Pow(pick.Value.X - _lockX, 2) + Math.Pow(pick.Value.Y - _lockY, 2));
            _lockX = pick.Value.X;
            _lockY = pick.Value.Y;
            _haveLock = true;
            _starLostCount = 0;
            if (AppState == "LostLock") SetAppState("Guiding");
            // The multi-star tracker's secondaries are relative to the old lock;
            // reseed them or it would keep voting for a field that moved.
            await BuildMultiStarAsync(ct);
            _logger.LogInformation(
                "Native guide: re-acquired at ({X:F1},{Y:F1}), {D:F1}px from the old lock",
                _lockX, _lockY, moved);
            RaiseAlert($"Guide star re-acquired ({moved:F0}px away); guiding resumed.", "info");
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            // Recovery is best-effort: a failure here must leave the loop running
            // so the widened search keeps trying on later frames.
            _logger.LogWarning(ex, "Native guide: re-acquire attempt failed");
        }
    }

    /// <summary>A slew under a running guiding session is not a lost star, it is
    /// a new field. Corrections stop while the mount moves (pulsing at a moving
    /// mount is worse than doing nothing), and when it stops we settle, pick a
    /// star on the fresh field and start a NEW guiding session: chart cleared,
    /// RMS and peaks zeroed, the algorithms' history dropped.
    ///
    /// Before this the loop just started losing the star, sat in LostLock, and
    /// eventually re-locked on whatever happened to be near the old lock
    /// position, carrying the previous target's RMS into the new one, while the
    /// badge kept reading as if nothing had happened (field report 2026-09-05).
    ///
    /// Dithering does not trip this: a dither is a pulse guide, and IsSlewing
    /// reports slews only. Returns true when the caller must skip this frame.</summary>
    private async Task<bool> HandleMountSlewAsync(ICamera cam, ITelescope? mount, CancellationToken ct) {
        if (mount == null || !mount.IsConnected) return false;

        if (mount.IsSlewing) {
            if (!_slewSeen) {
                _slewSeen = true;
                SetAppState("Slewing");
                RaiseAlert("Mount is slewing; guiding paused until it stops.", "info");
                _logger.LogInformation("Native guide: mount slew detected, corrections paused");
            }
            // Keep the view alive so the operator still sees the guide camera.
            var moving = await CaptureFullAsync(cam, ct, "Exposing");
            if (moving != null) {
                _lastFrame = moving; _lastFrameOriginX = 0; _lastFrameOriginY = 0;
                BuildView(double.NaN, double.NaN, 0, false);
            }
            return true;
        }

        if (!_slewSeen) return false;
        _slewSeen = false;

        _logger.LogInformation("Native guide: slew finished, settling {Ms} ms before re-acquiring", SlewSettleMs);
        try { await Task.Delay(SlewSettleMs, ct); } catch (OperationCanceledException) { return true; }

        // A new session: nothing measured on the old field may leak into it.
        ClearStepHistory();
        _raAlgo.Reset();
        _decAlgo.Reset();
        _backlashComp.Reset();
        _starLostCount = 0;
        _haveLock = false;
        _multiStar.Clear();

        await AutoSelectStarAsync(ct);
        if (!_haveLock) {
            SetAppState("LostLock");
            RaiseAlert("Slew finished but no guide star was found; still searching.");
            return true;
        }
        await BuildMultiStarAsync(ct);
        SetAppState("Guiding");
        RaiseAlert("Slew finished; guiding restarted on a new star.", "info");
        _logger.LogInformation("Native guide: guiding restarted at ({X:F1},{Y:F1}) after the slew", _lockX, _lockY);
        return true;
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
        if (img == null) { _lastFindStatus = null; _lastFindSnr = 0; _lastFindHfd = 0; return (_lockX, _lockY, false, 0, 0); }
        _lastFrame = img; _lastFrameOriginX = 0; _lastFrameOriginY = 0;

        int w = img.Properties.Width, h = img.Properties.Height;
        int sr = searchRegion ?? SearchRegion;
        var result = GuideStar.Find(img.Data, w, h, _lockX, _lockY, sr);
        _lastFindStatus = result.Status; _lastFindSnr = result.Snr; _lastFindHfd = result.Hfd;
        if (!result.Found) {
            return (_lockX, _lockY, false, result.Snr, result.Hfd);
        }
        return (result.X, result.Y, true, result.Snr, result.Hfd);
    }

    // ----- Internals -----

}
