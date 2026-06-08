using NINA.Core.Enum;
using NINA.Guider.Portable;
using NINA.Image.Interfaces;
using PortableGuideStep = NINA.Guider.Portable.GuideStep;

namespace NINA.Polaris.Services;

/// <summary>
/// In-process native autoguider. A drop-in alternative to the external
/// PHD2 integration, selected per-rig via <c>EquipmentProfile.GuiderDriver
/// == "native"</c>. Drives the rig's own guide camera + mount pulse
/// guiding through the ported PHD2 math in <c>NINA.Guider.Portable</c>.
///
/// <para>Implements <see cref="IGuider"/> so GuiderEndpoints, the status
/// WebSocket and the GUIDE tab consume it identically to PHD2. The DTO
/// shapes it emits (<see cref="GuideStep"/>, <see cref="SettleResult"/>,
/// <see cref="CalibrationData"/>) are the existing PHD2 records, so the
/// WebSocket JSON is byte-identical.</para>
///
/// <para>Scope: single- or multi-star centroid, RA+Dec calibration,
/// per-axis algorithms (Hysteresis/ResistSwitch/Lowpass/Lowpass2),
/// pulse guide (INDI/ASCOM/Alpaca), Dec backlash compensation, basic
/// dither. Deferred: pier-side/parity, ZFilter/GaussianProcess.</para>
/// </summary>
public sealed class NativeGuider : IGuider, IDisposable {
    private readonly EquipmentManager _equipment;
    private readonly ProfileService _profiles;
    private readonly ILogger<NativeGuider> _logger;

    private const int MaxSteps = 300;
    private const double Deg2Rad = Math.PI / 180.0;
    // Star search half-window (px) around the lock position each frame.
    private const int SearchRegion = 15;
    // Default guide-scope focal length when the rig hasn't set one.
    private const double DefaultGuiderFocalLengthMm = 200.0;

    private readonly object _stepsLock = new();
    private readonly List<GuideStep> _recentSteps = new();
    private readonly RmsCalculator _rms = new(100);

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Lock-position + calibration state (guarded by the loop being single).
    private double _lockX, _lockY;
    private bool _haveLock;
    private GuideCalibration _calibration = GuideCalibration.Invalid;
    // Human-readable calibration step, surfaced to the GUIDE UI so the user
    // sees what's happening (ASIAIR-style "Dec (south) step 4, dist 12.3 px").
    private volatile string? _calProgress;

    // Multi-star field tracker (primary + secondaries). Engaged only when the
    // rig enables it and more than one star was locked; otherwise the single
    // star ROI path below is used.
    private readonly MultiStarTracker _multiStar = new(SearchRegion);

    // Live view snapshot for the PHD2-style GUIDE UI (frame + overlay). The
    // guide loop captures frames already; we keep a reference to the latest one
    // plus the star/lock overlay so the WS payload (ViewState) and the JPEG
    // endpoint (EncodeViewJpeg) can surface them without re-capturing.
    private IImageData? _lastFrame;
    private int _lastFrameOriginX, _lastFrameOriginY;
    private volatile ViewFrame? _view;
    private long _viewSeq;

    /// <summary>Immutable-ish snapshot of one guide frame + its overlay. The
    /// pixel buffer is the camera's own (not cloned); it is not mutated after
    /// capture, and the reference swap is atomic.</summary>
    private sealed class ViewFrame {
        public ushort[] Pixels = Array.Empty<ushort>();
        public int Width, Height, BitDepth, OriginX, OriginY;
        public double LockX, LockY;
        public bool HaveLock;
        // (x, y) in full-sensor coords, per-star SNR, primary flag, found flag.
        public List<(double x, double y, double snr, bool primary, bool found)> Stars = new();
        public long FrameId;
    }

    // Per-axis algorithms (rebuilt from profile on guiding start).
    private IGuideAlgorithm _raAlgo = new HysteresisAlgorithm();
    private IGuideAlgorithm _decAlgo = new ResistSwitchAlgorithm();
    private BacklashComp _backlashComp = new(0);

    // Dither bookkeeping.
    private volatile bool _paused;
    private GuidingSettler? _settler;
    private double _settleThresholdPx = 1.5;
    private int _starLostCount;

