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

namespace NINA.Polaris.Services;

/// <summary>
/// TPPA (Three-Point Polar Alignment) orchestrator. Multi-phase state
/// machine that mirrors PHD2CalibrationOrchestrator in shape:
///   - StartJob spins a Task.Run(RunAsync) with a CancellationTokenSource
///   - Job state is broadcast via JobUpdated → StatusStreamHandler folds
///     into /ws/status under polarAlignment
///   - Abort cancels the CTS, RunAsync's finally lands at Phase.Cancelled
///
/// PA-1 lays the skeleton (enum + records + stubs). PA-2 fills in the
/// capture/slew/solve loop. PA-3 plugs in the polar-axis math. PA-5
/// adds the continuous Refinement mode (sliding-window solve loop
/// while the user adjusts knobs).
///
/// Refinement uses a separate CTS so the user can Stop refinement
/// without affecting any in-progress TPPA job (in practice TPPA must
/// complete before Refine becomes available, but the lifecycle plumbing
/// is independent in case we want to allow re-running TPPA from a
/// refinement state later).
/// </summary>
public class PolarAlignmentService {
    // Serialize polar-alignment captures against every other main-camera
    // capture so a stray LIVE/preview frame can't race the native driver.
    private static Task<IImageData> GatedCapture(
            ICamera camera, double exp, CaptureOptions opts, CancellationToken ct)
        => CameraCaptureGate.RunAsync(() => camera.CaptureAsync(exp, opts, ct), ct);

    private readonly EquipmentManager _equip;
    private readonly PlateSolveService _plateSolve;
    private readonly ProfileService _profiles;
    private readonly NotificationService _notify;
    private readonly ILogger<PolarAlignmentService> _logger;

    private readonly ConcurrentDictionary<string, PolarAlignmentJob> _jobs = new();

    /// <summary>Most recent job, Idle when nothing has run yet. The WS
    /// broadcaster reads this. Set to a fresh job by StartJob; mutated
    /// in-place by RunAsync; preserved post-completion so the UI can
    /// keep showing the last computed error vector.</summary>
    public PolarAlignmentJob? CurrentJob { get; private set; }

    /// <summary>Fires on every phase transition + every new solved
    /// point. StatusStreamHandler subscribes so it can push an
    /// immediate WS frame instead of waiting for the next 1Hz tick.</summary>
    public event Action<PolarAlignmentJob>? JobUpdated;

    public PolarAlignmentService(EquipmentManager equip,
                                 PlateSolveService plateSolve,
                                 ProfileService profiles,
                                 NotificationService notify,
                                 ILogger<PolarAlignmentService> logger) {
        _equip = equip;
        _plateSolve = plateSolve;
        _profiles = profiles;
        _notify = notify;
        _logger = logger;
    }

    public PolarAlignmentJob StartJob(PolarAlignmentOptions opts) {
        // Refuse to start a second TPPA on top of a running one, the
        // mount can't be in two places at once. Refinement is gated
        // separately (see StartRefinement).
        if (CurrentJob != null && CurrentJob.IsActive) {
            throw new InvalidOperationException(
                "A polar-alignment job is already in progress. Abort it first.");
        }

        var job = new PolarAlignmentJob {
            Id = Guid.NewGuid().ToString("N"),
            Options = opts,
            Phase = PolarAlignmentPhase.Preflight,
            Mode = "tppa",
            StartedAt = DateTime.UtcNow
        };
        _jobs[job.Id] = job;
        JobRetention.TrimFinished(_jobs, j => j.StartedAt,
            j => j.Phase is PolarAlignmentPhase.Ok or PolarAlignmentPhase.Failed
                          or PolarAlignmentPhase.Cancelled);
        CurrentJob = job;
        job.Cts = new CancellationTokenSource();
        job.Task = Task.Run(() => RunAsync(job, job.Cts.Token));
        return job;
    }

    public PolarAlignmentJob? GetJob(string id) =>
        _jobs.TryGetValue(id, out var j) ? j : null;

    public void Abort(string id) {
        if (_jobs.TryGetValue(id, out var j)) {
            j.Cts?.Cancel();
        }
    }

    /// <summary>Cancel whatever job is currently active (TPPA or
    /// refinement). Convenience for the UI "Stop everything" button.</summary>
    public void AbortCurrent() {
        var j = CurrentJob;
        if (j != null && j.IsActive) {
            j.Cts?.Cancel();
        }
    }

    /// <summary>Kick off a continuous capture+solve refinement loop.
    /// Requires a completed TPPA job in CurrentJob (so we have a
    /// baseline of 3 solved points). Each iteration captures, plate-
    /// solves, evicts the oldest of the 3 baseline points, appends the
    /// fresh one, and recomputes (azErr, altErr). The error decreases
    /// as the user adjusts the mount knobs. Loop continues until
    /// StopRefinement() or external cancellation.</summary>
    public PolarAlignmentJob StartRefinement() {
        var baseline = CurrentJob;
        if (baseline == null || baseline.Phase != PolarAlignmentPhase.Ok || baseline.Points.Count < 3) {
            throw new InvalidOperationException(
                "Run TPPA first to establish a 3-point baseline before refining.");
        }
        if (_refineCts != null && !_refineCts.IsCancellationRequested) {
            throw new InvalidOperationException("Refinement already running.");
        }

        // Reuse the same Job object, Mode flips to "refine", Phase
        // becomes Refining, but the 3 baseline Points carry over so
        // we can substitute them as new samples arrive. UI gets the
        // continuity for free (same WS payload shape).
        baseline.Mode = "refine";
        baseline.Phase = PolarAlignmentPhase.Refining;
        baseline.CompletedAt = null;
        try { JobUpdated?.Invoke(baseline); } catch { }

        _refineCts = new CancellationTokenSource();
        _ = Task.Run(() => RunRefinementAsync(baseline, _refineCts.Token));
        return baseline;
    }

