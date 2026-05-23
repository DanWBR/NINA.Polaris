using System.Collections.Concurrent;

namespace NINA.Polaris.Services;

/// <summary>
/// Multi-phase orchestrator for PHD2 calibration. Replaces the manual
/// "open PHD2, point near equator, set step size, click guide" flow with
/// a single button: Polaris computes a sane calibration step from pixel
/// scale + guide rate, optionally slews the MAIN mount to a known-good
/// calibration position, then triggers PHD2 guide() which initiates
/// calibration on an uncalibrated mount.
///
/// We can run this even when the user has no main mount slew (just skip
/// the SlewToEquator phase). When PHD2's mount is already correctly
/// positioned, the user can disable SlewToEquator and we'll only handle
/// the step calc + monitoring.
/// </summary>
public class PHD2CalibrationOrchestrator {
    private readonly PHD2Client _phd2;
    private readonly EquipmentManager _equip;
    private readonly SlewCenterService _slewCenter;
    private readonly ProfileService _profiles;
    private readonly ILogger<PHD2CalibrationOrchestrator> _logger;

    private readonly ConcurrentDictionary<string, CalibrationJob> _jobs = new();
    public CalibrationJob? CurrentJob { get; private set; }
    public event Action<CalibrationJob>? JobUpdated;

    public PHD2CalibrationOrchestrator(PHD2Client phd2,
                                       EquipmentManager equip,
                                       SlewCenterService slewCenter,
                                       ProfileService profiles,
                                       ILogger<PHD2CalibrationOrchestrator> logger) {
        _phd2 = phd2;
        _equip = equip;
        _slewCenter = slewCenter;
        _profiles = profiles;
        _logger = logger;
    }

    public CalibrationJob StartJob(SmartCalibrateOptions opts) {
        var job = new CalibrationJob {
            Id = Guid.NewGuid().ToString("N"),
            Options = opts,
            State = CalibrationPhase.Preflight,
            StartedAt = DateTime.UtcNow
        };
        _jobs[job.Id] = job;
        CurrentJob = job;
        job.Cts = new CancellationTokenSource();
        job.Task = Task.Run(() => RunAsync(job, job.Cts.Token));
        return job;
    }

    public CalibrationJob? GetJob(string id) =>
        _jobs.TryGetValue(id, out var j) ? j : null;

    public void Abort(string id) {
        if (_jobs.TryGetValue(id, out var j)) j.Cts?.Cancel();
    }

