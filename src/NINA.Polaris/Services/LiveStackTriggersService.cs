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

using NINA.Image.FileFormat.FITS;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>
/// Watches the live-stack frame stream and fires auto-refocus +
/// auto-recenter when the user-configured triggers cross threshold.
///
/// Frame handler is awaited sequentially inside
/// <see cref="LiveStackingService.AddFrameAsync"/>, so a slow trigger
/// run (a 60-second AF sweep, a 30-second recenter) naturally pauses
/// the upstream capture loop, no separate mutex needed. <see cref="_isExecuting"/>
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
    private readonly ActiveGuiderProvider _guiders;
    private readonly DitherBarrier _barrier;
    private readonly ILogger<LiveStackTriggersService> _logger;

    private readonly IDisposable _frameSub;
    private readonly object _stateLock = new();

    // Trigger snapshot state, reset on LiveStack Reset() (we hook
    // FrameCount==1 to do it implicitly).
    private DateTime _lastRefocusAt = DateTime.MinValue;
    private int _lastRefocusFrame;
    private double _lastRefocusTempC = double.NaN;
    private double _lastRefocusHfr;
    private DateTime _lastRecenterAt = DateTime.MinValue;
    private int _lastRecenterFrame;
    private double _lastRecenterDriftArcsec;
    private int _lastDitherFrame;

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
                // NaN/Infinity are NOT valid JSON and would 500 the
                // /api/livestack/triggers/status endpoint. The "not
                // yet observed" sentinels (NaN for temp, 0 for hfr /
                // drift / frame) become null so the UI's null-coalesce
                // can render an em-dash.
                return new LiveStackTriggersStatus {
                    IsExecuting = _isExecuting,
                    ExecutingKind = _executingKind,
                    LastRefocusAt = _lastRefocusAt == DateTime.MinValue ? null : _lastRefocusAt,
                    LastRefocusFrame = _lastRefocusFrame == 0 ? null : _lastRefocusFrame,
                    LastRefocusHfr = SafeDouble(_lastRefocusHfr, zeroMeansUnset: true),
                    LastRefocusTempC = SafeDouble(_lastRefocusTempC, zeroMeansUnset: false),
                    LastRecenterAt = _lastRecenterAt == DateTime.MinValue ? null : _lastRecenterAt,
                    LastRecenterFrame = _lastRecenterFrame == 0 ? null : _lastRecenterFrame,
                    LastRecenterDriftArcsec = SafeDouble(_lastRecenterDriftArcsec, zeroMeansUnset: true),
                    LastDitherFrame = _lastDitherFrame == 0 ? null : _lastDitherFrame,
                    ReferenceRaHours = SafeDouble(_referenceRaHours),
                    ReferenceDecDeg = SafeDouble(_referenceDecDeg),
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
                                    ActiveGuiderProvider guiders,
                                    DitherBarrier barrier,
                                    ILogger<LiveStackTriggersService> logger) {
        _stack = stack;
        _profiles = profiles;
        _equip = equip;
        _autoFocus = autoFocus;
        _slewCenter = slewCenter;
        _solver = solver;
        _guiders = guiders;
        _barrier = barrier;
        _logger = logger;
        _frameSub = _stack.SubscribeFrameIntegrated(OnFrameIntegratedAsync);
        // Reset trigger state when the user switches rigs, they get a
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
            _lastDitherFrame = 0;
            _referenceRaHours = null;
            _referenceDecDeg = null;
            _referenceSolved = false;
            _lastError = null;
        }
        Notify();
    }

    private async Task OnFrameIntegratedAsync(LiveStackFrameInfo info) {
        // Reentry guard. If a previous trigger is still running, drop
        // this frame's evaluation, no point queueing AFs back to back.
        if (_isExecuting) return;

        // Frame 1 = bootstrap: kick the reference solve once (off the
        // critical path, we don't want the first stack frame to block
        // for 5-10 seconds while ASTAP runs).
        if (info.FrameCount == 1) {
            ResetTriggerState();    // clear state from previous session
            // REFSOLVE (#633): the reference solve exists ONLY to give the
            // recenter trigger a coordinate baseline to measure drift against
            // (see ComputeDriftAsync / ExecuteRecenterAsync). With recenter off
            // it was pure waste — a 5-10 s ASTAP run per session that can fail
            // intermittently and spam the log — so only solve when recenter is on.
            if (Settings.RecenterEnabled)
                _ = Task.Run(() => SolveReferenceAsync(info.Frame));
            return;
        }

        var cfg = Settings;

        // Refocus first, there's no point recenter'ing on a defocused
        // frame, and AF takes longer than recenter so doing it now
        // amortises the pause better than splitting across two frames.
        if (cfg.RefocusEnabled && ShouldRefocus(info, cfg)) {
            await ExecuteRefocusAsync(info, cfg);
            return;
        }

        // Dither (ASIAIR-style, every N frames). Return only if one actually
        // ran, so a recenter cannot cancel the offset we just applied. A SKIP
        // has to fall through: the dither gate no longer advances on a skip, so
        // returning here would take the same branch on every frame and an
        // unguided session would never recenter again.
        // Dither config is now the GLOBAL per-rig DitherProfile (shared with
        // AUTORUN, ADV and the multi-camera barrier), not the LIVE-specific
        // LiveStackTriggers.Dither* fields.
        var dg = _profiles.ActiveEquipmentProfile.EffectiveDither;
        if (dg.Enabled && dg.EveryNFrames > 0) {
            // Multi-camera: the barrier owns the cadence (driven by the slowest
            // camera) and dithers for everyone from the capture loop, so hand it
            // our config and skip the per-loop dither here. Single-camera keeps
            // the existing every-N behavior.
            _barrier.ConfigureCadence(dg.EveryNFrames, new DitherParams(
                dg.Pixels, dg.RaOnly, dg.SettlePixels,
                dg.SettleTime, dg.SettleTimeout));
            if (!_barrier.OwnsDither
                && info.FrameCount - _lastDitherFrame >= dg.EveryNFrames) {
                if (await ExecuteDitherAsync(info, dg)) return;
            }
        }

        // Optional per-frame drift solve. Only run when the user has
        // explicitly enabled it (it's expensive, full plate solve per
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
        // Time gate with no prior run, fire on first opportunity
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
        // HFR degradation gate, only meaningful once we have a baseline.
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
            // on a hung AF (very unlikely, AutoFocusService has its own
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
            _lastError = "Recenter skipped, reference RA/Dec not solved";
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

    /// <summary>Returns true when a dither was actually issued.</summary>
    private async Task<bool> ExecuteDitherAsync(LiveStackFrameInfo info, DitherSettings dg) {
        var g = _guiders.Active;
        // Nothing to dither with. The gate is deliberately NOT advanced: a skip
        // is not a dither, and treating it as one turns a momentary blip into
        // several frames with no dither at all.
        //
        // Measured in the field (2026-08-10): the native guider dropped a BLOB
        // and declared the star lost at 23:43:37.887; frame 9 finished
        // integrating at 23:43:37.927, forty milliseconds later. That one read
        // of IsGuiding marked frame 9's dither as done, so with "every 3
        // frames" the session dithered at 3, 6 and then not again until 12.
        // Three minutes of subs landing on the same pixels because the guider
        // hiccuped inside a 40 ms window.
        //
        // Re-testing on the next frame costs two boolean reads, so the only
        // thing the old shortcut bought was quieter logs. Hence the transition
        // guard below instead: the state is reported once, not per frame.
        if (!g.IsConnected || !g.IsGuiding) {
            var why = !g.IsConnected ? "guider not connected" : "guider not guiding";
            var notice = "Dither skipped: " + why;
            if (_lastError != notice) {
                _logger.LogInformation(
                    "Live-stack dither skipped at frame {N}: {Why} ({Backend}). "
                    + "It will dither on the first frame after guiding resumes.",
                    info.FrameCount, why, g.Backend);
            }
            _lastError = notice;
            Notify();
            return false;
        }

        _isExecuting = true;
        _executingKind = "dither";
        // A real dither is starting (guider IS guiding now), so any stale
        // "Dither skipped: ..." notice from a previous frame no longer applies
        // — clear it so the message disappears once dithering resumes normally.
        if (_lastError != null && _lastError.StartsWith("Dither skipped", StringComparison.Ordinal))
            _lastError = null;
        Notify();

        // Wait for SettleDone before the next frame integrates, exactly like the
        // AUTORUN sequencer, so the dithered frame isn't stacked mid-slew.
        var settled = new TaskCompletionSource<SettleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSettled(SettleResult r) => settled.TrySetResult(r);
        g.Settled += OnSettled;
        try {
            _logger.LogInformation("Live-stack dither: {Px}px at frame {N} (raOnly={Ra}, backend={Backend})",
                dg.Pixels, info.FrameCount, dg.RaOnly, g.Backend);
            await g.DitherAsync(
                pixels: dg.Pixels,
                raOnly: dg.RaOnly,
                settlePixels: dg.SettlePixels,
                settleTime: dg.SettleTime,
                settleTimeout: dg.SettleTimeout);
            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(dg.SettleTimeout + 5));
            try {
                var r = await settled.Task.WaitAsync(cts.Token);
                if (r.Status != 0)
                    _logger.LogWarning("Live-stack dither settle status {S}: {E}", r.Status, r.Error);
            } catch (OperationCanceledException) {
                _logger.LogWarning("Live-stack dither settle timed out, continuing");
            }
        } catch (Exception ex) {
            _lastError = "Dither exception: " + ex.Message;
            _logger.LogWarning(ex, "Live-stack dither crashed");
        } finally {
            g.Settled -= OnSettled;
            lock (_stateLock) _lastDitherFrame = info.FrameCount;
            _isExecuting = false;
            _executingKind = null;
            Notify();
        }
        return true;
    }

    private async Task SolveReferenceAsync(IImageData firstFrame) {
        var tempFits = Path.Combine(Path.GetTempPath(),
            $"nina_livestack_ref_{Guid.NewGuid():N}.fits");
        try {
            FITSWriter.Write(firstFrame, tempFits);
            // FIELD8-2: hand ASTAP the mount's pointing. A SearchRadiusDeg with
            // no RA/Dec beside it is not a narrow search, it is no search
            // constraint at all: the solver omits -ra/-spd and scans the whole
            // sky (field log, 2026-07-31: "Search radius: 180 degrees" on a
            // 0.50 degree field). Worse, the solver's own retry ladder is gated
            // on the call having had hints, so this one also skipped the blind
            // retry AND the coarse-downsample retry that rescue marginal
            // frames, and gave up after a single attempt. The mount knows
            // where it is pointing; the reference solve should use it.
            var scope = _equip.Telescope;
            double? hintRa = null, hintDec = null;
            if (scope != null && scope.IsConnected
                    && !double.IsNaN(scope.RightAscension) && !double.IsNaN(scope.Declination)) {
                hintRa = scope.RightAscension;
                hintDec = scope.Declination;
            }
            var result = await _solver.SolveAsync(tempFits, new PlateSolveOptions {
                HintRa = hintRa, HintDec = hintDec,
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

    /// <summary>Manual fire, bypasses all gates. Used by the UI
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

    /// <summary>Map sentinel doubles (NaN, ±Infinity, optionally 0)
    /// to null so the JSON serializer doesn't throw. System.Text.Json
    /// rejects NaN/Infinity by default and there's no JsonNumberHandling
    /// flag for "treat zero as not-set", easier to normalise here
    /// than to teach every consumer to ignore garbage values.</summary>
    private static double? SafeDouble(double v, bool zeroMeansUnset = false) {
        if (double.IsNaN(v) || double.IsInfinity(v)) return null;
        if (zeroMeansUnset && v == 0) return null;
        return v;
    }
    private static double? SafeDouble(double? v) {
        if (v is null) return null;
        return SafeDouble(v.Value);
    }
}

public class LiveStackTriggersStatus {
    public bool IsExecuting { get; init; }
    public string? ExecutingKind { get; init; }
    public DateTime? LastRefocusAt { get; init; }
    /// <summary>Null = no refocus has run yet this session.</summary>
    public int? LastRefocusFrame { get; init; }
    /// <summary>Null = unknown / not measured (was 0 or NaN internally).</summary>
    public double? LastRefocusHfr { get; init; }
    /// <summary>Null = unknown (camera doesn't report temperature, or
    /// no refocus has run yet).</summary>
    public double? LastRefocusTempC { get; init; }
    public DateTime? LastRecenterAt { get; init; }
    /// <summary>Null = no recenter has run yet this session.</summary>
    public int? LastRecenterFrame { get; init; }

    /// <summary>The frame a dither last ran on, or null. Null while dithers are
    /// only being SKIPPED, which is the distinction the gate turns on: a skip
    /// must leave this alone so the next frame tries again.</summary>
    public int? LastDitherFrame { get; init; }
    /// <summary>Null = unknown.</summary>
    public double? LastRecenterDriftArcsec { get; init; }
    public double? ReferenceRaHours { get; init; }
    public double? ReferenceDecDeg { get; init; }
    public bool ReferenceSolved { get; init; }
    public string? LastError { get; init; }
}