    public void StopRefinement() {
        _refineCts?.Cancel();
        _refineCts = null;
        // Land the job back at Ok so the UI's "completed" indicator
        // stays lit. The last-computed error vector is preserved.
        if (CurrentJob != null && CurrentJob.Phase == PolarAlignmentPhase.Refining) {
            CurrentJob.Mode = "tppa";
            CurrentJob.Phase = PolarAlignmentPhase.Ok;
            CurrentJob.CompletedAt = DateTime.UtcNow;
            try { JobUpdated?.Invoke(CurrentJob); } catch { }
        }
    }

    /// <summary>True while the continuous refine LOOP is running (as
    /// opposed to a single-shot <see cref="RefineOnceAsync"/> pass).
    /// Surfaced on the WS payload so the UI's Auto toggle reflects the
    /// real server state even across clients/reconnects.</summary>
    public bool RefineLoopActive => _refineCts != null && !_refineCts.IsCancellationRequested;

    /// <summary>ASIAIR-style MANUAL refresh: one capture → solve →
    /// sliding-window update → error recompute, then back to Ok. The
    /// operator adjusts a knob, taps Refresh, reads the new error —
    /// no continuous loop hammering the camera in between. Returns
    /// false (with the reason in <c>CurrentJob.LastError</c>) when the
    /// single pass couldn't produce a solve; the previous error vector
    /// is preserved either way.</summary>
    public async Task<bool> RefineOnceAsync(CancellationToken ct = default) {
        var job = CurrentJob;
        if (job == null || job.Phase != PolarAlignmentPhase.Ok || job.Points.Count < 3) {
            throw new InvalidOperationException(
                "Run TPPA first to establish a 3-point baseline before refreshing.");
        }
        if (RefineLoopActive) {
            throw new InvalidOperationException(
                "Auto-refine is running; stop it before manual refresh.");
        }
        if (Interlocked.CompareExchange(ref _refineOnceBusy, 1, 0) != 0) {
            throw new InvalidOperationException("A manual refresh is already in progress.");
        }
        try {
            job.Mode = "refine";
            job.Phase = PolarAlignmentPhase.Refining;
            job.CompletedAt = null;
            try { JobUpdated?.Invoke(job); } catch { }

            var camera = _equip.Camera;
            var telescope = _equip.Telescope;
            if (camera == null || telescope == null) {
                job.LastError = "Refresh: camera or telescope disconnected.";
                return false;
            }
            return await RefineStepAsync(job, camera, telescope, _profiles.Active, ct);
        } finally {
            job.Mode = "tppa";
            job.Phase = PolarAlignmentPhase.Ok;
            job.CompletedAt = DateTime.UtcNow;
            try { JobUpdated?.Invoke(job); } catch { }
            Interlocked.Exchange(ref _refineOnceBusy, 0);
        }
    }

    private int _refineOnceBusy;
    private CancellationTokenSource? _refineCts;

    private async Task RunRefinementAsync(PolarAlignmentJob job, CancellationToken ct) {
        var camera = _equip.Camera;
        var telescope = _equip.Telescope;
        if (camera == null || telescope == null) {
            _notify.Push("error", "Refine: camera or telescope disconnected.");
            return;
        }

        var userProfile = _profiles.Active;
        try {
            while (!ct.IsCancellationRequested) {
                // One capture → solve → window → error pass (shared with the
                // manual Refresh path). Failures are non-fatal: the step logs
                // + records LastError, we settle and try again.
                try {
                    await RefineStepAsync(job, camera, telescope, userProfile, ct);
                } catch (OperationCanceledException) { break; }

                // Push update + settle before next iteration.
                try { JobUpdated?.Invoke(job); } catch { }
                try { await Task.Delay(Math.Max(500, job.Options.SettleSeconds * 1000), ct); }
                catch { break; }
            }
        } catch (Exception ex) {
            _logger.LogError(ex, "Refine loop crashed");
            _notify.Push("error", "Refine loop crashed: " + ex.Message);
        }
        // Loop exit: land back at Ok so the Refine button is offered
        // again. (StopRefinement also handles this, this is the
        // fallback for organic loop exits.)
        if (job.Phase == PolarAlignmentPhase.Refining) {
            job.Phase = PolarAlignmentPhase.Ok;
            job.Mode = "tppa";
            job.CompletedAt = DateTime.UtcNow;
            try { JobUpdated?.Invoke(job); } catch { }
        }
    }