    private async Task RunAsync(CalibrationJob job, CancellationToken ct) {
        try {
            // 1. Preflight ----------------------------------------------------
            SetPhase(job, CalibrationPhase.Preflight);
            if (!_phd2.IsConnected) { Fail(job, "PHD2 not connected"); return; }
            var equip = await _phd2.GetCurrentEquipmentAsync(ct);
            if (equip?.Camera?.Connected != true) { Fail(job, "PHD2 guide camera not connected"); return; }
            if (equip.Mount?.Connected != true) { Fail(job, "PHD2 mount not connected"); return; }

            // 2. Pixel scale --------------------------------------------------
            SetPhase(job, CalibrationPhase.PixelScale);
            double pxScale = _phd2.PixelScale;
            if (pxScale <= 0) {
                // PHD2 sometimes returns 0 if pixel size + focal length
                // aren't filled in the profile. Fall back to computing
                // from the rig's guider focal length if we can.
                var rig = _profiles.ActiveEquipmentProfile;
                if (rig.GuiderFocalLengthMm > 0) {
                    // Without a known pixel size, assume 4 µm (typical guide
                    // cam). User can fix the PHD2 profile to get a real value.
                    double assumedPxUm = 4.0;
                    pxScale = assumedPxUm * 206.265 / rig.GuiderFocalLengthMm;
                    job.Warnings.Add($"PHD2 pixel scale unknown — assuming {pxScale:F2}\"/px from rig focal length");
                } else {
                    Fail(job, "PHD2 pixel scale unavailable and rig guider focal length not set");
                    return;
                }
            }
            job.PixelScale = pxScale;

            // 3. Compute step -------------------------------------------------
            SetPhase(job, CalibrationPhase.Computing);
            const double defaultGuideRateArcsecPerSec = 7.5;  // 0.5x sidereal
            double guideRate = defaultGuideRateArcsecPerSec;
            // INDI exposes guide rate for some mounts but our IndiTelescope
            // abstraction doesn't surface it yet — fall back to the PHD2
            // wizard default. Future: read from _equip.Telescope if exposed.

            int stepMs;
            if (job.Options.CalibrationStepMsOverride > 0) {
                stepMs = job.Options.CalibrationStepMsOverride;
            } else {
                stepMs = CalibrationStepCalculator.Compute(pxScale, guideRate, distancePx: 25);
            }
            job.CalibrationStepMs = stepMs;
            _logger.LogInformation("Calibration step computed: pxScale={Px:F2} guideRate={GR} → {Ms}ms",
                pxScale, guideRate, stepMs);

            // 4. Slew (optional) ---------------------------------------------
            if (job.Options.SlewToEquator) {
                SetPhase(job, CalibrationPhase.Slewing);
                if (_equip.Telescope == null) {
                    job.Warnings.Add("SlewToEquator requested but no main telescope connected — skipping");
                } else {
                    // RA = current LST (meridian); Dec = 0 (equator).
                    // For now we use the option's TargetRaHours if set, else
                    // ask the user to supply via PHD2 mount directly. PHD2
                    // doesn't expose mount RA over its API, so we fall back
                    // to a fixed offset from local time.
                    double targetRa = job.Options.TargetRaHours ?? LocalSiderealHours();
                    double targetDec = job.Options.TargetDecDeg;
                    var slewJob = _slewCenter.StartJob(targetRa, targetDec);
                    // Poll the slew job's task
                    if (slewJob.Task != null) {
                        try { await slewJob.Task.WaitAsync(TimeSpan.FromSeconds(180), ct); }
                        catch (TimeoutException) {
                            job.Warnings.Add("Slew timed out at 180s — proceeding anyway");
                        }
                    }
                }
            }

            // 5. Apply step ---------------------------------------------------
            SetPhase(job, CalibrationPhase.ApplyingStep);
            // PHD2 Mount-axis algorithm exposes "calibration_step" on the
            // mount group; some PHD2 versions name it differently. Try a
            // couple of axis names defensively.
            bool stepApplied =
                await _phd2.SetAlgoParamAsync("Mount", "calibration_step", stepMs, ct)
                || await _phd2.SetAlgoParamAsync("ra", "calibration_step", stepMs, ct);
            if (!stepApplied) {
                job.Warnings.Add($"Could not apply calibration_step={stepMs}ms via set_algo_param — using PHD2's existing value");
            }

            // 6. Clear + find + guide ----------------------------------------
            SetPhase(job, CalibrationPhase.Calibrating);
            await _phd2.ClearCalibrationAsync();
            await _phd2.SetExposureMsAsync(job.Options.ExposureMsOverride ?? 2000, ct);
            await _phd2.LoopAsync();
            await Task.Delay(TimeSpan.FromSeconds(3), ct);  // settle loop
            try { await _phd2.AutoSelectStarAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "find_star failed (continuing)"); }
            // Start guide with recalibrate=true — that forces fresh calibration.
            await _phd2.StartGuidingAsync(
                settlePixels: 1.5, settleTime: 10, settleTimeout: 60, recalibrate: true);

            // 7. Monitor ------------------------------------------------------
            // Wait for AppState to become Guiding (success) or Stopped after
            // an error (failure) or timeout.
            var tcs = new TaskCompletionSource<bool>();
            Action<string> onAppState = state => {
                if (state == "Guiding") tcs.TrySetResult(true);
                else if (state == "Stopped" && job.State == CalibrationPhase.Calibrating)
                    tcs.TrySetResult(false);
            };
            Action<string> onAlert = msg => { job.LastAlert = msg; };

            _phd2.AppStateChanged += onAppState;
            _phd2.Alert += onAlert;
            try {
                var monitorTask = tcs.Task;
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(job.Options.TimeoutSeconds), ct);
                var winner = await Task.WhenAny(monitorTask, timeoutTask);
                if (winner == timeoutTask) {
                    Fail(job, $"Calibration timed out at {job.Options.TimeoutSeconds}s ({job.LastAlert ?? "no PHD2 alert"})");
                    return;
                }
                if (!await monitorTask) {
                    Fail(job, "PHD2 stopped during calibration ("
                        + (job.LastAlert ?? "no alert detail") + ")");
                    return;
                }
            } finally {
                _phd2.AppStateChanged -= onAppState;
                _phd2.Alert -= onAlert;
            }

