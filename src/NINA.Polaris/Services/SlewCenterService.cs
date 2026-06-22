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

using System.Collections.Concurrent;
using NINA.Image.FileFormat.FITS;
using NINA.Image.Interfaces;
using NINA.Polaris.Services.PlateSolving;

namespace NINA.Polaris.Services;

/// <summary>
/// "Slew &amp; Center" orchestrator. Given a target (RA, Dec), commands
/// the mount to slew, then iteratively plate-solves test exposures
/// and nudges the mount until the actual centre is within the
/// requested tolerance (default 30 arcsec).
///
/// Long-running by nature, slews take seconds, each plate solve
/// takes 3-30s depending on solver. Exposed through a job pattern:
/// <see cref="StartJob"/> returns immediately with a job id; the
/// job's state lives in <c>_jobs</c> and is broadcast to the UI by
/// <c>StatusStreamHandler</c>. <c>AbortJob(id)</c> cancels via the
/// job's CTS.
///
/// Consumed by the SKY tab "Go to" button, the meridian-flip post-flip
/// recentre step, and the LiveStack auto-recenter trigger.
/// </summary>
public class SlewCenterService {
    private readonly EquipmentManager _equip;
    private readonly PlateSolveService _solver;
    private readonly ProfileService _profiles;
    private readonly CameraStreamService _stream;
    private readonly ActiveGuiderProvider _guiders;
    private readonly ILogger<SlewCenterService> _logger;
    private readonly NINA.Polaris.Services.PlateSolving.PlateSolveProgressService? _progress;

    private readonly ConcurrentDictionary<string, SlewCenterJob> _jobs = new();

    public SlewCenterService(EquipmentManager equip, PlateSolveService solver,
        ProfileService profiles, CameraStreamService stream,
        ILogger<SlewCenterService> logger,
        NINA.Polaris.Services.PlateSolving.PlateSolveProgressService? progress = null,
        ActiveGuiderProvider? guiders = null) {
        _equip = equip;
        _solver = solver;
        _profiles = profiles;
        _stream = stream;
        _guiders = guiders;
        _logger = logger;
        _progress = progress;
    }

    public SlewCenterJob StartJob(double ra, double dec, double toleranceArcsec = 30,
            bool skipInitialSlew = false) {
        var job = new SlewCenterJob {
            Id = Guid.NewGuid().ToString("N"),
            TargetRa = ra,
            TargetDec = dec,
            ToleranceArcsec = toleranceArcsec,
            SkipInitialSlew = skipInitialSlew,
            State = SlewCenterState.Pending,
            CreatedAt = DateTime.UtcNow
        };

        _jobs[job.Id] = job;

        job.Cts = new CancellationTokenSource();
        job.Task = Task.Run(() => RunJobAsync(job, job.Cts.Token));

        return job;
    }

    public SlewCenterJob? GetJob(string jobId) {
        return _jobs.TryGetValue(jobId, out var job) ? job : null;
    }

    public void CancelJob(string jobId) {
        if (_jobs.TryGetValue(jobId, out var job)) {
            job.Cts?.Cancel();
            job.State = SlewCenterState.Cancelled;
            // Also yank the mount itself. Just cancelling the CTS
            // unwinds the C# pipeline but leaves a SlewAsync that's
            // already in flight on the wire running to completion,
            // the user clicking Cancel almost always means STOP THE
            // SCOPE NOW, not "finish what you started, then stop
            // bothering with the plate solve". Best-effort: log and
            // swallow if the abort itself fails, the CTS path still
            // brings the orchestrator to rest.
            try { _equip.Telescope?.AbortSlewAsync(); }
            catch (Exception ex) {
                _logger.LogWarning(ex, "AbortSlew during CancelJob failed");
            }
        }
    }

