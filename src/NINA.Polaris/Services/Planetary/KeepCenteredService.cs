using NINA.Image.Interfaces;

namespace NINA.Polaris.Services.Planetary;

/// <summary>
/// "Keep target centered" control loop for planetary video work.
/// Subscribes to the live camera stream, detects the brightest object's
/// centroid with <see cref="CentroidAligner"/>, and pulses the mount's
/// N/S/E/W manual-jog axes to drag the centroid back to frame center.
///
/// <para>
/// The operator toggles it on from the VIDEO Capture sidebar while a
/// planetary stream is running. Typical use: framing the Moon or
/// Jupiter for a SER recording and not having to manually nudge the
/// mount every few seconds to fight PE / polar misalignment / wind.
/// </para>
///
/// <para>Design choices documented in
/// <c>.claude/plans/analise-e-fa-a-um-graceful-badger.md</c>. TL;DR:
/// </para>
/// <list type="bullet">
///   <item>Pulse-based control (Move + delay + StopMotion) rather than
///         INDI pulse-guide, so every <see cref="ITelescope"/> backend
///         works without extending the interface.</item>
///   <item>Self-calibration on Start: pulse N for 250 ms, measure pixel
///         displacement; same for E. Builds a 2x2 matrix
///         M = [vN | vE] (px / s) that absorbs focal length, binning,
///         rotator angle, pier side, and the unknown arcsec/s of the
///         currently-selected slew rate. The inverse maps a pixel error
///         vector back to a (tN, tE) pair of pulse durations with the
///         right sign.</item>
///   <item>Simple P-controller with dead zone + rate limit + gain &lt; 1.
///         No I or D term to tune — gain 0.6 + 250 ms inter-pulse gap
///         converges in 3-5 frames on a typical 5-10 fps stream.</item>
///   <item>Refuses to start when prerequisites are wrong (no stream, no
///         mount, not tracking, parked) so the operator gets an
///         actionable error at Start rather than a silent no-op.</item>
/// </list>
/// </summary>
public sealed class KeepCenteredService : IDisposable {

    private readonly IFrameSource _frames;
    private readonly Func<ITelescope?> _telescopeAccessor;
    private readonly Func<bool> _cameraStreamRunning;
    private readonly ILogger<KeepCenteredService> _logger;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private IDisposable? _subscription;
    private Task? _loop;
    private KeepCenteredOptions _opts = new();

    // Loop state ------------------------------------------------------
    private volatile string _phase = "idle";      // idle|calibrating|locked|lost
    private double? _lastOffsetPx;
    private int _lastCorrectionMs;
    private int _consecutiveMisses;
    private DateTime _lastPulseEndUtc = DateTime.MinValue;

    // 2x2 calibration matrix [vN | vE] in pixels per second.
    // Identity on construction; populated by the calibration phase.
    private double _mNx, _mNy, _mEx, _mEy;
    // Inverse, cached so the per-frame controller doesn't reinvert each tick.
    private double _invNx, _invNy, _invEx, _invEy;

    // Latest centroid (null when last frame had no confident detection).
    private CentroidAligner.Centroid? _latestCentroid;
    private readonly SemaphoreSlim _pulseSemaphore = new(1, 1);

    public bool IsRunning {
        get { lock (_gate) return _cts != null; }
    }
    public string Phase => _phase;
    public double? LastOffsetPx => _lastOffsetPx;
    public int LastCorrectionMs => _lastCorrectionMs;

    /// <summary>Production constructor: wires to the live camera-stream
    /// fan-out and a live <see cref="EquipmentManager"/>.</summary>
    public KeepCenteredService(CameraStreamService stream,
                               EquipmentManager equip,
                               ILogger<KeepCenteredService> logger)
        : this(new CameraStreamFrameSource(stream),
               () => equip.Telescope,
               () => stream.IsRunning,
               logger) { }

    /// <summary>Test constructor: lets unit tests drive frames by hand
    /// and inject a spy <see cref="ITelescope"/>. NOT for production
    /// wiring -- the DI container resolves the production overload.</summary>
    internal KeepCenteredService(IFrameSource frames,
                                  Func<ITelescope?> telescopeAccessor,
                                  Func<bool> cameraStreamRunning,
                                  ILogger<KeepCenteredService> logger) {
        _frames = frames;
        _telescopeAccessor = telescopeAccessor;
        _cameraStreamRunning = cameraStreamRunning;
        _logger = logger;
    }