    public string Backend => "native";

    public bool IsConnected { get; private set; }
    public string AppState { get; private set; } = "Stopped";
    public bool IsGuiding => AppState == "Guiding";
    public bool IsCalibrating => AppState == "Calibrating";
    public string? CalibrationProgress => _calProgress;
    public bool IsPaused => AppState == "Paused";
    public bool IsLooping => AppState == "Looping";
    public bool IsSettling { get; private set; }

    public double PixelScale { get; private set; }
    public string? LastAlert { get; private set; }
    public DateTime? LastAlertAt { get; private set; }
    public string? LastSettleStatus { get; private set; }

    public double RmsRA { get; private set; }
    public double RmsDec { get; private set; }
    public double RmsTotal { get; private set; }
    public double PeakRA { get; private set; }
    public double PeakDec { get; private set; }

    public event Action<string>? AppStateChanged;
    public event Action<GuideStep>? GuideStepReceived;
    public event Action<string>? Alert;
    public event Action<SettleResult>? Settled;

    public NativeGuider(EquipmentManager equipment, ProfileService profiles,
                        ILogger<NativeGuider> logger) {
        _equipment = equipment;
        _profiles = profiles;
        _logger = logger;
    }

    private EquipmentProfile Rig => _profiles.ActiveEquipmentProfile;

    private void SetAppState(string s) {
        if (AppState == s) return;
        AppState = s;
        AppStateChanged?.Invoke(s);
    }

    private void RaiseAlert(string msg) {
        LastAlert = msg;
        LastAlertAt = DateTime.UtcNow;
        _logger.LogWarning("Native guider alert: {Msg}", msg);
        Alert?.Invoke(msg);
    }

    private void RecomputePixelScale() {
        var cam = _equipment.GuideCamera;
        var fl = Rig.GuiderFocalLengthMm;
        if (fl <= 0) {
            _logger.LogWarning(
                "GuiderFocalLengthMm is {Fl}; falling back to {Default}mm for pixel scale",
                fl, DefaultGuiderFocalLengthMm);
            fl = DefaultGuiderFocalLengthMm;
        }
        if (cam != null && cam.PixelSizeX > 0 && fl > 0) {
            PixelScale = 206.265 * cam.PixelSizeX / fl;
        } else {
            PixelScale = 0;
        }
    }

    // ----- Connection -----

    public async Task ConnectAsync(string host = "localhost", int port = 4400,
                                   CancellationToken ct = default) {
        var cam = _equipment.GuideCamera;
        if (cam == null) {
            throw new InvalidOperationException(
                "No guide camera selected for native guiding. Pick one in RIGS.");
        }
        if (_equipment.Camera != null &&
            ReferenceEquals(_equipment.Camera, cam)) {
            throw new InvalidOperationException(
                "Guide camera must be different from the imaging camera.");
        }
        if (!cam.IsConnected) {
            await cam.ConnectAsync(ct);
        }
        RecomputePixelScale();
        IsConnected = true;
        SetAppState("Stopped");
        _logger.LogInformation(
            "Native guider connected: cam={Cam}, pixelScale={Scale:F2} arcsec/px",
            cam.DeviceName, PixelScale);
    }

    public async Task DisconnectAsync(CancellationToken ct = default) {
        await StopLoopAsync();
        IsConnected = false;
        IsSettling = false;
        _haveLock = false;
        _multiStar.Clear();
        _view = null;
        _lastFrame = null;
        SetAppState("Stopped");
        _logger.LogInformation("Native guider disconnected");
    }

    // ----- Commands -----

    public async Task StartGuidingAsync(double settlePixels = 1.5, int settleTime = 10,
            int settleTimeout = 40, bool recalibrate = false, CancellationToken ct = default) {
        EnsureConnected();
        if (recalibrate || !_calibration.IsValid) {
            await CalibrateAsync(ct);
            if (!_calibration.IsValid) {
                RaiseAlert("Native guiding aborted: calibration failed.");
                return;
            }
        }
        if (!_haveLock) {
            await AutoSelectStarAsync(ct);
            if (!_haveLock) {
                RaiseAlert("Native guiding aborted: no guide star found.");
                return;
            }
        }
        BuildAlgorithms();
        await BuildMultiStarAsync(ct);
        _settleThresholdPx = settlePixels;
        _settler = new GuidingSettler(settlePixels, settleTime, settleTimeout, NowMs());
        await StartLoopAsync(LoopMode.Guide);
    }

