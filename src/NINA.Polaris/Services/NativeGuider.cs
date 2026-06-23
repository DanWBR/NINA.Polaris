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
public sealed partial class NativeGuider : IGuider, IDisposable {
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
    private CancellationTokenSource? _calCts;
    private Task? _loopTask;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Lock-position + calibration state (guarded by the loop being single).
    private double _lockX, _lockY;
    private bool _haveLock;
    // During calibration the star deliberately sweeps the frame and _lockX/_lockY
    // follow it (so the search window tracks the moving star). The displayed
    // crosshair, however, must stay pinned at the position where calibration
    // began -- the moving star is shown by its own marker circle. These hold that
    // fixed display anchor while _calAnchorActive is set.
    private double _calAnchorX, _calAnchorY;
    private bool _calAnchorActive;
    private GuideCalibration _calibration = GuideCalibration.Invalid;
    // Human-readable calibration step, surfaced to the GUIDE UI so the user
    // sees what's happening (ASIAIR-style "Dec (south) step 4, dist 12.3 px").
    private volatile string? _calProgress;
    // Snapshot of the last completed calibration (rates, angles, steps, plot
    // points) for the "Review Calibration" panel. Null until one completes.
    private object? _calDetails;

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
        // True when the source guide frame is a raw Bayer mosaic (colour guide
        // camera); the JPEG preview then collapses 2x2 quads to grayscale so it
        // doesn't render as a checkerboard.
        public bool IsBayered;
        // (x, y) in full-sensor coords, per-star SNR, primary flag, found flag.
        public List<(double x, double y, double snr, bool primary, bool found)> Stars = new();
        public long FrameId;
    }

    // Per-axis algorithms (rebuilt from profile on guiding start).
    private IGuideAlgorithm _raAlgo = new HysteresisAlgorithm();
    private IGuideAlgorithm _decAlgo = new ResistSwitchAlgorithm();
    private BacklashComp _backlashComp = new(0);
    // Timestamp of the previous guide frame, for the predictor's frame interval.
    private long _lastGuideMs;

    // Dither bookkeeping.
    private volatile bool _paused;
    private GuidingSettler? _settler;
    private double _settleThresholdPx = 1.5;
    private double _settleTimeSec = 10;
    private double _settleTimeoutSec = 40;
    // Live settle snapshot (written by the guide loop, read by the WS payload)
    // so the UI can show an ASIAIR-style "settling: err / tol, t / settleTime"
    // readout. _settleActive mirrors IsSettling for a lock-free read.
    private volatile bool _settleActive;
    private double _settleErrPx, _settleBelowSec, _settleElapsedSec;
    private int _starLostCount;
    private int _mountLostCount;

    public string Backend => "native";

    public bool IsConnected { get; private set; }
    public string AppState { get; private set; } = "Stopped";
    public bool IsGuiding => AppState == "Guiding";
    public bool IsCalibrating => AppState == "Calibrating";
    public string? CalibrationProgress => _calProgress;
    public object? CalibrationDetails => _calDetails;
    // Fall back to 1 s (not the 50 ms floor) when the rig has no stored
    // exposure yet (legacy rig / unset → 0). Reporting 50 ms made the GUIDE
    // panel's dropdown snap to its smallest option (0.1 s) and look like the
    // default was 0.1 s; 1 s is the sensible guiding default.
    public int ExposureMs => Rig.NativeGuideExposureMs > 0
        ? Math.Max(50, Rig.NativeGuideExposureMs)
        : 1000;
    public bool IsPaused => AppState == "Paused";
    public bool IsLooping => AppState == "Looping";
    public bool IsSettling { get; private set; }
    public double RaAggression => Rig.NativeRaAggression;
    public double DecAggression => Rig.NativeDecAggression;
    // True from the moment a dither offset is applied until the settle that
    // follows it completes. Distinct from IsSettling (which also covers the
    // post-start settle) so the UI can show a "Dithering" state and so we can
    // freeze RMS/error history while the star is deliberately being chased to
    // the new lock point (otherwise the dither delta inflates RMS).
    public bool IsDithering { get; private set; }

    /// <summary>Live settle telemetry (null unless settling) for the ASIAIR-style
    /// readout: current total error vs the tolerance, how long it has been within
    /// tolerance vs the required settle time, and elapsed vs timeout.</summary>
    public object? SettleProgress => _settleActive ? new {
        errorPx = _settleErrPx,
        thresholdPx = _settleThresholdPx,
        belowSec = _settleBelowSec,
        settleSec = _settleTimeSec,
        elapsedSec = _settleElapsedSec,
        timeoutSec = _settleTimeoutSec,
        dithering = IsDithering
    } : null;

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

    // Severity of the most recent alert ("info" | "warn" | "error"), surfaced
    // to the GUIDE callout so an informational message (e.g. "dark library
    // built") isn't styled like an error.
    public string LastAlertSeverity { get; private set; } = "warn";

    private void RaiseAlert(string msg, string severity = "warn") {
        LastAlert = msg;
        LastAlertAt = DateTime.UtcNow;
        LastAlertSeverity = severity;
        if (severity == "info") _logger.LogInformation("Native guider: {Msg}", msg);
        else _logger.LogWarning("Native guider alert: {Msg}", msg);
        Alert?.Invoke(msg);
    }

    /// <summary>Convenience for non-error notifications (styled as info, logged
    /// at Information level).</summary>
    private void RaiseInfo(string msg) => RaiseAlert(msg, "info");

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
        // Auto-restore the last saved calibration for this rig so a fresh session
        // can guide without recalibrating (PHD2-style restore).
        if (!_calibration.IsValid && TryRestoreCalibration())
            RaiseAlert("Restored last calibration for this rig. Recalibrate if the setup changed.");
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
        // Clear any stale alert from a previous session so the GUIDE banner
        // doesn't keep showing an error that no longer applies once a fresh
        // run begins. A genuine new failure below re-raises its own alert.
        LastAlert = null;
        LastAlertAt = null;
        // Guard the mount up-front: with a restored calibration we skip the
        // calibration step (which had its own check), so without this a
        // disconnected mount would let guiding "run" while every pulse is
        // silently dropped -- the star drifts uncorrected and RMS explodes.
        var startMount = _equipment.Telescope;
        if (startMount == null || !startMount.IsConnected || !startMount.Capabilities.SupportsPulseGuide) {
            RaiseAlert("Native guiding aborted: connect a pulse-guide-capable mount first.");
            SetAppState("Stopped");
            return;
        }
        if (recalibrate || !_calibration.IsValid) {
            // Run calibration under a cancellable token so the Stop button can
            // abort it (the request token alone isn't cancelled by /stop).
            _calCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try {
                await CalibrateAsync(_calCts.Token);
            } finally {
                _calCts.Dispose(); _calCts = null;
            }
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
        _settleTimeSec = settleTime;
        _settleTimeoutSec = settleTimeout;
        _settleActive = true;
        _settleErrPx = 0; _settleBelowSec = 0; _settleElapsedSec = 0;
        _settler = new GuidingSettler(settlePixels, settleTime, settleTimeout, NowMs());
        await StartLoopAsync(LoopMode.Guide);
    }

    public Task StopAsync(CancellationToken ct = default) {
        // Cancel an in-progress calibration too (it runs outside the loop CTS).
        try { _calCts?.Cancel(); } catch { }
        return StopLoopAsync();
    }

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
        IsDithering = true;
        LastSettleStatus = "settling";
        _settleThresholdPx = settlePixels;
        _settleTimeSec = settleTime;
        _settleTimeoutSec = settleTimeout;
        _settleActive = true;
        _settleErrPx = mag; _settleBelowSec = 0; _settleElapsedSec = 0;
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
        _calDetails = null;
        // Drop only the calibration for the CURRENT equipment signature, plus the
        // legacy single slot. Other rigs' / other equipment's saved calibrations
        // are left intact so they can still be restored later.
        var key = CalibrationKey();
        try {
            _profiles.UpdateEquipmentProfile(Rig.Id, r => {
                r.NativeCalibrations?.RemoveAll(c =>
                    string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
                r.NativeCalibration = null;
            });
        } catch (Exception ex) { _logger.LogWarning(ex, "Failed to clear persisted calibration"); }
        _logger.LogInformation("Native calibration cleared for {Key}", key);
        return Task.CompletedTask;
    }

    /// <summary>Equipment signature for the active rig: identifies the gear whose
    /// geometry/rates a calibration is valid for. Swapping any of these (guide
    /// camera, its driver, binning, guider focal length, mount, its driver)
    /// yields a different key, so a stale calibration is never reused and the
    /// matching one is restored when the original gear is refitted.</summary>
    private string CalibrationKey() {
        static string N(string? v) => (v ?? "").Trim().ToLowerInvariant();
        int focal = (int)Math.Round(Rig.GuiderFocalLengthMm);
        int bin = Math.Clamp(Rig.NativeGuideBin <= 0 ? 1 : Rig.NativeGuideBin, 1, 4);
        return $"cam={N(Rig.GuideCameraDriver)}:{N(Rig.GuideCamera)}"
             + $"|bin={bin}|fl={focal}"
             + $"|mount={N(Rig.TelescopeDriver)}:{N(Rig.Telescope)}";
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

}