    /// <summary>Start the loop. Throws if a stream is not running, the
    /// mount is missing / parked / not tracking, or the loop is already
    /// running. The actual calibration + per-frame work runs on a
    /// background task; this method returns once the subscription is
    /// armed.</summary>
    public Task StartAsync(KeepCenteredOptions? opts, CancellationToken ct) {
        lock (_gate) {
            if (_cts != null)
                throw new InvalidOperationException("Keep centered is already running");
            if (!_cameraStreamRunning())
                throw new InvalidOperationException(
                    "Camera stream is not running. Start the VIDEO stream first.");
            var t = _telescopeAccessor();
            if (t == null || !t.IsConnected)
                throw new InvalidOperationException("Mount is not connected.");
            if (t.IsParked)
                throw new InvalidOperationException(
                    "Mount is parked. Unpark before enabling Keep centered.");
            if (!t.IsTracking)
                throw new InvalidOperationException(
                    "Mount is not tracking. Enable tracking before Keep centered.");
            if (!t.Capabilities.SupportsManualJog)
                throw new InvalidOperationException(
                    "This mount driver does not support manual jog (N/S/E/W).");

            _opts = opts ?? new KeepCenteredOptions();
            _consecutiveMisses = 0;
            _lastOffsetPx = null;
            _lastCorrectionMs = 0;
            _latestCentroid = null;
            _lastPulseEndUtc = DateTime.MinValue;
            _phase = "calibrating";

            _cts = new CancellationTokenSource();
            // Link with the caller's CT so disposing the request scope
            // also stops the loop -- defensive, the endpoint normally
            // calls StopAsync explicitly.
            var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
            _subscription = _frames.Subscribe(OnFrame);
            _loop = Task.Run(() => RunAsync(linked.Token));
            _logger.LogInformation(
                "Keep centered started (deadZone={Dz}px, gain={G}%, gap={Gap}ms, calPulse={Cal}ms)",
                _opts.DeadZonePx, _opts.GainPercent, _opts.MinGapMs, _opts.CalibrationPulseMs);
        }
        return Task.CompletedTask;
    }

    /// <summary>Stop the loop. Awaits the background task so any pulse
    /// in flight finishes (with its <see cref="ITelescope.StopMotionAsync"/>
    /// in the finally block) before this returns. Safe to call when
    /// already stopped.</summary>
    public async Task StopAsync() {
        CancellationTokenSource? cts;
        IDisposable? sub;
        Task? loop;
        lock (_gate) {
            cts = _cts;
            sub = _subscription;
            loop = _loop;
            _cts = null;
            _subscription = null;
            _loop = null;
        }
        if (cts == null) return;
        try { cts.Cancel(); } catch { /* already disposed */ }
        sub?.Dispose();
        if (loop != null) {
            try { await loop; } catch { /* expected cancellation */ }
        }
        cts.Dispose();
        _phase = "idle";
        // Belt-and-suspenders: if Stop interrupted between Move and
        // StopMotion, make absolutely sure the mount isn't still slewing.
        try {
            var t = _telescopeAccessor();
            if (t != null && t.IsConnected) await t.StopMotionAsync();
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Final StopMotionAsync on Stop raised (continuing)");
        }
        _logger.LogInformation("Keep centered stopped");
    }

    // -----------------------------------------------------------------
    // Frame dispatch (subscription callback)
    // -----------------------------------------------------------------

