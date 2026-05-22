using NINA.Image.FileFormat.FITS;
using NINA.Image.Interfaces;

namespace NINA.Headless.Services;

/// <summary>
/// Watches the live-stack frame stream and fires auto-refocus +
/// auto-recenter when the user-configured triggers cross threshold.
///
/// Frame handler is awaited sequentially inside
/// <see cref="LiveStackingService.AddFrameAsync"/>, so a slow trigger
/// run (a 60-second AF sweep, a 30-second recenter) naturally pauses
/// the upstream capture loop — no separate mutex needed. <see cref="_isExecuting"/>
/// guards against a frame arriving mid-execution from triggering a
/// second concurrent action.
///
/// Reference RA/Dec for recenter is set by a one-shot plate solve on
/// the first integrated frame. Failure leaves recenter disabled with
/// a clear error surfaced through <see cref="CurrentStatus"/>.
/// </summary>
public class LiveStackTriggersService : IDisposable {
    private readonly LiveStackingService _stack;
    private readonly ProfileService _profiles;
    private readonly EquipmentManager _equip;
    private readonly AutoFocusService _autoFocus;
    private readonly SlewCenterService _slewCenter;
    private readonly PlateSolveService _solver;
    private readonly ILogger<LiveStackTriggersService> _logger;

    private readonly IDisposable _frameSub;
    private readonly object _stateLock = new();

    // Trigger snapshot state — reset on LiveStack Reset() (we hook
    // FrameCount==1 to do it implicitly).
    private DateTime _lastRefocusAt = DateTime.MinValue;
    private int _lastRefocusFrame;
    private double _lastRefocusTempC = double.NaN;
    private double _lastRefocusHfr;
    private DateTime _lastRecenterAt = DateTime.MinValue;
    private int _lastRecenterFrame;
    private double _lastRecenterDriftArcsec;

    private double? _referenceRaHours;
    private double? _referenceDecDeg;
    private bool _referenceSolved;

    private volatile bool _isExecuting;
    private volatile string? _executingKind;
    private string? _lastError;

    public LiveStackTriggers Settings => _profiles.ActiveEquipmentProfile.LiveStackTriggers;

    public LiveStackTriggersStatus CurrentStatus {
        get {
            lock (_stateLock) {
                return new LiveStackTriggersStatus {
                    IsExecuting = _isExecuting,
                    ExecutingKind = _executingKind,
                    LastRefocusAt = _lastRefocusAt == DateTime.MinValue ? null : _lastRefocusAt,
                    LastRefocusFrame = _lastRefocusFrame,
                    LastRefocusHfr = _lastRefocusHfr,
                    LastRefocusTempC = _lastRefocusTempC,
                    LastRecenterAt = _lastRecenterAt == DateTime.MinValue ? null : _lastRecenterAt,
                    LastRecenterFrame = _lastRecenterFrame,
                    LastRecenterDriftArcsec = _lastRecenterDriftArcsec,
                    ReferenceRaHours = _referenceRaHours,
                    ReferenceDecDeg = _referenceDecDeg,
                    ReferenceSolved = _referenceSolved,
                    LastError = _lastError
                };
            }
        }
    }

    public event Action<LiveStackTriggersStatus>? StatusChanged;

    public LiveStackTriggersService(LiveStackingService stack,
                                    ProfileService profiles,
                                    EquipmentManager equip,
                                    AutoFocusService autoFocus,
                                    SlewCenterService slewCenter,
                                    PlateSolveService solver,
                                    ILogger<LiveStackTriggersService> logger) {
        _stack = stack;
        _profiles = profiles;
        _equip = equip;
        _autoFocus = autoFocus;
        _slewCenter = slewCenter;
        _solver = solver;
        _logger = logger;
        _frameSub = _stack.SubscribeFrameIntegrated(OnFrameIntegratedAsync);
        // Reset trigger state when the user switches rigs — they get a
        // fresh slate per rig.
        _profiles.EquipmentProfileActivated += _ => ResetTriggerState();
    }