    private async Task RunJobAsync(SlewCenterJob job, CancellationToken ct) {
        const int maxIterations = 5;
        // Per-rig knobs so long-FL setups don't saturate Sirius on a
        // hardcoded 5s frame and short-FL setups don't time out
        // waiting for stars at gain 0. Defaults match the previous
        // hardcoded values (5.0s / 100) so existing rigs behave
        // identically until the operator tweaks them in Manage Rigs.
        var rig = _profiles.ActiveEquipmentProfile;
        double solveExposure = rig.SlewCenterExposureSec > 0 ? rig.SlewCenterExposureSec : 5.0;
        int solveGain = rig.SlewCenterGain > 0 ? rig.SlewCenterGain : 100;

        // Live solver console for the SKY tab, same stream the STUDIO/
        // PREVIEW solves use (one solve at a time).
        _progress?.Begin("SKY (slew & center)");
        try {
            if (_equip.Telescope == null) {
                job.Error = "No telescope connected";
                job.State = SlewCenterState.Failed;
                return;
            }

            if (_equip.Camera == null) {
                job.Error = "No camera connected for plate solving";
                job.State = SlewCenterState.Failed;
                return;
            }

            // Adaptive centering goal. A flat 30" tolerance is fine on a
            // wide field but leaves the target visibly off-centre at long
            // focal length / small FOV (the LDN 43 "lands low" report). So
            // when we can derive the image scale (focal length + pixel
            // size), aim for ~12 px of residual pointing error instead --
            // tighter on long FL, naturally looser on short FL where 12 px
            // is already coarse. Never tighter than an 8" floor (below the
            // mount's repeatable pointing + seeing, chasing it just burns
            // iterations) and never looser than the requested tolerance.
            // The requested tolerance still acts as the "good enough"
            // accept threshold on the final iteration (see after the loop),
            // so a mount that can't hit the tight goal converges instead of
            // failing.
            double requestedTol = job.ToleranceArcsec;
            double goalTol = requestedTol;
            {
                double flMm = rig?.FocalLengthMm ?? 0;
                double pixUm = _equip.Camera?.PixelSizeX ?? 0;
                if (flMm > 0 && pixUm > 0) {
                    double scaleArcsecPerPx = pixUm * 206.265 / flMm;
                    double adaptive = scaleArcsecPerPx * 12.0;
                    goalTol = Math.Clamp(Math.Min(requestedTol, adaptive), 8.0, requestedTol);
                    _logger.LogInformation(
                        "Adaptive centering goal {Goal:F1}\" (scale {Scale:F2}\"/px, requested {Req:F0}\")",
                        goalTol, scaleArcsecPerPx, requestedTol);
                }
            }
            // Surface the active goal so the SKY tab status + logs reflect
            // what we're actually converging to.
            job.ToleranceArcsec = goalTol;

            // Don't bail upfront if no plate solver is available, the
            // user explicitly asked to slew, and they value the mount
            // physically moving to the target far more than they value
            // the centering pass. So perform a single Slew step first,
            // then fail with the same diagnostic IF (and only if) we
            // were about to attempt a solve.
            //
            // Surface the same multi-solver diagnostic in the failure
            // path so the user still gets actionable install / API-key
            // guidance, just AFTER the mount has moved.
            string solverUnavailableError = null;
            if (!_solver.IsAvailable) {
                var lines = _solver.AllSolvers.Select(s =>
                    "  • " + s.DisplayName + ", "
                    + (s.IsAvailable ? "ready"
                       : s is AstrometryNetOnlineSolver
                           ? "needs PlateSolve:AstrometryApiKey in appsettings"
                           : "binary not found"));
                solverUnavailableError =
                    "Slew completed. Centering skipped, no plate solver available:\n"
                    + string.Join("\n", lines)
                    + "\nTip: install a solver or use Slew Only to skip this message.";
            }

            if (solverUnavailableError != null) {
                // Slew once, then short-circuit to Failed with the
                // diagnostic, bypasses the iteration loop entirely
                // because every iteration relies on a working solver.
                job.State = SlewCenterState.Slewing;
                _logger.LogInformation("Slew-only fallback: slewing to RA={Ra:F4} Dec={Dec:F4} (no plate solver)",
                    job.TargetRa, job.TargetDec);
                try {
                    await _equip.Telescope.SlewAsync(job.TargetRa, job.TargetDec, ct);
                    await WaitForSlewComplete(ct);
                } catch (Exception slewEx) {
                    job.Error = "Slew failed: " + slewEx.Message;
                    job.State = SlewCenterState.Failed;
                    return;
                }
                job.Error = solverUnavailableError;
                job.State = SlewCenterState.Failed;
                return;
            }

            // FIELD-1: snapshot + stop the video stream around the
            // solve loop. While CCD_VIDEO_STREAM is on, IndiCamera
            // fans BLOBs to the stream subscribers and bypasses the
            // exposure TCS -- so the awaited CaptureAsync below would
            // never resolve and the solve would hit the 60 s timeout.
            // The SVBONY OSC driver hits this hardest because its
            // streamed sub-frames don't parse as full-sensor FITS
            // either way. Save the operator's settings so we can
            // restart with the same exposure / gain / binning after.
            var streamWasRunning = _stream.IsRunning;
            StreamConfig? savedStream = null;
            if (streamWasRunning) {
                savedStream = new StreamConfig(
                    ExposureSeconds: _stream.ExposureSeconds,
                    Gain: _stream.Gain,
                    BinX: _stream.BinX,
                    BinY: _stream.BinY);
                _logger.LogInformation(
                    "Pausing camera stream (exp={Exp}s gain={Gain}) for plate solve",
                    savedStream.ExposureSeconds, savedStream.Gain);
                try { await _stream.StopAsync(); }
                catch (Exception ex) {
                    _logger.LogWarning(ex, "Failed to stop video stream before solve (continuing)");
                }
            }

            // Pause guiding across the slew: the mount is about to move, so
            // the locked guide star will leave the frame. Stop now; we
            // re-acquire a NEW star and restart guiding only after a
            // successful centre (see the finally block).
            var guider = _guiders.Active;
            bool guiderWasGuiding = false;
            try {
                if (guider != null && guider.IsConnected && guider.IsGuiding) {
                    guiderWasGuiding = true;
                    _logger.LogInformation("Pausing guiding for slew & center");
                    await guider.StopAsync(ct);
                }
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Failed to stop guiding before slew (continuing)");
            }

            try {

            for (int i = 0; i < maxIterations; i++) {
                ct.ThrowIfCancellationRequested();
                job.Iteration = i + 1;

                // Step 1: Slew. "Center only" skips the initial slew
                // on the first iteration: the scope is assumed to be
                // already pointing at (roughly) the field, so we go
                // straight to capture + solve + sync and let the
                // convergence loop nudge it in. Later iterations still
                // slew to apply the corrected coordinates.
                if (i == 0 && job.SkipInitialSlew) {
                    _logger.LogInformation(
                        "Center-only: skipping initial slew, refining current pointing");
                } else {
                    job.State = SlewCenterState.Slewing;
                    _logger.LogInformation("Slew-and-center iteration {I}: slewing to RA={Ra:F4} Dec={Dec:F4}",
                        i + 1, job.TargetRa, job.TargetDec);

                    await _equip.Telescope.SlewAsync(job.TargetRa, job.TargetDec, ct);
                    await WaitForSlewComplete(ct);
                }

                ct.ThrowIfCancellationRequested();

                // Step 2: Capture short exposure for plate solving.
                // Pass gain via CaptureOptions so vendor cameras (Canon,
                // Nikon, ASCOM) that don't honour a bare CaptureAsync
                // gain still get the right ISO/gain for the solve.
                job.State = SlewCenterState.Capturing;
                _logger.LogInformation(
                    "Capturing {Exp}s solve frame at gain {Gain}", solveExposure, solveGain);

                // Force full resolution (bin 1x1) for the solve frame regardless
                // of the current preview/live binning. A binned solve (e.g. the
                // camera left at bin3 by a preview => 1280x720 on a 4K sensor)
                // loses stars and the scale ASTAP/astrometry rely on, which made
                // SKY slew-and-solve fail intermittently. All camera backends
                // honour CaptureOptions.BinX/Y per-capture, so the next preview/
                // live capture restores the user's binning on its own.
                var imageData = await _equip.Camera.CaptureAsync(
                    solveExposure,
                    new NINA.Image.Interfaces.CaptureOptions(Gain: solveGain, BinX: 1, BinY: 1, ImageType: "SOLVE"),
                    ct);

                var tempFits = Path.Combine(Path.GetTempPath(),
                    $"nina_solve_{job.Id}_{i}.fits");

                FITSWriter.Write(imageData, tempFits);

                ct.ThrowIfCancellationRequested();

                // Step 3: Plate solve
                job.State = SlewCenterState.Solving;
                _logger.LogInformation("Plate solving...");
                _progress?.Append($"-- iteration {i + 1}/{maxIterations}: plate solving --");

                // Position hint: prefer the mount's ACTUAL current pointing over
                // the slew target. They coincide right after a normal first slew,
                // but in center-only / post-flip / KeepCentered cases the mount can
                // be pointing well off the desired target coordinates, so hinting
                // ASTAP with the target would steer it to the wrong field. Fall back
                // to the target when the mount doesn't report a usable RA/Dec.
                double hintRa = job.TargetRa, hintDec = job.TargetDec;
                var scope = _equip.Telescope;
                if (scope != null && scope.IsConnected) {
                    var mra = scope.RightAscension;
                    var mdec = scope.Declination;
                    if (!double.IsNaN(mra) && !double.IsNaN(mdec) &&
                            mra >= 0 && mra <= 24 && mdec >= -90 && mdec <= 90) {
                        hintRa = mra;
                        hintDec = mdec;
                    }
                }

                // FOV hint: ASTAP without a scale hint has to search its whole
                // range, which is the main cause of slow/failed solves. ASTAP's
                // -fov is the field *height* (vertical), so derive it from the
                // image HEIGHT + Y pixel size — using the width here over-states
                // the FOV on any non-square sensor and makes the hinted solve
                // fail at the wrong image scale (N.I.N.A. desktop passes FoVH too).
                double fovDeg = 0;
                double fl = _profiles.ActiveEquipmentProfile?.FocalLengthMm ?? 0;
                double pixSize = _equip.Camera?.PixelSizeY ?? 0;
                if (pixSize <= 0) pixSize = _equip.Camera?.PixelSizeX ?? 0;
                int imgHeight = imageData.Properties.Height;
                if (fl > 0 && pixSize > 0 && imgHeight > 0) {
                    double sensorMm = pixSize * imgHeight / 1000.0;
                    fovDeg = 2.0 * Math.Atan(sensorMm / (2.0 * fl)) * (180.0 / Math.PI);
                }

                _logger.LogInformation(
                    "Solve hints: RA={Ra:F4}h Dec={Dec:F4}° fov={Fov:F2}° radius=10°",
                    hintRa, hintDec, fovDeg);

                var solveResult = await _solver.SolveAsync(tempFits, new PlateSolveOptions {
                    HintRa = hintRa,
                    HintDec = hintDec,
                    FovDeg = fovDeg,
                    SearchRadiusDeg = 10
                }, ct, _progress != null ? _progress.Append : null);

                try { File.Delete(tempFits); } catch { }

                if (!solveResult.Success) {
                    _logger.LogWarning("Plate solve failed on iteration {I}: {Error}",
                        i + 1, solveResult.Error);
                    job.Error = "Solve failed: " + solveResult.Error;

                    if (i == maxIterations - 1) {
                        job.State = SlewCenterState.Failed;
                        return;
                    }
                    continue;
                }

                // Step 4: Calculate error
                var errorArcsec = AngularSeparationArcsec(
                    job.TargetRa, job.TargetDec,
                    solveResult.RaHours, solveResult.DecDeg);

                job.ActualRa = solveResult.RaHours;
                job.ActualDec = solveResult.DecDeg;
                job.ErrorArcsec = errorArcsec;
                job.Rotation = solveResult.RotationDeg;
                job.Scale = solveResult.ScaleArcsecPerPixel;

                _logger.LogInformation(
                    "Solve result: RA={Ra:F4}h Dec={Dec:F4}°, error={Err:F1}\" (tolerance={Tol:F0}\")",
                    solveResult.RaHours, solveResult.DecDeg, errorArcsec, job.ToleranceArcsec);

                // Auto-update the active rig's focal length from the solve.
                // Only runs once per job (on the first successful solve we have
                // a reliable scale) and skipped if the camera doesn't report a
                // pixel size or the derived value is wildly different (>50%
                // off, likely a misidentification of the field).
                if (job.DerivedFocalLengthMm == null) {
                    TryUpdateFocalLengthFromSolve(solveResult.ScaleArcsecPerPixel, job);
                }

                // Step 5: Check convergence
                if (errorArcsec <= job.ToleranceArcsec) {
                    job.State = SlewCenterState.Centered;
                    _logger.LogInformation("Centered! Error {Err:F1}\" within tolerance {Tol:F0}\"",
                        errorArcsec, job.ToleranceArcsec);
                    return;
                }

                // Step 6: Sync mount and prepare for next iteration
                job.State = SlewCenterState.Syncing;
                _logger.LogInformation("Syncing mount at RA={Ra:F4} Dec={Dec:F4}",
                    solveResult.RaHours, solveResult.DecDeg);

                await _equip.Telescope.SyncAsync(solveResult.RaHours, solveResult.DecDeg, ct);

                // FIELD4-1: post-sync settle. The driver-level SyncAsync
                // returns when the EQUATORIAL_EOD write is ack'd, but
                // the mount's internal coordinate system needs a beat
                // (typically 200-500 ms) to adopt the new zero. Without
                // this delay the next iteration's slew computes its
                // motion vector from STALE mount RA/Dec, producing
                // backwards / perpendicular nudges close to convergence
                // (the "erratic near target" symptom) and stretching
                // the loop into a final iteration that bumps into the
                // camera CaptureAsync timeout. 800 ms covers every
                // mainstream mount we've tested with margin.
                await Task.Delay(800, ct);
            }

            // Didn't reach the (possibly tightened) goal within the
            // iteration budget. Accept anyway if we're inside the
            // originally requested tolerance -- the adaptive goal is an
            // aspiration, the requested tolerance is the contract, and a
            // mount that lands at e.g. 18" against an 11" goal but a 30"
            // request is centred for the user's purposes. Only fail when
            // we're outside even the requested tolerance.
            if (job.ErrorArcsec > 0 && job.ErrorArcsec <= requestedTol) {
                job.State = SlewCenterState.Centered;
                _logger.LogInformation(
                    "Accepted at {Err:F1}\" (within requested {Req:F0}\", goal was {Goal:F1}\")",
                    job.ErrorArcsec, requestedTol, goalTol);
                return;
            }

            job.State = SlewCenterState.Failed;
            job.Error = $"Did not converge after {maxIterations} iterations (last error: {job.ErrorArcsec:F1}\")";

            } finally {
                // FIELD-1: restart the stream with the operator's saved
                // settings so the PREVIEW / VIDEO canvas resumes after
                // the solve (success, fail, or convergence). Wrapped in
                // try/catch so a stream-restart failure doesn't mask a
                // legitimate solve result the caller is waiting on.
                if (savedStream != null) {
                    try {
                        _stream.Start(savedStream);
                        _logger.LogInformation("Resumed camera stream after plate solve");
                    } catch (Exception ex) {
                        _logger.LogWarning(ex,
                            "Failed to resume video stream after solve (operator can restart manually)");
                    }
                }

                // Resume guiding only on a successful centre. The old guide
                // star is out of frame after the slew, so re-acquire a new
                // one (AutoSelectStar) before restarting. On failure/cancel
                // we leave guiding off rather than guide on the wrong field.
                if (guiderWasGuiding && job.State == SlewCenterState.Centered) {
                    try {
                        _logger.LogInformation("Resuming guiding: re-selecting guide star");
                        await guider.AutoSelectStarAsync(ct);
                        await guider.StartGuidingAsync(ct: ct);
                    } catch (Exception ex) {
                        _logger.LogWarning(ex,
                            "Failed to resume guiding after slew & center (re-select + start guiding manually)");
                    }
                }
            }

        } catch (OperationCanceledException) {
            job.State = SlewCenterState.Cancelled;
            _logger.LogInformation("Slew-and-center cancelled");
        } catch (Exception ex) {
            job.State = SlewCenterState.Failed;
            job.Error = ex.Message;
            _logger.LogError(ex, "Slew-and-center failed");
        } finally {
            _progress?.End();
        }
    }