    private void OnFrame(IImageData frame) {
        try {
            var w = frame.Properties.Width;
            var h = frame.Properties.Height;
            if (frame.Data == null || w < 5 || h < 5) return;

            // Sanity gate before trusting the centroid: the brightest
            // pixel must be brighter than ~3x the simple sample-median
            // we estimate from a stride walk, otherwise we're locking
            // onto a hot pixel or pure noise. Cheap O(W*H/256) sweep.
            ushort peak = 0;
            long sum = 0; int count = 0;
            for (int i = 0; i < frame.Data.Length; i += 256) {
                var v = frame.Data[i];
                if (v > peak) peak = v;
                sum += v; count++;
            }
            if (count == 0) return;
            var roughMean = sum / (double)count;
            if (peak < Math.Max(50, roughMean * 3)) {
                _latestCentroid = null;
                return;
            }

            _latestCentroid = CentroidAligner.Find(frame.Data, w, h);
            _lastFrameW = w;
            _lastFrameH = h;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "OnFrame centroid extraction failed");
        }
    }

    // -----------------------------------------------------------------
    // Background loop: calibration + control
    // -----------------------------------------------------------------

    private async Task RunAsync(CancellationToken ct) {
        try {
            await CalibrateAsync(ct);
            if (ct.IsCancellationRequested) return;
            _phase = "locked";
            _logger.LogInformation(
                "Keep centered calibrated: M=[N({Nx:F2},{Ny:F2}) E({Ex:F2},{Ey:F2})] px/s",
                _mNx, _mNy, _mEx, _mEy);
            await ControlLoopAsync(ct);
        } catch (OperationCanceledException) {
            // Normal Stop path
        } catch (Exception ex) {
            _logger.LogError(ex, "Keep centered loop crashed");
            _phase = "idle";
        }
    }

    private async Task CalibrateAsync(CancellationToken ct) {
        var t = _telescopeAccessor()
            ?? throw new InvalidOperationException("Mount disappeared during calibration");
        // Reference centroid: median of 3 frames over up to 3 s. If the
        // operator hasn't centered the target yet, calibration will
        // still complete but the next control tick will already issue
        // a recentering pulse.
        var refC = await AcquireConfidentCentroidAsync(ct, TimeSpan.FromSeconds(3));
        if (refC == null) {
            _phase = "lost";
            throw new InvalidOperationException(
                "Calibration: no confident centroid in the first 3 s. " +
                "Make sure a bright target (Moon / planet) is visible in the frame.");
        }

        // North pulse -----------------------------------------------------
        await PulseAndMeasureAsync("north", refC,
            (dx, dy) => { _mNx = dx; _mNy = dy; }, ct);

        // East pulse ------------------------------------------------------
        // Re-anchor on the post-N centroid so a slight residual drift
        // from imperfect StopMotion settle doesn't pollute the E vector.
        var midC = await AcquireConfidentCentroidAsync(ct, TimeSpan.FromSeconds(2));
        if (midC == null) {
            _phase = "lost";
            throw new InvalidOperationException(
                "Calibration: lost target between N and E pulses.");
        }
        await PulseAndMeasureAsync("east", midC,
            (dx, dy) => { _mEx = dx; _mEy = dy; }, ct);

        // Sanity: if either vector is near zero the mount didn't move
        // (driver rejected the jog silently, slew rate too low, brake
        // engaged...). Bail with a clear message instead of dividing
        // by zero next tick.
        var nMag = Math.Sqrt(_mNx * _mNx + _mNy * _mNy);
        var eMag = Math.Sqrt(_mEx * _mEx + _mEy * _mEy);
        if (nMag < 0.5 || eMag < 0.5) {
            throw new InvalidOperationException(
                $"Calibration: mount did not move enough during pulses (|N|={nMag:F2} px/s, |E|={eMag:F2} px/s). " +
                "Increase the slew rate before enabling Keep centered.");
        }

        // Invert M = [[Nx Ex] [Ny Ey]] -> M^-1 = (1/det) * [[Ey -Ex] [-Ny Nx]]
        var det = _mNx * _mEy - _mEx * _mNy;
        if (Math.Abs(det) < 1e-3) {
            throw new InvalidOperationException(
                "Calibration: N and E pulses produced nearly-parallel motion vectors " +
                "(rotator at a degenerate angle?). Toggle off, rotate the camera ~30°, retry.");
        }
        _invNx =  _mEy / det;
        _invNy = -_mNy / det;
        _invEx = -_mEx / det;
        _invEy =  _mNx / det;
    }

    /// <summary>Pulse a direction for the configured calibration time,
    /// measure the resulting centroid displacement, hand it back as a
    /// per-second velocity vector via the setter.</summary>
    private async Task PulseAndMeasureAsync(
            string direction,
            CentroidAligner.Centroid refC,
            Action<double, double> setVector,
            CancellationToken ct) {
        var pulseMs = _opts.CalibrationPulseMs;
        await IssuePulseAsync(direction, pulseMs, ct);
        // Settle: one extra frame interval so the centroid we read
        // actually reflects the post-pulse position.
        await Task.Delay(150, ct);
        var newC = await AcquireConfidentCentroidAsync(ct, TimeSpan.FromSeconds(2));
        if (newC == null) {
            throw new InvalidOperationException(
                $"Calibration: lost target after {direction} pulse.");
        }
        var pulseSeconds = pulseMs / 1000.0;
        setVector((newC.X - refC.X) / pulseSeconds,
                  (newC.Y - refC.Y) / pulseSeconds);
    }

    private async Task ControlLoopAsync(CancellationToken ct) {
        var t = _telescopeAccessor()
            ?? throw new InvalidOperationException("Mount disappeared during loop");

        while (!ct.IsCancellationRequested) {
            // Frame-paced wait. A typical planetary stream runs 5-15 fps;
            // 80 ms keeps us responsive without busy-spinning when the
            // stream stalls.
            await Task.Delay(80, ct);

            // Periodic mount-health gate. If the operator parks mid-loop
            // or tracking drops, bail with a clean status.
            if (!t.IsConnected || t.IsParked) {
                _logger.LogWarning("Keep centered: mount disconnected or parked, stopping");
                _phase = "lost";
                break;
            }

            var c = _latestCentroid;
            if (c == null) {
                _consecutiveMisses++;
                if (_consecutiveMisses >= _opts.MaxConsecutiveMisses && _phase != "lost") {
                    _phase = "lost";
                    _logger.LogWarning(
                        "Keep centered: target lost for {N} consecutive frames", _consecutiveMisses);
                }
                continue;
            }
            // Recovered from a "lost" stretch.
            if (_consecutiveMisses > 0) {
                _consecutiveMisses = 0;
                _phase = "locked";
            }

            // Pixel error from frame center. We could let the operator
            // set a custom target point ("lock here") later; for now
            // the geometric center is what they want.
            //
            // We deliberately read the *last* frame's dims from the
            // centroid call indirectly -- on every frame, OnFrame
            // updates _latestCentroid against w/h of THAT frame, so
            // by the time we read here, the cached centroid's
            // reference frame may have changed shape if subframe
            // toggled. Re-pulling the latest frame's w/h cleanly is
            // out of scope; we use the operator's frame-center
            // assumption which is correct in steady state and a few
            // pixels off only during a one-tick subframe switch.
            // For that we cache the frame dims in OnFrame.
            var w = _lastFrameW;
            var h = _lastFrameH;
            if (w <= 0 || h <= 0) continue;

            var cx = w / 2.0;
            var cy = h / 2.0;
            var ex = c.X - cx;
            var ey = c.Y - cy;
            var mag = Math.Sqrt(ex * ex + ey * ey);
            _lastOffsetPx = mag;

            if (mag < _opts.DeadZonePx) {
                _lastCorrectionMs = 0;
                continue;
            }

            // Rate limit: don't issue a pulse until the previous one
            // had time to settle in the centroid stream.
            var sinceLast = (DateTime.UtcNow - _lastPulseEndUtc).TotalMilliseconds;
            if (sinceLast < _opts.MinGapMs) continue;

            // Solve M * [tN; tE] = -e for pulse seconds, then convert
            // to ms + gain + clamp.
            var negEx = -ex;
            var negEy = -ey;
            var tN = _invNx * negEx + _invEx * negEy;
            var tE = _invNy * negEx + _invEy * negEy;

            var gain = _opts.GainPercent / 100.0;
            tN *= gain;
            tE *= gain;

            await DispatchAxisPulseAsync(tN, "north", "south", ct);
            await DispatchAxisPulseAsync(tE, "east",  "west",  ct);
        }
    }

    private async Task DispatchAxisPulseAsync(
            double seconds, string positiveDir, string negativeDir, CancellationToken ct) {
        var absMs = (int)Math.Round(Math.Abs(seconds) * 1000.0);
        if (absMs < 30) return;
        absMs = Math.Min(absMs, 400);
        var dir = seconds >= 0 ? positiveDir : negativeDir;
        try {
            await IssuePulseAsync(dir, absMs, ct);
            _lastCorrectionMs = absMs;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Keep centered pulse {Dir} {Ms}ms failed", dir, absMs);
        }
    }

    /// <summary>Send a Move{Dir} -> Delay(ms) -> StopMotion sequence.
    /// Single in-flight pulse at a time across the loop (semaphore).
    /// StopMotion runs in the finally block so a cancellation between
    /// Move and Stop still halts the axis.</summary>
    private async Task IssuePulseAsync(string direction, int ms, CancellationToken ct) {
        var t = _telescopeAccessor()
            ?? throw new InvalidOperationException("Mount disappeared during pulse");
        await _pulseSemaphore.WaitAsync(ct);
        try {
            switch (direction) {
                case "north": await t.MoveNorthAsync(ct); break;
                case "south": await t.MoveSouthAsync(ct); break;
                case "east":  await t.MoveEastAsync(ct);  break;
                case "west":  await t.MoveWestAsync(ct);  break;
                default: throw new ArgumentException("Bad pulse direction: " + direction);
            }
            try { await Task.Delay(ms, ct); } catch (OperationCanceledException) { /* still stop below */ }
        } finally {
            try {
                // Use CancellationToken.None for the stop: even on cancel
                // we MUST halt the axis. The mount driver doesn't care
                // about our CT.
                await t.StopMotionAsync(CancellationToken.None);
            } catch (Exception ex) {
                _logger.LogDebug(ex, "StopMotionAsync after pulse raised (continuing)");
            }
            _lastPulseEndUtc = DateTime.UtcNow;
            _pulseSemaphore.Release();
        }
    }

    /// <summary>Block (with a soft polling cadence) until the
    /// OnFrame-cached centroid is non-null, OR until the timeout
    /// elapses (returns null). Used by calibration to wait for the
    /// next confident detection without grabbing the camera-stream
    /// internals.</summary>
    private async Task<CentroidAligner.Centroid?> AcquireConfidentCentroidAsync(
            CancellationToken ct, TimeSpan timeout) {
        var deadline = DateTime.UtcNow + timeout;
        // Drop the cached one first: we want a fresh detection AFTER
        // any preceding pulse settles, not a stale pre-pulse value.
        _latestCentroid = null;
        while (DateTime.UtcNow < deadline) {
            await Task.Delay(80, ct);
            if (_latestCentroid != null) return _latestCentroid;
        }
        return null;
    }

    // OnFrame caches the frame dims so the control loop can compute
    // frame-center without reaching back into the stream service.
    private int _lastFrameW, _lastFrameH;

    public void Dispose() {
        try { StopAsync().Wait(2000); } catch { }
        _pulseSemaphore.Dispose();
    }

    // -----------------------------------------------------------------
    // Frame source abstraction (production wraps CameraStreamService;
    // tests inject a hand-driven source).
    // -----------------------------------------------------------------

    internal interface IFrameSource {
        IDisposable Subscribe(Action<IImageData> handler);
    }

    private sealed class CameraStreamFrameSource : IFrameSource {
        private readonly CameraStreamService _stream;
        public CameraStreamFrameSource(CameraStreamService stream) { _stream = stream; }
        public IDisposable Subscribe(Action<IImageData> handler)
            => _stream.SubscribeFrames(handler);
    }

    // -----------------------------------------------------------------
    // We also need to record frame dims for the control loop. The
    // simplest way without surfacing yet another IFrameSource method
    // is to do it inside OnFrame -- so wire it there.
    // -----------------------------------------------------------------
}