    /// <summary>Public reset used by /api/livestack/reset path so the
    /// trigger state matches the stack state.</summary>
    public void ResetTriggerState() {
        lock (_stateLock) {
            _lastRefocusAt = DateTime.MinValue;
            _lastRefocusFrame = 0;
            _lastRefocusTempC = double.NaN;
            _lastRefocusHfr = 0;
            _lastRecenterAt = DateTime.MinValue;
            _lastRecenterFrame = 0;
            _lastRecenterDriftArcsec = 0;
            _referenceRaHours = null;
            _referenceDecDeg = null;
            _referenceSolved = false;
            _lastError = null;
        }
        Notify();
    }

    private async Task OnFrameIntegratedAsync(LiveStackFrameInfo info) {
        // Reentry guard. If a previous trigger is still running, drop
        // this frame's evaluation — no point queueing AFs back to back.
        if (_isExecuting) return;

        // Frame 1 = bootstrap: kick the reference solve once (off the
        // critical path — we don't want the first stack frame to block
        // for 5-10 seconds while ASTAP runs).
        if (info.FrameCount == 1) {
            ResetTriggerState();    // clear state from previous session
            _ = Task.Run(() => SolveReferenceAsync(info.Frame));
            return;
        }

        var cfg = Settings;

        // Refocus first — there's no point recenter'ing on a defocused
        // frame, and AF takes longer than recenter so doing it now
        // amortises the pause better than splitting across two frames.
        if (cfg.RefocusEnabled && ShouldRefocus(info, cfg)) {
            await ExecuteRefocusAsync(info, cfg);
            return;
        }

        // Optional per-frame drift solve. Only run when the user has
        // explicitly enabled it (it's expensive — full plate solve per
        // frame). Result feeds into ShouldRecenter below.
        double? currentDrift = null;
        if (cfg.RecenterEnabled && cfg.RecenterDriftArcsec > 0 && _referenceSolved) {
            currentDrift = await ComputeDriftAsync(info.Frame);
        }

        if (cfg.RecenterEnabled && ShouldRecenter(info, cfg, currentDrift)) {
            await ExecuteRecenterAsync(info, cfg, currentDrift);
        }
    }

    private bool ShouldRefocus(LiveStackFrameInfo info, LiveStackTriggers cfg) {
        // Frame count gate
        if (cfg.RefocusEveryNFrames > 0
            && info.FrameCount - _lastRefocusFrame >= cfg.RefocusEveryNFrames)
            return true;
        // Time elapsed gate
        if (cfg.RefocusEveryMinutes > 0 && _lastRefocusAt != DateTime.MinValue
            && (info.At - _lastRefocusAt) >= TimeSpan.FromMinutes(cfg.RefocusEveryMinutes))
            return true;
        // Time gate with no prior run — fire on first opportunity
        if (cfg.RefocusEveryMinutes > 0 && _lastRefocusAt == DateTime.MinValue
            && info.FrameCount > 1)
            return true;
        // Temperature delta gate
        if (cfg.RefocusTempDeltaC > 0 && _equip.Camera != null
            && !double.IsNaN(_lastRefocusTempC)) {
            var t = _equip.Camera.Temperature;
            if (!double.IsNaN(t) && Math.Abs(t - _lastRefocusTempC) >= cfg.RefocusTempDeltaC)
                return true;
        }
        // HFR degradation gate — only meaningful once we have a baseline.
        if (cfg.RefocusHfrIncreasePercent > 0 && _lastRefocusHfr > 0
            && info.MedianHfr > 0
            && info.MedianHfr >= _lastRefocusHfr * (1 + cfg.RefocusHfrIncreasePercent / 100.0))
            return true;
        return false;
    }

    private bool ShouldRecenter(LiveStackFrameInfo info, LiveStackTriggers cfg, double? currentDrift) {
        if (cfg.RecenterEveryNFrames > 0
            && info.FrameCount - _lastRecenterFrame >= cfg.RecenterEveryNFrames)
            return true;
        if (cfg.RecenterEveryMinutes > 0 && _lastRecenterAt != DateTime.MinValue
            && (info.At - _lastRecenterAt) >= TimeSpan.FromMinutes(cfg.RecenterEveryMinutes))
            return true;
        if (cfg.RecenterEveryMinutes > 0 && _lastRecenterAt == DateTime.MinValue
            && info.FrameCount > 1)
            return true;
        if (cfg.RecenterDriftArcsec > 0 && currentDrift.HasValue
            && currentDrift.Value >= cfg.RecenterDriftArcsec)
            return true;
        return false;
    }