            // 8. Validate -----------------------------------------------------
            SetPhase(job, CalibrationPhase.Validating);
            var cal = _phd2.Calibration;
            if (cal == null || !cal.Calibrated) {
                Fail(job, "PHD2 reports not calibrated after Guiding state — unexpected");
                return;
            }
            job.Calibration = cal;

            // Orthogonality check: |XAngle - YAngle| should be near 90°.
            double angleDelta = Math.Abs(((cal.XAngle - cal.YAngle) * 180.0 / Math.PI + 360) % 180 - 90);
            if (angleDelta > 20) {
                job.Warnings.Add(
                    $"Calibration axes not orthogonal (Δ={angleDelta:F1}°) — guiding may be unreliable");
            }
            if (Math.Abs(cal.XRate) < 1e-5) {
                Fail(job, $"Calibration rate near zero (XRate={cal.XRate:E2}) — mount didn't move during calibration");
                return;
            }

            // 9. Done ---------------------------------------------------------
            SetPhase(job, CalibrationPhase.Ok);
            job.CompletedAt = DateTime.UtcNow;
            JobUpdated?.Invoke(job);

        } catch (OperationCanceledException) {
            Fail(job, "Cancelled");
        } catch (Exception ex) {
            _logger.LogError(ex, "Smart calibration crashed");
            Fail(job, ex.Message);
        }
    }

    private void SetPhase(CalibrationJob job, CalibrationPhase phase) {
        job.State = phase;
        try { JobUpdated?.Invoke(job); }
        catch (Exception ex) { _logger.LogDebug(ex, "JobUpdated handler threw"); }
    }

    private void Fail(CalibrationJob job, string error) {
        job.Error = error;
        job.State = CalibrationPhase.Fail;
        job.CompletedAt = DateTime.UtcNow;
        _logger.LogWarning("Smart calibration failed: {Error}", error);
        try { JobUpdated?.Invoke(job); } catch { }
    }

    private static double LocalSiderealHours() {
        // Crude approximation: GMST at UTC midnight = J2000 epoch offset.
        // For the slew helper this only needs to be within ~15min — PHD2
        // calibrates on whatever star is in view after the slew.
        var now = DateTime.UtcNow;
        var jd = now.ToOADate() + 2415018.5;
        var T = (jd - 2451545.0) / 36525.0;
        var gmst = 6.697374558 + 0.06570982441908 * (jd - 2451545.0)
                   + 1.00273790935 * (now.Hour + now.Minute / 60.0 + now.Second / 3600.0);
        gmst = ((gmst % 24) + 24) % 24;
        return gmst;
    }
}

/// <summary>
/// Pure helper for calibration step computation. PHD2's recommended
/// formula: <c>step_ms = round(distance_px * pixel_scale_arcsec / guide_rate_arcsec_per_sec * 1000)</c>.
/// Capped to [250, 3000] ms — PHD2's own sane window.
/// </summary>
public static class CalibrationStepCalculator {
    public const int MinStepMs = 250;
    public const int MaxStepMs = 3000;
    public const int DefaultDistancePx = 25;

    public static int Compute(double pixelScaleArcsecPerPx, double guideRateArcsecPerSec, int distancePx = DefaultDistancePx) {
        if (pixelScaleArcsecPerPx <= 0 || guideRateArcsecPerSec <= 0 || distancePx <= 0) return MinStepMs;
        var ms = (int)Math.Round(distancePx * pixelScaleArcsecPerPx / guideRateArcsecPerSec * 1000.0);
        return Math.Clamp(ms, MinStepMs, MaxStepMs);
    }
}

public record SmartCalibrateOptions(
    bool SlewToEquator = false,
    double? TargetRaHours = null,
    double TargetDecDeg = 0.0,
    int? ExposureMsOverride = null,
    int CalibrationStepMsOverride = 0,
    int TimeoutSeconds = 240);

public class CalibrationJob {
    public string Id { get; set; } = "";
    public SmartCalibrateOptions Options { get; set; } = new();
    public CalibrationPhase State { get; set; } = CalibrationPhase.Preflight;
    public double PixelScale { get; set; }
    public int CalibrationStepMs { get; set; }
    public CalibrationData? Calibration { get; set; }
    public string? Error { get; set; }
    public string? LastAlert { get; set; }
    public List<string> Warnings { get; set; } = new();
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    internal Task? Task { get; set; }
    internal CancellationTokenSource? Cts { get; set; }
}

public enum CalibrationPhase {
    Preflight, PixelScale, Computing, Slewing, ApplyingStep,
    Calibrating, Validating, Ok, Fail
}