    /// <summary>One refine pass: capture at the CURRENT mount position
    /// (no slew) → plate solve → precess J2000→date → sliding-window the
    /// 3 baseline points → recompute (azErr, altErr) when the window
    /// still spans a usable arc. Shared by the continuous loop and the
    /// manual single-shot Refresh. Returns false with the reason in
    /// <c>job.LastError</c> when the pass produced no usable solve;
    /// OperationCanceledException propagates to the caller.</summary>
    private async Task<bool> RefineStepAsync(PolarAlignmentJob job,
            NINA.Image.Interfaces.ICamera camera,
            NINA.Image.Interfaces.ITelescope telescope,
            UserProfile userProfile, CancellationToken ct) {
        // 1. Capture at the CURRENT mount position (no slew).
        IImageData image;
        try {
            image = await GatedCapture(camera,
                job.Options.ExposureSeconds,
                new CaptureOptions(Gain: job.Options.Gain, ImageType: "POLAR"),
                ct);
        } catch (OperationCanceledException) { throw; }
        catch (Exception ex) {
            _logger.LogWarning(ex, "Refine: capture failed");
            job.LastError = "Refine: capture failed — " + ex.Message;
            return false;
        }
        if (image == null || image.Properties.Width <= 0) {
            job.LastError = "Refine: camera returned an empty frame.";
            return false;
        }

        // 2. Plate solve.
        PlateSolveResult solve;
        try {
            solve = await SolveOnceAsync(image, telescope, ct);
        } catch (OperationCanceledException) { throw; }
        catch (Exception ex) {
            _logger.LogDebug(ex, "Refine: solve threw");
            job.LastError = "Refine: plate solve failed — " + ex.Message;
            return false;
        }
        if (!solve.Success) {
            _logger.LogDebug("Refine: solve failed ({Err})", solve.Error);
            job.LastError = "Refine: plate solve failed" +
                (string.IsNullOrEmpty(solve.Error) ? "." : " — " + solve.Error);
            return false;
        }

        // 3. POLARUI2: displacement-to-target refinement. Precess the
        //    solve → of-date, then measure the rotation still needed to
        //    take the current pointing to the ANCHORED target — the
        //    RA/Dec the pointing will have once the knobs are fully
        //    corrected. One solve per refresh; no 3-point re-fit, so
        //    there is no sliding-window degeneracy at a fixed pointing
        //    (the old approach froze the error the moment the window
        //    collapsed, which in the field read as "Refresh does
        //    nothing").
        var nowUtc = DateTime.UtcNow;
        var (raNow, decNow) = PolarAlignmentMath.PrecessJ2000ToDate(
            solve.RaHours, solve.DecDeg, nowUtc);

        if (job.RefineTargetRaHours == null || job.RefineTargetDecDeg == null) {
            // First refine solve after TPPA: knobs untouched yet, so
            // target = current pointing rotated by the full correction.
            var (tRa, tDec) = PolarAlignmentMath.ComputeRefineTarget(
                raNow, decNow, nowUtc,
                job.AzErrorArcsec, job.AltErrorArcsec,
                userProfile.Latitude, userProfile.Longitude);
            job.RefineTargetRaHours = tRa;
            job.RefineTargetDecDeg = tDec;
            _logger.LogInformation(
                "Refine anchor set: pointing {Ra}h/{Dec}° → target {TRa}h/{TDec}°",
                raNow.ToString("F4"), decNow.ToString("F4"),
                tRa.ToString("F4"), tDec.ToString("F4"));
        }

        // Guard: a slew (or a bad solve) moved the pointing far away
        // from the anchor — the knob decomposition would be garbage.
        double sepDeg = PolarAlignmentMath.AngularSeparationDeg(
            raNow, decNow, job.RefineTargetRaHours.Value, job.RefineTargetDecDeg.Value);
        if (sepDeg > 20.0) {
            job.LastError =
                "Refine: the mount moved too far from the refinement anchor " +
                "(did it slew?). Re-run the 3-point sweep to re-measure.";
            return false;
        }

        var remaining = PolarAlignmentMath.ComputeRefineError(
            job.RefineTargetRaHours.Value, job.RefineTargetDecDeg.Value,
            raNow, decNow, nowUtc,
            userProfile.Latitude, userProfile.Longitude);
        if (remaining == null) {
            job.LastError =
                "Refine: pointing is too close to the zenith or the due " +
                "east/west horizon for a stable alt/az decomposition — " +
                "re-run TPPA from a different part of the sky.";
            return false;
        }

        var (azErr, altErr) = remaining.Value;
        job.AzErrorArcsec = azErr;
        job.AltErrorArcsec = altErr;
        job.TotalErrorArcsec = PolarAlignmentMath.TotalErrorArcsec(azErr, altErr);
        job.LastError = null;
        return true;
    }