    private async Task ExecuteRefocusAsync(LiveStackFrameInfo info, LiveStackTriggers cfg) {
        _isExecuting = true;
        _executingKind = "refocus";
        Notify();
        try {
            _logger.LogInformation("Live-stack triggers: firing refocus at frame {N}", info.FrameCount);
            _autoFocus.Start(cfg.RefocusRequest);
            // Poll until idle. Cap at 5 minutes to avoid waiting forever
            // on a hung AF (very unlikely — AutoFocusService has its own
            // timeouts but defence in depth).
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (_autoFocus.State == AutoFocusState.Running && DateTime.UtcNow < deadline) {
                await Task.Delay(500);
            }
            lock (_stateLock) {
                _lastRefocusFrame = info.FrameCount;
                _lastRefocusAt = info.At;
                _lastRefocusTempC = _equip.Camera?.Temperature ?? double.NaN;
                _lastRefocusHfr = _autoFocus.LastResult?.FinalMeasuredHfr
                    ?? info.MedianHfr;  // fall back to the trigger frame's HFR
            }
            if (_autoFocus.LastResult?.Success != true) {
                _lastError = "AF failed: " + (_autoFocus.LastError ?? "unknown");
                _logger.LogWarning(_lastError);
            }
        } catch (Exception ex) {
            _lastError = "Refocus exception: " + ex.Message;
            _logger.LogError(ex, "Live-stack refocus crashed");
        } finally {
            _isExecuting = false;
            _executingKind = null;
            Notify();
        }
    }

    private async Task ExecuteRecenterAsync(LiveStackFrameInfo info, LiveStackTriggers cfg, double? observedDrift) {
        if (!_referenceSolved || _referenceRaHours == null || _referenceDecDeg == null) {
            _lastError = "Recenter skipped — reference RA/Dec not solved";
            return;
        }
        _isExecuting = true;
        _executingKind = "recenter";
        Notify();
        try {
            _logger.LogInformation("Live-stack triggers: firing recenter at frame {N} (drift={Drift:F1}\")",
                info.FrameCount, observedDrift ?? 0);
            var job = _slewCenter.StartJob(_referenceRaHours.Value, _referenceDecDeg.Value,
                cfg.RecenterToleranceArcsec);
            // Poll the job. SlewCenterService caps at 5 iterations, so
            // total realistic max is ~3 minutes (slew + capture + solve
            // per iter). Same 5-minute defence-in-depth cap.
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (job.State != SlewCenterState.Centered
                && job.State != SlewCenterState.Failed
                && job.State != SlewCenterState.Cancelled
                && DateTime.UtcNow < deadline) {
                await Task.Delay(500);
            }
            lock (_stateLock) {
                _lastRecenterFrame = info.FrameCount;
                _lastRecenterAt = info.At;
                _lastRecenterDriftArcsec = observedDrift ?? job.ErrorArcsec ?? 0;
            }
            if (job.State != SlewCenterState.Centered) {
                _lastError = "Recenter failed: " + (job.Error ?? job.State.ToString());
                _logger.LogWarning(_lastError);
            }
        } catch (Exception ex) {
            _lastError = "Recenter exception: " + ex.Message;
            _logger.LogError(ex, "Live-stack recenter crashed");
        } finally {
            _isExecuting = false;
            _executingKind = null;
            Notify();
        }
    }

    private async Task SolveReferenceAsync(IImageData firstFrame) {
        var tempFits = Path.Combine(Path.GetTempPath(),
            $"nina_livestack_ref_{Guid.NewGuid():N}.fits");
        try {
            FITSWriter.Write(firstFrame, tempFits);
            var result = await _solver.SolveAsync(tempFits, new PlateSolveOptions {
                SearchRadiusDeg = 30, Downsample = 2
            });
            if (result.Success) {
                lock (_stateLock) {
                    _referenceRaHours = result.RaHours;
                    _referenceDecDeg = result.DecDeg;
                    _referenceSolved = true;
                }
                _logger.LogInformation(
                    "Live-stack reference solved: RA={Ra:F4}h Dec={Dec:F4}°",
                    result.RaHours, result.DecDeg);
            } else {
                _lastError = "Reference solve failed: " + (result.Error ?? "unknown");
                _logger.LogWarning(_lastError);
            }
            Notify();
        } catch (Exception ex) {
            _lastError = "Reference solve crashed: " + ex.Message;
            _logger.LogWarning(ex, _lastError);
        } finally {
            try { File.Delete(tempFits); } catch { }
        }
    }