    public Task StopAsync(CancellationToken ct = default) => StopLoopAsync();

    public Task LoopAsync(CancellationToken ct = default) {
        EnsureConnected();
        return StartLoopAsync(LoopMode.Loop);
    }

    public Task PauseAsync(CancellationToken ct = default) {
        if (IsGuiding) {
            _paused = true;
            SetAppState("Paused");
        }
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken ct = default) {
        if (IsPaused) {
            _paused = false;
            SetAppState("Guiding");
        }
        return Task.CompletedTask;
    }

    public Task DitherAsync(double pixels = 5.0, bool raOnly = false, double settlePixels = 1.5,
            int settleTime = 10, int settleTimeout = 40, CancellationToken ct = default) {
        if (!IsGuiding || !_haveLock) {
            return Task.CompletedTask;
        }
        // Offset the lock position by a random vector of the requested
        // magnitude; the guide loop then chases the star back, and the
        // settler reports done/timeout. raOnly restricts the offset to
        // the camera X axis as an MVP approximation of RA-only dithering.
        var rng = Random.Shared;
        double angle = raOnly ? 0.0 : rng.NextDouble() * 2.0 * Math.PI;
        double mag = pixels * (0.5 + rng.NextDouble() * 0.5);
        double offX = mag * Math.Cos(angle);
        double offY = raOnly ? 0.0 : mag * Math.Sin(angle);
        _lockX += offX;
        _lockY += offY;
        // Shift every tracked star's reference by the same vector so multi-star
        // stays consistent with the new lock point.
        _multiStar.OffsetReferences(offX, offY);
        _raAlgo.Reset();
        _decAlgo.Reset();
        IsSettling = true;
        LastSettleStatus = "settling";
        _settleThresholdPx = settlePixels;
        _settler = new GuidingSettler(settlePixels, settleTime, settleTimeout, NowMs());
        _logger.LogInformation("Native dither: {Px}px (raOnly={RaOnly})", pixels, raOnly);
        return Task.CompletedTask;
    }

    public async Task SetExposureAsync(int milliseconds, CancellationToken ct = default) {
        if (milliseconds <= 0) return;
        _profiles.UpdateEquipmentProfile(Rig.Id, r => r.NativeGuideExposureMs = milliseconds);
        await Task.CompletedTask;
    }

    public async Task AutoSelectStarAsync(CancellationToken ct = default) {
        EnsureConnected();
        var cam = _equipment.GuideCamera!;
        // Clear any ROI so detection sees the full sensor.
        try { await cam.SetSubframeAsync(0, 0, 0, 0, ct); } catch { }
        var img = await CaptureFullAsync(cam, ct);
        if (img == null) {
            RaiseAlert("Auto-select failed: no guide frame.");
            return;
        }
        int w = img.Properties.Width, h = img.Properties.Height;
        var detector = new NINA.Image.ImageAnalysis.StarDetector();
        var stars = detector.Detect(img.Data, w, h);

        // Pick the brightest, non-saturated, interior star (away from
        // edges so the search window stays in-frame).
        int margin = SearchRegion + 5;
        double satGuard = (1 << Math.Max(1, img.Properties.BitDepth)) - 1;
        NINA.Image.ImageAnalysis.DetectedStar? best = null;
        double bestFlux = -1;
        foreach (var s in stars) {
            if (s.X < margin || s.Y < margin || s.X > w - margin || s.Y > h - margin) continue;
            if (satGuard > 1 && s.Peak >= satGuard * 0.95) continue;
            if (s.Flux > bestFlux) { bestFlux = s.Flux; best = s; }
        }
        if (best == null) {
            RaiseAlert("Auto-select failed: no suitable guide star.");
            return;
        }
        _lockX = best.X;
        _lockY = best.Y;
        _haveLock = true;
        SetAppState("Selected");
        _logger.LogInformation("Native guide star locked at ({X:F1},{Y:F1})", _lockX, _lockY);
    }