    private async Task WaitForSlewComplete(CancellationToken ct) {
        if (_equip.Telescope == null) return;
        for (int i = 0; i < 300; i++) {
            ct.ThrowIfCancellationRequested();
            if (!_equip.Telescope.IsSlewing) return;
            await Task.Delay(1000, ct);
        }
        _logger.LogWarning("Slew did not complete within 5 minutes");
    }

    /// <summary>
    /// Compute focal length from the plate-solve scale + camera pixel size and
    /// push it into the active rig. Skipped silently when:
    /// - no camera connected / camera doesn't report pixel size
    /// - scale is non-positive
    /// - derived value is &gt;50% off from the current rig value (likely a
    ///   misidentification, don't clobber the user's setting on bad data)
    ///
    /// Formula (standard plate-scale relation):
    ///   scale (arcsec/px) = pixel_size (um) / focal_length (mm) × 206.265
    ///   →  focal_length (mm) = pixel_size (um) × 206.265 / scale (arcsec/px)
    /// </summary>
    private void TryUpdateFocalLengthFromSolve(double scaleArcsecPerPx, SlewCenterJob job) {
        if (scaleArcsecPerPx <= 0) return;
        if (_equip.Camera == null) return;

        var pixelSizeUm = _equip.Camera.PixelSizeX;
        if (pixelSizeUm <= 0 || double.IsNaN(pixelSizeUm)) {
            _logger.LogDebug("Camera does not report PixelSizeX, skipping focal-length auto-update");
            return;
        }

        var derived = pixelSizeUm * 206.265 / scaleArcsecPerPx;
        job.DerivedFocalLengthMm = derived;

        var rig = _profiles.ActiveEquipmentProfile;
        var previous = rig.FocalLengthMm;

        // Sanity check: refuse if more than 50% different
        if (previous > 0) {
            var ratio = derived / previous;
            if (ratio < 0.5 || ratio > 1.5) {
                _logger.LogWarning(
                    "Plate solve suggests focal length {New:F0}mm but rig has {Old:F0}mm " +
                    "(ratio {Ratio:F2}), refusing to auto-update; please verify manually",
                    derived, previous, ratio);
                return;
            }
        }

        if (Math.Abs(derived - previous) < 1.0) {
            _logger.LogDebug("Focal length already accurate ({FL:F0}mm), no update", derived);
            return;
        }

        _profiles.UpdateEquipmentProfile(rig.Id, r => r.FocalLengthMm = derived);
        _logger.LogInformation(
            "Auto-updated active rig '{Rig}' focal length: {Old:F0}mm → {New:F0}mm " +
            "(from solve: {Px:F2}um/px × 206.265 / {Scale:F2}\"/px)",
            rig.Name, previous, derived, pixelSizeUm, scaleArcsecPerPx);
    }