    private async Task RunAsync(PolarAlignmentJob job, CancellationToken ct) {
        // Track original mount position so we can slew back at the
        // end (cosmetic, TPPA already extracted the error vector by
        // then, but leaving the user 60° off where they expected is
        // surprising).
        double ra0 = 0, dec0 = 0;
        var mountDecReadings = new List<double>(3);

        try {
            // 1. Preflight ----------------------------------------------------
            SetPhase(job, PolarAlignmentPhase.Preflight);
            _notify.Push("info", "Polar alignment starting…", 2500);

            var telescope = _equip.Telescope;
            var camera = _equip.Camera;
            if (telescope == null || !telescope.IsConnected) {
                Fail(job, "Telescope not connected.");
                return;
            }
            if (camera == null || !camera.IsConnected) {
                Fail(job, "Camera not connected.");
                return;
            }
            if (!telescope.IsTracking) {
                Fail(job, "Telescope must be tracking (sidereal) for TPPA. Enable tracking and retry.");
                return;
            }
            if (telescope.IsParked) {
                Fail(job, "Telescope is parked. Unpark before running polar alignment.");
                return;
            }

            ra0 = telescope.RightAscension;
            dec0 = telescope.Declination;

            // 2. Three solved points -----------------------------------------
            // Slew step measured in degrees; mount RA is in hours, so
            // convert via /15.
            double slewStepHours = job.Options.SlewStepDegrees / 15.0;

            // Meridian-aware sweep direction: always slew AWAY from the
            // meridian. A fixed +RA sweep could cross it mid-routine on a
            // GEM, triggering a pier flip between points — cone error flips
            // sign across the pier, which shifts the small-circle centre and
            // silently corrupts the 3-point fit (and some mounts, e.g. the
            // ZWO AM series over LX200, reject near-limit GoTos outright).
            // Hour angle of the start position decides: pointing EAST of the
            // meridian (HA <= 0), +RA moves further east; pointing WEST
            // (HA > 0), -RA moves further west.
            {
                var siteProfile = _profiles.Active;
                double lst = PolarAlignmentMath.LocalSiderealHours(
                    DateTime.UtcNow, siteProfile.Longitude);
                double haHours = lst - ra0;                   // wrap to (-12, +12]
                haHours = ((haHours + 12.0) % 24.0 + 24.0) % 24.0 - 12.0;
                if (haHours > 0) slewStepHours = -slewStepHours;
                _logger.LogInformation(
                    "Polar align: start HA {HA:F2}h -> sweeping {Dir} in RA ({Step:F1}deg steps)",
                    haHours, slewStepHours >= 0 ? "east (+RA)" : "west (-RA)",
                    job.Options.SlewStepDegrees);
            }

            for (int i = 0; i < 3; i++) {
                ct.ThrowIfCancellationRequested();

                double targetRa = NormalizeRaHours(ra0 + i * slewStepHours);

                // Slew. For i=0 this is usually a no-op (~0.01h drift
                // during the brief preflight) but we still call it to
                // make sure we know our exact pointing.
                SetPhase(job, MovingPhaseFor(i));
                _notify.Push("info", $"Polar align: slewing to point {i + 1}/3 (RA {targetRa:F3}h)", 2000);
                await telescope.SlewAsync(targetRa, dec0, ct);

                // CRITICAL: SlewAsync only waits for the driver to ACK the
                // coord write (slew accepted/started), NOT for the mount to
                // arrive. Without blocking on IsSlewing the capture below
                // fires mid-slew — the frame is trailed and the plate solve
                // fails or solves a position that isn't the intended RA,
                // which silently corrupts the 3-point fit. Wait for arrival,
                // THEN settle.
                await WaitForSlewCompleteAsync(telescope, ct);

                // Settle, mount stops shaking, INDI driver finishes
                // emitting EQUATORIAL_EOD_COORD updates.
                if (job.Options.SettleSeconds > 0) {
                    await Task.Delay(job.Options.SettleSeconds * 1000, ct);
                }

                // Capture + plate-solve.
                SetPhase(job, SolvingPhaseFor(i));
                var image = await GatedCapture(camera, 
                    job.Options.ExposureSeconds,
                    new CaptureOptions(Gain: job.Options.Gain, ImageType: "POLAR"),
                    ct);
                if (image == null || image.Properties.Width <= 0 || image.Properties.Height <= 0) {
                    Fail(job, $"Point {i + 1}: camera returned an empty frame.");
                    return;
                }

                var result = await SolveOnceAsync(image, telescope, ct);
                if (!result.Success) {
                    // One retry with doubled exposure, common rescue
                    // for marginal star count on the first attempt.
                    _logger.LogInformation(
                        "Polar align point {Index} first solve failed ({Err}); retrying with 2x exposure",
                        i + 1, result.Error);
                    var retryImage = await GatedCapture(camera, 
                        job.Options.ExposureSeconds * 2.0,
                        new CaptureOptions(Gain: job.Options.Gain, ImageType: "POLAR"),
                        ct);
                    if (retryImage != null && retryImage.Properties.Width > 0) {
                        result = await SolveOnceAsync(retryImage, telescope, ct);
                    }
                }
                if (!result.Success) {
                    Fail(job, $"Plate solve failed at point {i + 1}: {result.Error}. " +
                              $"Try increasing exposure or gain in rig settings.");
                    return;
                }

                // ASTAP solves in J2000; the polar-axis fit works in the
                // of-date Alt/Az frame (LST-based), so precess the solved
                // coords to the equinox of date before storing the point —
                // otherwise the whole 3-point set is biased by ~0.35° of
                // accumulated precession and the reported polar error is
                // systematically wrong.
                var ptUtc = DateTime.UtcNow;
                var (raDate, decDate) = PolarAlignmentMath.PrecessJ2000ToDate(
                    result.RaHours, result.DecDeg, ptUtc);
                job.Points.Add(new PolarPoint(
                    Index: i,
                    RaHours: raDate,
                    DecDeg: decDate,
                    RotationDeg: result.RotationDeg,
                    AtUtc: ptUtc));

                // POLARUI2c field diagnostics. The 3-point fit is only
                // valid if the mount rotated PURELY about its RA axis
                // between points — a firmware pointing model that re-aims
                // the DEC axis on each GoTo silently breaks that and the
                // fitted "axis error" becomes garbage. The mount's own
                // reported coordinates expose it: its Dec reading must sit
                // at dec0 for all three points. Log both frames per point
                // so a bad session can be diagnosed from the LOG panel.
                double mntRa = double.NaN, mntDec = double.NaN;
                try { mntRa = telescope.RightAscension; mntDec = telescope.Declination; } catch { }
                mountDecReadings.Add(mntDec);
                _logger.LogInformation(
                    "Polar align point {N}: mount reports RA {MRa}h Dec {MDec}° (commanded RA {CRa}h Dec {CDec}°); solved RA {SRa}h Dec {SDec}° (of-date)",
                    i + 1,
                    mntRa.ToString("F4"), mntDec.ToString("F4"),
                    targetRa.ToString("F4"), dec0.ToString("F4"),
                    raDate.ToString("F4"), decDate.ToString("F4"));

                // Force a WS push so the UI's "Point N of 3 solved"
                // ticker updates immediately instead of waiting for the
                // next 1Hz tick.
                try { JobUpdated?.Invoke(job); } catch { }
            }

            // 3. Compute polar error ----------------------------------------
            SetPhase(job, PolarAlignmentPhase.Computing);
            var userProfile = _profiles.Active;
            if (userProfile.Latitude == 0 && userProfile.Longitude == 0) {
                Fail(job, "Site latitude/longitude not set. Configure your location in Settings before running polar alignment.");
                return;
            }
            var (azErr, altErr) = PolarAlignmentMath.ComputeError(
                job.Points[0], job.Points[1], job.Points[2],
                userProfile.Latitude, userProfile.Longitude);
            job.AzErrorArcsec = azErr;
            job.AltErrorArcsec = altErr;
            job.TotalErrorArcsec = PolarAlignmentMath.TotalErrorArcsec(azErr, altErr);

            // POLARUI2c: pure-RA-rotation sanity check. All three GoTos
            // commanded the SAME Dec; if the mount's own Dec readout
            // moved by more than a few arcmin between points, its
            // firmware re-aimed the Dec axis (pointing model / plate-
            // solve sync interplay) and the cone assumption — hence the
            // error vector — is invalid. Surface that loudly instead of
            // letting the user chase a fictitious axis.
            var validDecs = mountDecReadings.Where(d => !double.IsNaN(d)).ToList();
            if (validDecs.Count == 3) {
                double decSpreadDeg = validDecs.Max() - validDecs.Min();
                if (decSpreadDeg > 0.1) {
                    _logger.LogWarning(
                        "Polar align: mount-reported Dec drifted {Spread}° across the sweep (readings: {D0}/{D1}/{D2}) — Dec axis moved between points, fit unreliable",
                        decSpreadDeg.ToString("F3"),
                        validDecs[0].ToString("F3"), validDecs[1].ToString("F3"), validDecs[2].ToString("F3"));
                    job.LastError =
                        $"Warning: the mount moved its Dec axis between sweep points " +
                        $"(Dec readout drifted {decSpreadDeg:F2}°). The error vector may be " +
                        "invalid — disable any hand-controller pointing model / star " +
                        "alignment and re-run the sweep.";
                }
            }

            // POLARUI2: fresh sweep → drop the old refinement anchor;
            // the first Refresh after this re-anchors from the new
            // error at the then-current pointing (post home-slew).
            job.RefineTargetRaHours = null;
            job.RefineTargetDecDeg = null;

            // 4. Cosmetic slew home -----------------------------------------
            SetPhase(job, PolarAlignmentPhase.SlewingHome);
            try {
                await telescope.SlewAsync(ra0, dec0, ct);
                await WaitForSlewCompleteAsync(telescope, ct);
            } catch (OperationCanceledException) { throw; }
            catch (Exception ex) {
                // Don't fail the whole alignment if the home slew
                // hiccups, the user already has their error vector.
                _logger.LogWarning(ex, "Polar align: slew back to start failed");
            }

            // 4b. POLARUI2b: anchor the refinement target NOW, while the
            //     job still owns the mount and the operator cannot have
            //     touched the knobs yet. Anchoring lazily on the first
            //     Refresh (previous behaviour) silently assumed the knobs
            //     were untouched between TPPA and that refresh — an
            //     operator who cranks the azimuth right after reading the
            //     error and THEN hits Refresh got a stale target, and
            //     walking the dot to that target parked the axis ~1× the
            //     original error away while the readout showed arcseconds
            //     (field session 2026-07-10: axis left ~10° off in az
            //     after "converging" to 2″). One extra capture+solve at
            //     the end of the sweep closes the race; if it fails we
            //     fall back to first-Refresh anchoring and say so.
            try {
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Max(1, job.Options.SettleSeconds)), ct);
                var anchorImage = await GatedCapture(camera,
                    job.Options.ExposureSeconds,
                    new CaptureOptions(Gain: job.Options.Gain, ImageType: "POLAR"),
                    ct);
                var anchorSolve = await SolveOnceAsync(anchorImage, telescope, ct);
                if (anchorSolve.Success) {
                    var tAnchor = DateTime.UtcNow;
                    var (aRa, aDec) = PolarAlignmentMath.PrecessJ2000ToDate(
                        anchorSolve.RaHours, anchorSolve.DecDeg, tAnchor);
                    var (tRa, tDec) = PolarAlignmentMath.ComputeRefineTarget(
                        aRa, aDec, tAnchor,
                        job.AzErrorArcsec, job.AltErrorArcsec,
                        userProfile.Latitude, userProfile.Longitude);
                    job.RefineTargetRaHours = tRa;
                    job.RefineTargetDecDeg = tDec;
                    _logger.LogInformation(
                        "Polar align: refine anchor set at sweep end — pointing {Ra}h/{Dec}° → target {TRa}h/{TDec}°",
                        aRa.ToString("F4"), aDec.ToString("F4"),
                        tRa.ToString("F4"), tDec.ToString("F4"));
                } else {
                    _logger.LogWarning(
                        "Polar align: anchor solve failed ({Err}); will anchor on the first Refresh — do NOT adjust knobs before it",
                        anchorSolve.Error);
                }
            } catch (OperationCanceledException) { throw; }
            catch (Exception ex) {
                _logger.LogWarning(ex,
                    "Polar align: anchor capture failed; will anchor on the first Refresh");
            }