    /// <summary>Lock the detected star nearest to a clicked full-sensor point.
    /// Captures a fresh frame, detects stars and picks the closest interior,
    /// non-saturated one to (targetX, targetY). Rebuilds the view so the
    /// overlay updates immediately.</summary>
    public async Task SelectStarNearAsync(double targetX, double targetY, CancellationToken ct = default) {
        EnsureConnected();
        var cam = _equipment.GuideCamera!;
        try { await cam.SetSubframeAsync(0, 0, 0, 0, ct); } catch { }
        var img = await CaptureFullAsync(cam, ct);
        if (img == null) { RaiseAlert("Select star failed: no guide frame."); return; }

        int w = img.Properties.Width, h = img.Properties.Height;
        _lastFrame = img; _lastFrameOriginX = 0; _lastFrameOriginY = 0;
        var detector = new NINA.Image.ImageAnalysis.StarDetector();
        var stars = detector.Detect(img.Data, w, h);

        int margin = SearchRegion + 5;
        double satGuard = (1 << Math.Max(1, img.Properties.BitDepth)) - 1;
        NINA.Image.ImageAnalysis.DetectedStar? best = null;
        double bestDist = double.MaxValue;
        foreach (var s in stars) {
            if (s.X < margin || s.Y < margin || s.X > w - margin || s.Y > h - margin) continue;
            if (satGuard > 1 && s.Peak >= satGuard * 0.95) continue;
            double dx = s.X - targetX, dy = s.Y - targetY;
            double d = dx * dx + dy * dy;
            if (d < bestDist) { bestDist = d; best = s; }
        }
        if (best == null) { RaiseAlert("No suitable star near the click."); return; }

        _lockX = best.X;
        _lockY = best.Y;
        _haveLock = true;
        _multiStar.Clear();
        SetAppState("Selected");
        BuildView(_lockX, _lockY, 0, true);
        _logger.LogInformation("Native guide star picked near ({Tx:F0},{Ty:F0}) -> ({X:F1},{Y:F1})",
            targetX, targetY, _lockX, _lockY);
    }

    public Task ClearCalibrationAsync(CancellationToken ct = default) {
        _calibration = GuideCalibration.Invalid;
        _logger.LogInformation("Native calibration cleared");
        return Task.CompletedTask;
    }

    public List<GuideStep> SnapshotSteps() {
        lock (_stepsLock) return new List<GuideStep>(_recentSteps);
    }

    public void ClearStepHistory() {
        lock (_stepsLock) {
            _recentSteps.Clear();
            _rms.Reset();
            RmsRA = RmsDec = RmsTotal = PeakRA = PeakDec = 0;
        }
    }

    // ----- Calibration -----