    private async Task<double?> ComputeDriftAsync(IImageData frame) {
        if (!_referenceSolved || _referenceRaHours == null || _referenceDecDeg == null) return null;
        var tempFits = Path.Combine(Path.GetTempPath(),
            $"nina_livestack_drift_{Guid.NewGuid():N}.fits");
        try {
            FITSWriter.Write(frame, tempFits);
            var result = await _solver.SolveAsync(tempFits, new PlateSolveOptions {
                HintRa = _referenceRaHours.Value,
                HintDec = _referenceDecDeg.Value,
                SearchRadiusDeg = 5,
                Downsample = 2
            });
            if (!result.Success) return null;
            return AngularDistanceArcsec(
                result.RaHours, result.DecDeg,
                _referenceRaHours.Value, _referenceDecDeg.Value);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Drift solve failed (non-fatal)");
            return null;
        } finally {
            try { File.Delete(tempFits); } catch { }
        }
    }

    /// <summary>Great-circle angular distance in arcseconds via the
    /// haversine formula. Inputs are RA in hours, Dec in degrees.</summary>
    public static double AngularDistanceArcsec(double ra1Hours, double dec1Deg,
                                                double ra2Hours, double dec2Deg) {
        const double degToRad = Math.PI / 180.0;
        const double hourToRad = Math.PI / 12.0;
        double phi1 = dec1Deg * degToRad;
        double phi2 = dec2Deg * degToRad;
        double dphi = (dec2Deg - dec1Deg) * degToRad;
        double dlam = (ra2Hours - ra1Hours) * hourToRad;
        double a = Math.Sin(dphi / 2) * Math.Sin(dphi / 2)
                 + Math.Cos(phi1) * Math.Cos(phi2) * Math.Sin(dlam / 2) * Math.Sin(dlam / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return c * (180.0 / Math.PI) * 3600.0;
    }

    /// <summary>Manual fire — bypasses all gates. Used by the UI
    /// "▶ Now" button. Still respects the reentry guard.</summary>
    public async Task FireRefocusNowAsync() {
        if (_isExecuting) return;
        var info = new LiveStackFrameInfo(_stack.FrameCount, null!,
            _stack.LastFrameMedianHfr, _stack.LastFrameStarCount, DateTime.UtcNow);
        await ExecuteRefocusAsync(info, Settings);
    }
    public async Task FireRecenterNowAsync() {
        if (_isExecuting) return;
        var info = new LiveStackFrameInfo(_stack.FrameCount, null!,
            _stack.LastFrameMedianHfr, _stack.LastFrameStarCount, DateTime.UtcNow);
        await ExecuteRecenterAsync(info, Settings, null);
    }

    private void Notify() {
        try { StatusChanged?.Invoke(CurrentStatus); }
        catch (Exception ex) { _logger.LogDebug(ex, "StatusChanged handler threw"); }
    }

    public void Dispose() { _frameSub.Dispose(); }
}

public class LiveStackTriggersStatus {
    public bool IsExecuting { get; init; }
    public string? ExecutingKind { get; init; }
    public DateTime? LastRefocusAt { get; init; }
    public int LastRefocusFrame { get; init; }
    public double LastRefocusHfr { get; init; }
    public double LastRefocusTempC { get; init; }
    public DateTime? LastRecenterAt { get; init; }
    public int LastRecenterFrame { get; init; }
    public double LastRecenterDriftArcsec { get; init; }
    public double? ReferenceRaHours { get; init; }
    public double? ReferenceDecDeg { get; init; }
    public bool ReferenceSolved { get; init; }
    public string? LastError { get; init; }
}