    private static double AngularSeparationArcsec(double ra1Hours, double dec1Deg,
        double ra2Hours, double dec2Deg) {
        var ra1Rad = ra1Hours * 15.0 * Math.PI / 180.0;
        var dec1Rad = dec1Deg * Math.PI / 180.0;
        var ra2Rad = ra2Hours * 15.0 * Math.PI / 180.0;
        var dec2Rad = dec2Deg * Math.PI / 180.0;

        var cosSep = Math.Sin(dec1Rad) * Math.Sin(dec2Rad) +
                     Math.Cos(dec1Rad) * Math.Cos(dec2Rad) * Math.Cos(ra1Rad - ra2Rad);

        cosSep = Math.Clamp(cosSep, -1.0, 1.0);
        var sepRad = Math.Acos(cosSep);

        return sepRad * 180.0 / Math.PI * 3600.0;
    }
}

public class SlewCenterJob {
    public string Id { get; set; } = "";
    public double TargetRa { get; set; }
    public double TargetDec { get; set; }
    public double ToleranceArcsec { get; set; }
    /// <summary>Center-only mode: skip the initial slew on the first
    /// iteration (the scope is already on the field; just refine).</summary>
    public bool SkipInitialSlew { get; set; }
    public SlewCenterState State { get; set; }
    public int Iteration { get; set; }
    public double? ActualRa { get; set; }
    public double? ActualDec { get; set; }
    public double? ErrorArcsec { get; set; }
    public double? Rotation { get; set; }
    public double? Scale { get; set; }
    /// <summary>Focal length (mm) derived from the first successful solve in this job, if any.</summary>
    public double? DerivedFocalLengthMm { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }

    internal CancellationTokenSource? Cts { get; set; }
    internal Task? Task { get; set; }
}

public enum SlewCenterState {
    Pending,
    Slewing,
    Capturing,
    Solving,
    Syncing,
    Centered,
    Failed,
    Cancelled
}