    private async Task CalibrateAsync(CancellationToken ct) {
        EnsureConnected();
        var cam = _equipment.GuideCamera!;
        var mount = _equipment.Telescope;
        if (mount == null || !mount.IsConnected || !mount.Capabilities.SupportsPulseGuide) {
            RaiseAlert("Calibration needs a connected, pulse-guide-capable mount.");
            return;
        }
        if (!_haveLock) {
            await AutoSelectStarAsync(ct);
            if (!_haveLock) return;
        }

        SetAppState("Calibrating");
        try { await cam.SetSubframeAsync(0, 0, 0, 0, ct); } catch { }

        double decRad = double.IsNaN(mount.Declination) ? double.NaN : mount.Declination * Deg2Rad;
        int stepMs = Math.Max(50, Rig.NativeCalibrationStepMs);
        // Threshold scales with frame so big sensors don't undershoot.
        var process = new CalibrationProcess(stepMs, 25.0, 60, decRad);

        _calProgress = "Calibrating: locating star...";
        try {
            // Seed the process with the current centroid.
            var (curX, curY, found) = await FindStarAsync(cam, ct);
            if (!found) { RaiseAlert("Calibration failed: star lost at start."); SetAppState("Stopped"); return; }

            string? lastPhase = null;
            double phStartX = curX, phStartY = curY;
            int phaseStep = 0;

            for (int i = 0; i < 200 && !ct.IsCancellationRequested; i++) {
                var step = process.Tick(curX, curY);
                if (step.Failed) {
                    RaiseAlert($"Calibration failed: {step.Phase}");
                    SetAppState("Stopped");
                    return;
                }
                if (step.Done) break;

                // Reset the per-phase step counter + reference position whenever
                // the calibration phase changes (West -> East -> Dec ...).
                if (step.Phase != lastPhase) {
                    lastPhase = step.Phase; phStartX = curX; phStartY = curY; phaseStep = 0;
                }
                phaseStep++;
                double dx = curX - phStartX, dy = curY - phStartY;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                _calProgress = $"{step.Phase}: step {phaseStep}, dist {dist:F1} px";

                if (step.Pulse && step.DurationMs > 0) {
                    try {
                        await mount.PulseGuideAsync(step.Direction, step.DurationMs, ct);
                    } catch (Exception ex) {
                        RaiseAlert($"Calibration pulse failed: {ex.Message}");
                        SetAppState("Stopped");
                        return;
                    }
                    await SettleAfterPulse(step.DurationMs, ct);
                }
                (curX, curY, found) = await FindStarAsync(cam, ct);
                if (!found) {
                    RaiseAlert("Calibration failed: star lost mid-sequence.");
                    SetAppState("Stopped");
                    return;
                }
            }

            _calibration = process.Result;
            if (_calibration.IsValid) {
                // Stamp the pier side this calibration was measured on so a later
                // meridian flip can mirror it instead of forcing a recalibration.
                _calibration = _calibration with { CalibrationPierSide = mount.SideOfPier };
                // Re-lock at the recentred position.
                _lockX = curX; _lockY = curY;
                _calProgress = "Calibration complete";
                _logger.LogInformation(
                    "Native calibration complete: xAngle={Xa:F3} xRate={Xr:F5} yAngle={Ya:F3} yRate={Yr:F5}",
                    _calibration.XAngle, _calibration.XRate, _calibration.YAngle, _calibration.YRate);
            } else {
                RaiseAlert("Calibration did not complete.");
            }
            SetAppState("Stopped");
        } finally {
            _calProgress = null;
        }
    }

    // ----- Guide loop -----

    private enum LoopMode { Loop, Guide }

    private async Task StartLoopAsync(LoopMode mode) {
        await StopLoopAsync();
        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        _paused = false;
        _starLostCount = 0;
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
        if (AppState is "Guiding" or "Looping" or "Paused") SetAppState("Stopped");
    }

    private async Task LoopAsync(LoopMode mode, CancellationToken ct) {
        var cam = _equipment.GuideCamera!;
        var mount = _equipment.Telescope;
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
            // Keep guiding state but skip the pulse; alert occasionally.
            if (_starLostCount % 5 == 1) RaiseAlert("Guide star lost; skipping correction.");
            PushStep(new PortableGuideStep(NowMs(), 0, 0, 0, 0, 0, 0, snr, hfd, false));
            BuildView(curX, curY, snr, false);
            return;
        }
        _starLostCount = 0;

        double dx = curX - _lockX;
        double dy = curY - _lockY;
        var (raPx, decPx) = MountCoordTransform.CameraToMount(_calibration, dx, dy);

        // Per-axis algorithm: correction (pixels) to apply this frame.
        double raCorr = _raAlgo.Result(raPx);
        double decCorr = _decAlgo.Result(decPx);

        // Rates: RA scaled for declination, Dec from calibration.
        double decRad = (mount != null && !double.IsNaN(mount.Declination))
            ? mount.Declination * Deg2Rad : _calibration.DeclinationRad;
        double raRate = MountCoordTransform.RaRateAtDec(_calibration, decRad);
        double decRate = _calibration.YRate;

        int minMoveRaMs = RateToMs(Rig.NativeMinMoveRaPx, raRate);
        int minMoveDecMs = RateToMs(Rig.NativeMinMoveDecPx, decRate);
        // Clamp pulse to the exposure period so corrections can't run away.
        int maxMs = expMs;

        int raMs = MountCoordTransform.ComputeMoveDurationMs(raCorr, raRate, minMoveRaMs, maxMs);
        int decMs = MountCoordTransform.ComputeMoveDurationMs(decCorr, decRate, minMoveDecMs, maxMs);