            // With a >1.5° error the axis is far enough out that the
            // one-shot refinement geometry (and any pointing model the
            // mount holds) accumulates percent-level cross-terms, and a
            // single knob turn covers many arcminutes. Rough-correct,
            // then RE-RUN the sweep — the 3-point fit is the ground
            // truth; refine is for the final arcminutes.
            if (job.TotalErrorArcsec > 1.5 * 3600.0 && job.LastError == null) {
                job.LastError =
                    $"Large initial error ({job.TotalErrorArcsec / 3600.0:F1}°). " +
                    "Correct roughly using the knob directions, then re-run the " +
                    "3-point sweep and use Refresh only for the final arcminutes.";
            }

            // 5. Done -------------------------------------------------------
            job.CompletedAt = DateTime.UtcNow;
            SetPhase(job, PolarAlignmentPhase.Ok);
            _notify.Push("ok",
                "Polar alignment complete, see POLAR tab for error vector.", 5000);
        } catch (OperationCanceledException) {
            SetPhase(job, PolarAlignmentPhase.Cancelled);
            job.CompletedAt = DateTime.UtcNow;
            _notify.Push("warn", "Polar alignment cancelled.");
            // Best-effort slew home so the mount isn't stranded.
            try {
                if (_equip.Telescope != null && ra0 > 0)
                    await _equip.Telescope.SlewAsync(ra0, dec0, CancellationToken.None);
            } catch { /* shutdown, eat it */ }
        } catch (Exception ex) {
            _logger.LogError(ex, "Polar alignment RunAsync crashed");
            Fail(job, ex.Message);
        }
    }

    /// <summary>Write a temp FITS, call the plate solver, delete the
    /// FITS regardless of outcome. Caller decides what to do with
    /// failures (retry / fail the job).</summary>
    private async Task<PlateSolveResult> SolveOnceAsync(
        IImageData image, ITelescope? telescope, CancellationToken ct,
        double? hintRaHours = null, double? hintDecDeg = null) {
        var path = WriteTempFits(image);
        try {
            // RA hint in hours, Dec hint in degrees. Prefer an explicit
            // hint (rudimentary mode passes the target, which is valid even
            // when no mount is connected), then the live mount pointing,
            // else leave null for a blind solve. Reading telescope.* when
            // telescope is null would NRE — rudimentary manual-mount mode
            // legitimately runs with no telescope connected.
            double? hintRa = hintRaHours ?? (telescope != null ? telescope.RightAscension : null);
            double? hintDec = hintDecDeg ?? (telescope != null ? telescope.Declination : null);
            var opts = new PlateSolveOptions {
                HintRa = hintRa,
                HintDec = hintDec,
                SearchRadiusDeg = 30,
                ScaleArcsecPerPixel = ComputePixelScaleHint(),
                FovDeg = 0  // let the solver derive from pixel scale + image size
            };
            return await _plateSolve.SolveAsync(path, opts, ct);
        } finally {
            try { File.Delete(path); } catch { /* housekeeping */ }
        }
    }

    /// <summary>Block until the mount finishes slewing (IsSlewing clears)
    /// or a timeout elapses. <see cref="ITelescope.SlewAsync"/> only waits
    /// for the driver to acknowledge the coord write (slew accepted), not
    /// for the mount to physically arrive — so every TPPA / rudimentary
    /// capture has to gate on this first, otherwise it shoots a frame
    /// while the mount is still moving. Mirrors
    /// SlewCenterService.WaitForSlewComplete.</summary>
    private async Task WaitForSlewCompleteAsync(ITelescope telescope, CancellationToken ct) {
        // Give the driver a beat to raise EQUATORIAL_EOD_COORD to Busy so a
        // stale Ok left over from before the slew isn't read as "already
        // arrived" on the very first poll.
        await Task.Delay(750, ct);
        for (int i = 0; i < 300; i++) {   // up to ~5 min, then give up
            ct.ThrowIfCancellationRequested();
            if (!telescope.IsSlewing) return;
            await Task.Delay(1000, ct);
        }
        _logger.LogWarning("Polar align: slew did not complete within 5 minutes; proceeding anyway");
    }

    private static PolarAlignmentPhase MovingPhaseFor(int index) => index switch {
        0 => PolarAlignmentPhase.MovingToPoint1,
        1 => PolarAlignmentPhase.MovingToPoint2,
        _ => PolarAlignmentPhase.MovingToPoint3,
    };

    private static PolarAlignmentPhase SolvingPhaseFor(int index) => index switch {
        0 => PolarAlignmentPhase.SolvingPoint1,
        1 => PolarAlignmentPhase.SolvingPoint2,
        _ => PolarAlignmentPhase.SolvingPoint3,
    };

    /// <summary>Wrap an RA value back into [0, 24) hours. Adding
    /// slewStepHours can push past 24h near RA=23h.</summary>
    private static double NormalizeRaHours(double ra) {
        var r = ra % 24.0;
        return r < 0 ? r + 24.0 : r;
    }

    /// <summary>Best-effort pixel-scale hint for the plate solver,
    /// computed from camera pixel size + rig main focal length. The
    /// solver derives the real scale from the FITS header too, but
    /// the hint narrows search radius (especially on ASTAP) and is
    /// REQUIRED by PlateSolve3. Returns 0 when either input is
    /// missing, the solver chain handles the unknown-scale case.</summary>
    private double ComputePixelScaleHint() {
        var cam = _equip.Camera;
        if (cam == null) return 0;
        var rig = _profiles.ActiveEquipmentProfile;
        if (rig.FocalLengthMm <= 0) return 0;
        // PixelSizeX is in microns. arcsec/pixel = pixelSize_um * 206.265 / focalLength_mm.
        var px = cam.PixelSizeX;
        if (double.IsNaN(px) || px <= 0) return 0;
        return px * 206.265 / rig.FocalLengthMm;
    }

    private void SetPhase(PolarAlignmentJob job, PolarAlignmentPhase phase) {
        job.Phase = phase;
        try { JobUpdated?.Invoke(job); }
        catch (Exception ex) { _logger.LogDebug(ex, "JobUpdated handler threw"); }
    }

    private void Fail(PolarAlignmentJob job, string error) {
        job.LastError = error;
        job.Phase = PolarAlignmentPhase.Failed;
        job.CompletedAt = DateTime.UtcNow;
        _logger.LogWarning("Polar alignment failed: {Error}", error);
        try { JobUpdated?.Invoke(job); } catch { }
        _notify.Push("error", "Polar alignment failed: " + error);
    }

    /// <summary>Write an IImageData to a freshly-created temp FITS so
    /// the plate solver (which takes a file path, not a buffer) can
    /// consume it. Caller is responsible for deleting the file.
    /// Lives here rather than in ImageWriterService because that
    /// service writes to the configured ImageOutputDir using session
    /// metadata; for polar alignment we want a throwaway temp file.</summary>
    internal static string WriteTempFits(IImageData image) {
        var path = Path.Combine(Path.GetTempPath(),
            "polaris-polar-" + Guid.NewGuid().ToString("N") + ".fits");
        FITSWriter.Write(image, path);
        return path;
    }

    // ─── RDPA-2: Rudimentary single-target polar alignment ──────────
    //
    // Different workflow from TPPA: the user picks ONE bright visible
    // target, Polaris slews + captures + solves once, then reports the
    // pointing error attributed to polar misalignment. The user walks
    // to the mount, nudges azimuth/altitude knobs, and clicks
    // "re-solve" — repeat until happy. No 3-point sweep, no auto
    // convergence threshold (the user decides).
    //
    // Reuses the same PolarAlignmentJob + WS broadcast plumbing so the
    // UI handler can render either mode without branching on Mode at
    // the transport layer. New phases are added below to disambiguate
    // the iteration progress without polluting the TPPA enum values.

    /// <summary>
    /// Start a rudimentary alignment session. Resets CurrentJob to a
    /// fresh job in Rudimentary mode, optionally slews to the target,
    /// captures one frame, plate-solves, and computes the error vector.
    /// The job stays alive after this returns so subsequent
    /// RudimentaryReSolveAsync calls can append to the iteration history.
    /// </summary>
    public async Task<RudimentaryStepResult> StartRudimentaryAsync(
        RudimentaryStartRequest req, CancellationToken ct) {

        if (CurrentJob != null && CurrentJob.IsActive) {
            throw new InvalidOperationException(
                "A polar-alignment job is already in progress. Abort it first.");
        }

        var job = new PolarAlignmentJob {
            Id = Guid.NewGuid().ToString("N"),
            Options = new PolarAlignmentOptions(
                ExposureSeconds: req.ExposureSeconds,
                Gain: req.Gain,
                SettleSeconds: req.SettleSeconds),
            Mode = "rudimentary",
            Phase = PolarAlignmentPhase.Preflight,
            StartedAt = DateTime.UtcNow,
            TargetRaHours = req.TargetRaHours,
            TargetDecDeg = req.TargetDecDeg,
            TargetName = req.TargetName,
        };
        _jobs[job.Id] = job;
        JobRetention.TrimFinished(_jobs, j => j.StartedAt,
            j => j.Phase is PolarAlignmentPhase.Ok or PolarAlignmentPhase.Failed
                          or PolarAlignmentPhase.Cancelled);
        CurrentJob = job;
        job.Cts = new CancellationTokenSource();

        try { JobUpdated?.Invoke(job); } catch { }
        return await DoRudimentaryStepAsync(job, slew: req.SlewToTarget, ct);
    }

    /// <summary>
    /// Run another capture+solve at the current mount position (no
    /// slew). Requires a prior StartRudimentaryAsync that established
    /// the target. Each call appends to the iteration history so the
    /// UI sparkline can show convergence.
    /// </summary>
    public async Task<RudimentaryStepResult> RudimentaryReSolveAsync(CancellationToken ct) {
        var job = CurrentJob;
        if (job == null || job.Mode != "rudimentary" || job.TargetRaHours == null) {
            throw new InvalidOperationException(
                "No active rudimentary alignment session. Call /start first.");
        }
        // Don't bring up a brand-new CTS; reuse the start one so the
        // /abort endpoint cancels in-flight resolves too.
        if (job.Cts == null || job.Cts.IsCancellationRequested) {
            job.Cts = new CancellationTokenSource();
        }
        return await DoRudimentaryStepAsync(job, slew: false, ct);
    }

    private async Task<RudimentaryStepResult> DoRudimentaryStepAsync(
        PolarAlignmentJob job, bool slew, CancellationToken externalCt) {

        // Combine the caller's ct with the job's CTS so /abort works.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            externalCt, job.Cts!.Token);
        var ct = linked.Token;

        try {
            // Preflight: camera + (optional) telescope + site lat/lon.
            SetPhase(job, PolarAlignmentPhase.Preflight);
            var camera = _equip.Camera;
            var telescope = _equip.Telescope;
            var profile = _profiles.Active;

            if (camera == null || !camera.IsConnected) {
                return FailRudimentary(job, "Camera not connected.");
            }
            if (profile.Latitude == 0 && profile.Longitude == 0) {
                return FailRudimentary(job,
                    "Site latitude/longitude not set. Configure your location in Settings.");
            }
            if (slew) {
                if (telescope == null || !telescope.IsConnected) {
                    return FailRudimentary(job,
                        "Telescope not connected. Disable 'Slew to target' if your mount is manual.");
                }
                if (telescope.IsParked) {
                    return FailRudimentary(job,
                        "Telescope is parked. Unpark before running alignment.");
                }
            }

            // Optionally slew.
            if (slew && telescope != null && job.TargetRaHours.HasValue && job.TargetDecDeg.HasValue) {
                SetPhase(job, PolarAlignmentPhase.RudimentarySlewing);
                _notify.Push("info",
                    $"Rudimentary align: slewing to {(job.TargetName ?? "target")}", 2500);
                // Targets come from the catalog in J2000; INDI's
                // EQUATORIAL_EOD_COORD is of-date (JNow). Slewing the raw
                // J2000 numbers points the mount ~0.35° off, which the
                // single-target error would then misread as polar error
                // that never zeroes out. Precess to date for the slew.
                // (The error math still compares J2000 target vs J2000
                // solve, where the epoch offset cancels.)
                var (slewRa, slewDec) = PolarAlignmentMath.PrecessJ2000ToDate(
                    job.TargetRaHours.Value, job.TargetDecDeg.Value, DateTime.UtcNow);
                await telescope.SlewAsync(slewRa, slewDec, ct);
                // Same fix as TPPA: block until the mount actually arrives,
                // not just until the slew command is acknowledged, or we
                // capture + solve mid-slew at the wrong pointing.
                await WaitForSlewCompleteAsync(telescope, ct);
                if (job.Options.SettleSeconds > 0) {
                    await Task.Delay(job.Options.SettleSeconds * 1000, ct);
                }
            }

            // Capture.
            SetPhase(job, PolarAlignmentPhase.RudimentaryCapturing);
            var image = await GatedCapture(camera, 
                job.Options.ExposureSeconds,
                new CaptureOptions(Gain: job.Options.Gain, ImageType: "POLAR"),
                ct);
            if (image == null || image.Properties.Width <= 0 || image.Properties.Height <= 0) {
                return FailRudimentary(job, "Camera returned an empty frame.");
            }

            // Plate solve, with one retry on doubled exposure (same
            // rescue TPPA uses for marginal star count on first try).
            SetPhase(job, PolarAlignmentPhase.RudimentarySolving);
            // Hint with the TARGET coords (valid even on a manual mount with
            // no telescope connected — passing telescope! here would NRE in
            // SolveOnceAsync when reading RightAscension).
            var solve = await SolveOnceAsync(image, telescope, ct,
                hintRaHours: job.TargetRaHours, hintDecDeg: job.TargetDecDeg);
            if (!solve.Success) {
                _logger.LogInformation(
                    "Rudimentary: first solve failed ({Err}), retrying with 2x exposure",
                    solve.Error);
                var retry = await GatedCapture(camera, 
                    job.Options.ExposureSeconds * 2.0,
                    new CaptureOptions(Gain: job.Options.Gain, ImageType: "POLAR"),
                    ct);
                if (retry != null && retry.Properties.Width > 0) {
                    solve = await SolveOnceAsync(retry, telescope, ct,
                        hintRaHours: job.TargetRaHours, hintDecDeg: job.TargetDecDeg);
                }
            }
            if (!solve.Success) {
                return FailRudimentary(job,
                    $"Plate solve failed: {solve.Error}. Increase exposure or gain in rig settings.");
            }

            // Compute the single-target polar error.
            var (azErr, altErr) = PolarAlignmentMath.ComputeErrorSingleTarget(
                targetRaHours: job.TargetRaHours!.Value,
                targetDecDeg: job.TargetDecDeg!.Value,
                solvedRaHours: solve.RaHours,
                solvedDecDeg: solve.DecDeg,
                siteLatDeg: profile.Latitude,
                siteLongDeg: profile.Longitude,
                utcNow: DateTime.UtcNow);
            var total = PolarAlignmentMath.TotalErrorArcsec(azErr, altErr);

            job.SolvedRaHours = solve.RaHours;
            job.SolvedDecDeg = solve.DecDeg;
            job.AzErrorArcsec = azErr;
            job.AltErrorArcsec = altErr;
            job.TotalErrorArcsec = total;
            job.History.Add(new RudimentaryIteration(total, DateTime.UtcNow));
            // Cap history at 20 entries so a long session doesn't leak
            // memory in the WS payload.
            while (job.History.Count > 20) job.History.RemoveAt(0);

            SetPhase(job, PolarAlignmentPhase.Ok);
            return new RudimentaryStepResult(
                Ok: true, Error: null,
                SolvedRaHours: solve.RaHours, SolvedDecDeg: solve.DecDeg,
                AzErrorArcsec: azErr, AltErrorArcsec: altErr,
                TotalErrorArcsec: total,
                IterationCount: job.History.Count);
        } catch (OperationCanceledException) {
            SetPhase(job, PolarAlignmentPhase.Cancelled);
            return new RudimentaryStepResult(false, "Cancelled by user",
                0, 0, 0, 0, 0, job.History.Count);
        } catch (Exception ex) {
            _logger.LogError(ex, "Rudimentary alignment step crashed");
            return FailRudimentary(job, ex.Message);
        }
    }

    private RudimentaryStepResult FailRudimentary(PolarAlignmentJob job, string error) {
        job.LastError = error;
        job.Phase = PolarAlignmentPhase.Failed;
        try { JobUpdated?.Invoke(job); } catch { }
        _notify.Push("error", "Polar alignment: " + error);
        return new RudimentaryStepResult(false, error,
            0, 0, 0, 0, 0, job.History.Count);
    }
}