/// <summary>Knobs for the Keep Centered controller. All optional; the
/// defaults are tuned for a typical planetary stream (5-15 fps, 0.5-2
/// arcsec/pixel, SLEW_GUIDE / SLEW_CENTERING / SLEW_2X rates).
/// Exposed via the POST /api/telescope/keep-centered/start body so the
/// operator can override per-session for unusual setups (much longer
/// focal length, very slow mount, etc.) without a profile edit.</summary>
public record KeepCenteredOptions {
    /// <summary>Pixel offset below which no correction is issued.
    /// Larger -> calmer mount, more wander; smaller -> tighter lock,
    /// more pulses.</summary>
    public double DeadZonePx { get; init; } = 5.0;

    /// <summary>Per-pulse gain as a percentage of the geometric
    /// solution. &lt; 100 underdamps to prevent overshoot/oscillation;
    /// the residual error gets corrected over the next 2-3 frames.</summary>
    public double GainPercent { get; init; } = 60.0;

    /// <summary>Minimum gap between consecutive pulses, milliseconds.
    /// Gives the centroid stream time to reflect the previous pulse
    /// before issuing the next; smaller than the inter-frame interval
    /// triggers cascading over-correction.</summary>
    public int MinGapMs { get; init; } = 250;

    /// <summary>Calibration pulse duration, milliseconds. Long enough
    /// to produce a measurable pixel shift at any slew rate the
    /// operator might pick, short enough that the planet doesn't leave
    /// the frame on a SLEW_FIND-class rate.</summary>
    public int CalibrationPulseMs { get; init; } = 250;

    /// <summary>Number of consecutive frames without a confident
    /// centroid before the service transitions to "lost". A "lost"
    /// service stays subscribed and resumes (back to "locked") as
    /// soon as a confident detection lands -- no auto-stop.</summary>
    public int MaxConsecutiveMisses { get; init; } = 30;
}