        // Direction: correction moves the star back toward lock. PHD2
        // calibration measured WEST as +X-rate and SOUTH as +Y-rate, so a
        // positive RA error (star drifted +RA-px) is corrected by pulsing
        // EAST, positive Dec by NORTH.
        var raDir = raCorr >= 0 ? GuideDirections.guideEast : GuideDirections.guideWest;
        var decDir = decCorr >= 0 ? GuideDirections.guideNorth : GuideDirections.guideSouth;

        // Dec backlash compensation: on a direction reversal, add the measured
        // slack take-up, re-clamped to the runaway guard.
        if (decMs > 0) decMs = Math.Min(_backlashComp.Adjust(decDir, decMs), maxMs);

        if (mount != null && mount.IsConnected && mount.Capabilities.SupportsPulseGuide) {
            try {
                if (raMs > 0) await mount.PulseGuideAsync(raDir, raMs, ct);
                if (decMs > 0) await mount.PulseGuideAsync(decDir, decMs, ct);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Pulse guide failed");
            }
        }

        double scale = PixelScale > 0 ? PixelScale : 1.0;
        var step = new PortableGuideStep(
            NowMs(),
            raPx * scale, decPx * scale,
            raPx, decPx,
            raMs, decMs,
            snr, hfd, true);
        PushStep(step);
        BuildView(curX, curY, snr, true);

        // Settle progress (dither / start).
        if (_settler != null) {
            double totalErrPx = Math.Sqrt(raPx * raPx + decPx * decPx);
            var state = _settler.Update(totalErrPx, NowMs());
            if (state != GuidingSettler.State.Settling) {
                IsSettling = false;
                bool ok = state == GuidingSettler.State.Done;
                LastSettleStatus = ok ? "done" : "failed";
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

    private async Task<IImageData?> CaptureFullAsync(ICamera cam, CancellationToken ct) {
        int expMs = Math.Max(50, Rig.NativeGuideExposureMs);
        int bin = Math.Clamp(Rig.NativeGuideBin <= 0 ? 1 : Rig.NativeGuideBin, 1, 4);
        var opts = new CaptureOptions(
            Gain: Rig.NativeGuideGain > 0 ? Rig.NativeGuideGain : (int?)null,
            BinX: bin, BinY: bin);
        try {
            return await cam.CaptureAsync(expMs / 1000.0, opts, ct);
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

        var calSide = _calibration.CalibrationPierSide;
        var nowSide = mount.SideOfPier;
        if (calSide == PierSide.pierUnknown || nowSide == PierSide.pierUnknown) return;
        if (nowSide == calSide) return; // no flip

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
        _raAlgo.Reset();
        _decAlgo.Reset();
        _backlashComp.Reset();
        // The field also rotated 180 deg, so re-seed the multi-star set and the
        // lock star on the new side.
        _haveLock = false;
        await AutoSelectStarAsync(ct);
        if (_haveLock) await BuildMultiStarAsync(ct);
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

    private async Task<(double x, double y, bool found)> FindStarAsync(ICamera cam, CancellationToken ct) {
        var (x, y, found, _, _) = await FindStarDetailedAsync(cam, ct);
        return (x, y, found);
    }

    private async Task<(double x, double y, bool found, double snr, double hfd)>
            FindStarDetailedAsync(ICamera cam, CancellationToken ct) {
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
        var result = GuideStar.Find(img.Data, w, h, _lockX, _lockY, SearchRegion);
        if (!result.Found) {
            return (_lockX, _lockY, false, result.Snr, result.Hfd);
        }
        return (result.X, result.Y, true, result.Snr, result.Hfd);
    }

    // ----- Internals -----

    private void BuildAlgorithms() {
        // Per-axis algorithm selection (default hysteresis RA / resist-switch Dec,
        // PHD2's defaults). Lowpass/Lowpass2/Identity also available.
        _raAlgo = GuideAlgorithmFactory.Create(
            string.IsNullOrWhiteSpace(Rig.NativeRaAlgorithm) ? "hysteresis" : Rig.NativeRaAlgorithm,
            minMove: Math.Max(0.0, Rig.NativeMinMoveRaPx),
            aggression: Math.Clamp(Rig.NativeRaAggression, 0.0, 2.0),
            hysteresis: Math.Clamp(Rig.NativeRaHysteresis, 0.0, 0.99));
        _decAlgo = GuideAlgorithmFactory.Create(
            string.IsNullOrWhiteSpace(Rig.NativeDecAlgorithm) ? "resistswitch" : Rig.NativeDecAlgorithm,
            minMove: Math.Max(0.0, Rig.NativeMinMoveDecPx),
            aggression: Math.Clamp(Rig.NativeRaAggression, 0.0, 2.0),
            hysteresis: Math.Clamp(Rig.NativeRaHysteresis, 0.0, 0.99));
        _raAlgo.Reset();
        _decAlgo.Reset();
        // Dec backlash compensation: only when enabled on the rig AND the
        // calibration actually measured a backlash. Disabled by default
        // because an over-large value oscillates worse than no comp.
        double measuredBacklash = Rig.NativeBacklashComp ? _calibration.BacklashMs : 0;
        _backlashComp = new BacklashComp(measuredBacklash, Rig.NativeBacklashMaxMs);
        _backlashComp.Reset();
    }

    private static int RateToMs(double px, double ratePxPerMs) {
        if (ratePxPerMs <= 0) return 0;
        return (int)Math.Round(px / ratePxPerMs);
    }

    private void PushStep(PortableGuideStep p) {
        var step = new GuideStep {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(p.TimestampMs).UtcDateTime,
            RaPixels = p.RaRawPx,
            DecPixels = p.DecRawPx,
            RaArcsec = p.RaArcsec,
            DecArcsec = p.DecArcsec,
            SNR = p.Snr,
            Mass = 0,
            RaDuration = p.RaDurationMs,
            DecDuration = p.DecDurationMs,
            RaDirection = null,
            DecDirection = null
        };
        lock (_stepsLock) {
            _recentSteps.Add(step);
            if (_recentSteps.Count > MaxSteps) _recentSteps.RemoveAt(0);
            if (p.StarFound) _rms.Add(p.RaArcsec, p.DecArcsec);
            var (rRa, rDec, rTot, pRa, pDec) = _rms.Compute();
            RmsRA = rRa; RmsDec = rDec; RmsTotal = rTot; PeakRA = pRa; PeakDec = pDec;
        }
        if (IsGuiding) SetAppState("Guiding");
        GuideStepReceived?.Invoke(step);
    }

    // ----- Live view (PHD2-style GUIDE UI) -----

    /// <summary>Snapshot the latest captured frame + star/lock overlay into the
    /// atomically-swapped <see cref="_view"/> for the WS payload and JPEG endpoint.</summary>
    private void BuildView(double primaryX, double primaryY, double snr, bool found) {
        var img = _lastFrame;
        if (img == null) return;
        var vf = new ViewFrame {
            Pixels = img.Data,
            Width = img.Properties.Width,
            Height = img.Properties.Height,
            BitDepth = img.Properties.BitDepth,
            OriginX = _lastFrameOriginX,
            OriginY = _lastFrameOriginY,
            LockX = _lockX, LockY = _lockY, HaveLock = _haveLock,
            FrameId = ++_viewSeq
        };
        bool multi = Rig.NativeMultiStar && _multiStar.Count > 1;
        if (multi) {
            foreach (var s in _multiStar.Stars)
                vf.Stars.Add((s.CurX, s.CurY, s.Snr, s.IsPrimary, s.Found));
        } else if (found && !double.IsNaN(primaryX)) {
            vf.Stars.Add((primaryX, primaryY, snr, true, true));
        }
        _view = vf;
    }

    /// <summary>WS-serializable view: frame geometry, lock, star markers, and a
    /// star-profile cross-section + FWHM. Coordinates are full-sensor pixels;
    /// the frame buffer's top-left maps to (OriginX, OriginY).</summary>
    public object? ViewState {
        get {
            var vf = _view;
            if (vf == null) return null;
            var (profile, fwhm) = ComputeProfile(vf);
            return new {
                width = vf.Width,
                height = vf.Height,
                originX = vf.OriginX,
                originY = vf.OriginY,
                lockX = vf.HaveLock ? vf.LockX : (double?)null,
                lockY = vf.HaveLock ? vf.LockY : (double?)null,
                frameId = vf.FrameId,
                stars = vf.Stars.Select(s => new {
                    x = s.x, y = s.y, snr = s.snr, primary = s.primary, found = s.found
                }),
                profile,
                fwhm
            };
        }
    }

    /// <summary>Mid-row intensity cross-section (normalized 0..1) through the
    /// primary star + a FWHM estimate (px). Returns an empty profile when no
    /// primary star/lock is available.</summary>
    private static (double[] profile, double fwhm) ComputeProfile(ViewFrame vf) {
        double px = double.NaN, py = double.NaN;
        foreach (var s in vf.Stars) {
            if (s.primary && s.found) { px = s.x - vf.OriginX; py = s.y - vf.OriginY; break; }
        }
        if (double.IsNaN(px) && vf.HaveLock) { px = vf.LockX - vf.OriginX; py = vf.LockY - vf.OriginY; }
        if (double.IsNaN(px)) return (Array.Empty<double>(), 0);

        int cx = (int)Math.Round(px), cy = (int)Math.Round(py);
        if (cy < 0 || cy >= vf.Height || vf.Pixels.Length < (long)vf.Width * vf.Height)
            return (Array.Empty<double>(), 0);

        const int half = 15;
        int n = half * 2 + 1;
        var prof = new double[n];
        double mn = double.MaxValue, mx = double.MinValue;
        for (int i = 0; i < n; i++) {
            int x = cx - half + i;
            double v = (x >= 0 && x < vf.Width) ? vf.Pixels[cy * vf.Width + x] : 0;
            prof[i] = v;
            if (v < mn) mn = v;
            if (v > mx) mx = v;
        }
        double range = mx - mn;
        if (range < 1e-6) range = 1;
        for (int i = 0; i < n; i++) prof[i] = (prof[i] - mn) / range;
        return (prof, FwhmFromProfile(prof));
    }

    /// <summary>FWHM (px) from a normalized cross-section: width between the
    /// half-maximum crossings on either side of the peak, linearly interpolated.</summary>
    private static double FwhmFromProfile(double[] p) {
        if (p.Length < 3) return 0;
        int peak = 0;
        for (int i = 1; i < p.Length; i++) if (p[i] > p[peak]) peak = i;
        const double halfMax = 0.5; // normalized peak is 1, baseline 0
        double Cross(int from, int step) {
            for (int i = from; i >= 0 && i < p.Length; i += step) {
                if (p[i] <= halfMax) {
                    int prev = i - step;
                    if (prev < 0 || prev >= p.Length) return i;
                    double denom = p[prev] - p[i];
                    double frac = Math.Abs(denom) < 1e-9 ? 0 : (p[prev] - halfMax) / denom;
                    return prev + step * frac;
                }
            }
            return step < 0 ? 0 : p.Length - 1;
        }
        double left = Cross(peak, -1);
        double right = Cross(peak, 1);
        return Math.Max(0, right - left);
    }

    /// <summary>Encode the latest guide frame as an auto-stretched JPEG for the
    /// PHD2-style camera view. Returns null when no frame is available yet.</summary>
    public byte[]? EncodeViewJpeg(int maxDim = 600, int quality = 75) {
        var vf = _view;
        if (vf == null || vf.Pixels.Length < (long)vf.Width * vf.Height) return null;
        try {
            return NINA.Polaris.Services.Studio.FitsThumbnailer.RenderJpegFromBuffer(
                vf.Pixels, vf.Width, vf.Height, vf.BitDepth, maxDim, quality);
        } catch {
            return null;
        }
    }

    private void EnsureConnected() {
        if (!IsConnected)
            throw new InvalidOperationException("Native guider not connected.");
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static async Task SettleAfterPulse(int pulseMs, CancellationToken ct) {
        // Small dwell after a pulse so the mount applies it before the next
        // measurement. Cap so calibration doesn't crawl.
        int dwell = Math.Clamp(pulseMs + 250, 100, 3000);
        try { await Task.Delay(dwell, ct); } catch (OperationCanceledException) { }
    }

    public void Dispose() {
        try { StopLoopAsync().Wait(2000); } catch { }
        _gate.Dispose();
    }
}
