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

using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>
/// Continuous video feed from the active camera. Auto-picks between two
/// modes per camera capability:
///
/// - <b>Native</b>, when the camera implements CCD_VIDEO_STREAM (INDI
///   astronomy cams typically do). We just flip the driver switch and
///   subscribe to its continuous BLOB stream. Frame cadence is the
///   driver's choice (10-30 fps typical), no per-frame round-trip.
///
/// - <b>Loop</b>, universal fallback. Tight server-side capture loop
///   on the calling thread: while (running) { capture → relay }.
///   Works for every ICamera but is bounded by exposure + transfer time
///   (~1-5 fps for typical settings, faster on planetary cams with
///   sub-100 ms exposures).
///
/// Streamed frames are <i>ephemeral</i>, they go straight to
/// ImageRelayService (which broadcasts via /ws/image-stream) but bypass
/// FITS write, star detection, and stats. The PREVIEW canvas renders
/// them in real time; nothing else in the app sees them.
/// </summary>
public class CameraStreamService : IDisposable {
    private readonly EquipmentManager _equip;
    private readonly ImageRelayService _relay;
    private readonly ILogger<CameraStreamService> _logger;
    private readonly CaptureProgressService _captureProgress;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private IDisposable? _nativeSubscription;
    private readonly object _lock = new();
    private long _frameCount;
    private long _transmittedFrames;
    private DateTime _startedAt;
    private DateTime _lastFrameAt;
    // External listeners (VideoRecordingService, SlewPreviewService, etc.)
    // Fan-out is keyed by an integer handle so callers can Dispose safely
    // even after concurrent Stop / Restart.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Action<IImageData>> _externalSubs = new();
    private int _nextSubId;

    // Which FrameKind streamed JPEGs are tagged with (set per Start). Video
    // by default; SlewPreviewService starts the stream with SlewPreview.
    private FrameKind _broadcastKind = FrameKind.Video;

    public bool IsRunning { get; private set; }
    public string Mode { get; private set; } = "idle";      // "native" | "loop" | "idle"
    public double ExposureSeconds { get; private set; }
    public int Gain { get; private set; }
    public int BinX { get; private set; } = 1;
    public int BinY { get; private set; } = 1;
    public long FrameCount => Interlocked.Read(ref _frameCount);
    /// <summary>Cumulative count of downscaled-JPEG frames actually
    /// rendered + broadcast to clients. Exposed (alongside FrameCount) so
    /// callers can measure a windowed transmit rate by deltaing it over a
    /// time slice instead of relying on the since-start average.</summary>
    public long TransmittedFrames => Interlocked.Read(ref _transmittedFrames);
    public DateTime StartedAt => _startedAt;
    public DateTime LastFrameAt => _lastFrameAt;
    public string? LastError { get; private set; }

    // --- Diagnostics (surfaced via /api/camera/stream/status) so a slow
    // stream can be traced to its bottleneck without log-diving:
    //   - LastCaptureMs: how long the per-frame CaptureAsync took (loop
    //     mode only; 0 in native mode where the driver pushes BLOBs).
    //   - LastFrameWidth/Height + LastFrameRawBytes: the size of each
    //     frame. RAW streaming sends width*height*2 bytes/frame (before
    //     LZ4), so a full-frame OSC at BIN1 is tens of MB per frame ->
    //     bandwidth-bound. Shrink via ROI + binning.
    public double LastCaptureMs { get; private set; }
    public int LastFrameWidth { get; private set; }
    public int LastFrameHeight { get; private set; }
    public long LastFrameRawBytes { get; private set; }

    /// <summary>Capture FPS: frames produced by the camera (loop capture
    /// or native driver BLOBs) per second since the stream started.
    /// Returns 0 when not running.</summary>
    public double Fps {
        get {
            if (!IsRunning) return 0;
            var elapsed = (DateTime.UtcNow - _startedAt).TotalSeconds;
            return elapsed > 0 ? FrameCount / elapsed : 0;
        }
    }

    /// <summary>Transmission FPS: downscaled-JPEG frames actually rendered
    /// and broadcast to clients per second. Lower than <see cref="Fps"/>
    /// when the encode/link can't keep up (frames are dropped by the
    /// relay's in-flight guard) or 0 when no client is connected.</summary>
    public double TransmitFps {
        get {
            if (!IsRunning) return 0;
            var elapsed = (DateTime.UtcNow - _startedAt).TotalSeconds;
            return elapsed > 0 ? Interlocked.Read(ref _transmittedFrames) / elapsed : 0;
        }
    }

    public CameraStreamService(EquipmentManager equip,
                               ImageRelayService relay,
                               ILogger<CameraStreamService> logger,
                               CaptureProgressService captureProgress) {
        _equip = equip;
        _relay = relay;
        _logger = logger;
        _captureProgress = captureProgress;
    }

    /// <summary>
    /// Start a stream. Throws when no camera is connected or a stream
    /// is already running. <paramref name="forceLoop"/>=true skips the
    /// native path even when the camera supports it (useful for
    /// debugging the loop fallback).
    /// </summary>
    public void Start(StreamConfig cfg) {
        lock (_lock) {
            if (IsRunning) throw new InvalidOperationException("Stream already running, stop first");
            var cam = _equip.Camera ?? throw new InvalidOperationException("No camera connected");

            ExposureSeconds = cfg.ExposureSeconds <= 0 ? 0.1 : cfg.ExposureSeconds;
            Gain = cfg.Gain ?? cam.Gain;
            BinX = cfg.BinX ?? 1;
            BinY = cfg.BinY ?? 1;
            _broadcastKind = cfg.Kind;
            LastError = null;
            Interlocked.Exchange(ref _frameCount, 0);
            Interlocked.Exchange(ref _transmittedFrames, 0);
            _startedAt = DateTime.UtcNow;
            _lastFrameAt = _startedAt;
            _cts = new CancellationTokenSource();

            // PLAN8-2: the sharpness yardstick is only meaningful within one
            // stream. Exposure, gain, ROI and target all change what "sharp"
            // measures, and every one of them restarts the stream.
            ResetSharpness();

            var useNative = !cfg.ForceLoop && cam.Capabilities.SupportsVideoStream;
            if (useNative) StartNative(cam, _cts.Token);
            else StartLoop(cam, _cts.Token);

            IsRunning = true;
            _logger.LogInformation("Camera stream started in {Mode} mode (exp={Exp}s gain={Gain})",
                Mode, ExposureSeconds, Gain);
        }
    }

    public async Task StopAsync() {
        Task? loop;
        IDisposable? sub;
        ICamera? cam;
        lock (_lock) {
            if (!IsRunning) return;
            IsRunning = false;
            loop = _loopTask;
            sub = _nativeSubscription;
            _nativeSubscription = null;
            cam = _equip.Camera;
            _cts?.Cancel();
        }

        sub?.Dispose();
        if (cam != null && cam.IsStreaming) {
            try { await cam.StopVideoStreamAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "StopVideoStreamAsync failed (non-fatal)"); }
        }
        if (loop != null) {
            try { await loop; } catch { /* expected cancellation */ }
        }
        Mode = "idle";
        _logger.LogInformation("Camera stream stopped after {N} frames ({Fps:F1} fps avg)",
            FrameCount, FrameCount > 0 ? FrameCount / Math.Max(0.001, (DateTime.UtcNow - _startedAt).TotalSeconds) : 0);
    }

    /// <summary>
    /// Apply new exposure/gain to a RUNNING stream so the operator can tune the
    /// live view (the VIDEO controls stay enabled while streaming). In loop mode
    /// RunLoop reads these fields each frame, so the change takes effect on the
    /// next exposure. In native mode the driver was configured once at start, so
    /// the native stream is restarted with the new settings (a brief gap). No-op
    /// when not running.
    /// </summary>
    public async Task UpdateLiveAsync(double? exposureSeconds, int? gain) {
        bool restartNative;
        lock (_lock) {
            if (exposureSeconds is > 0) ExposureSeconds = exposureSeconds.Value;
            if (gain is >= 0) Gain = gain.Value;
            if (!IsRunning) return;
            restartNative = Mode == "native";
        }
        if (!restartNative) return;   // loop mode already honors the updated fields
        double exp; int g, bx, by; FrameKind kind;
        lock (_lock) { exp = ExposureSeconds; g = Gain; bx = BinX; by = BinY; kind = _broadcastKind; }

        // Prefer a live, in-place retune: stopping+restarting the native capture
        // just to change exposure can hang some SDKs (SVBony). If the backend
        // can apply it live, do that and skip the restart entirely.
        var cam = _equip.Camera;
        if (cam != null) {
            try {
                if (await cam.UpdateVideoStreamAsync(
                        new VideoStreamOptions(ExposureSeconds: exp, Gain: g, BinX: bx, BinY: by))) {
                    _logger.LogDebug("Native stream retuned live (exp={Exp}s gain={Gain})", exp, g);
                    return;
                }
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Live stream retune failed; falling back to restart");
            }
        }

        await StopAsync();
        Start(new StreamConfig(exp, g, bx, by, ForceLoop: false, Kind: kind));
    }

    private void StartNative(ICamera cam, CancellationToken ct) {
        Mode = "native";
        _nativeSubscription = cam.SubscribeVideoFrames(OnStreamFrame);
        // Fire-and-forget the driver toggle, driver-side cancellation
        // happens through StopVideoStreamAsync in StopAsync.
        _ = Task.Run(async () => {
            try {
                await cam.StartVideoStreamAsync(new VideoStreamOptions(
                    ExposureSeconds: ExposureSeconds, Gain: Gain, BinX: BinX, BinY: BinY), ct);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Native StartVideoStreamAsync failed");
                LastError = ex.Message;
                // Convert to loop mode as a graceful fallback so the user
                // still gets video, just slower.
                Mode = "loop";
                _loopTask = Task.Run(() => RunLoop(cam, ct));
                return;
            }

            // Some drivers (notably indi_asi_ccd) accept the
            // CCD_VIDEO_STREAM toggle, then fire BLOBs that aren't
            // actually parseable FITS, IndiCamera.OnBlobReceived
            // guards those out as "empty" so OnStreamFrame never gets
            // called. Symptom: stream "starts" but FrameCount stays
            // at 0 and the canvas stays black. Give native 2 seconds
            // to produce at least one usable frame; if not, drop
            // CCD_VIDEO_STREAM and switch to loop mode (per-exposure
            // CaptureAsync, which the ASI driver DOES service correctly).
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (OperationCanceledException) { return; }
            if (FrameCount == 0 && !ct.IsCancellationRequested) {
                _logger.LogWarning(
                    "Native CCD_VIDEO_STREAM produced no usable frames in 2s, falling back to loop mode");
                LastError = "Driver did not deliver parseable video stream BLOBs; using loop fallback.";
                try { await cam.StopVideoStreamAsync(CancellationToken.None); }
                catch (Exception ex) {
                    _logger.LogDebug(ex, "Stopping native stream during fallback failed (continuing)");
                }
                _nativeSubscription?.Dispose();
                _nativeSubscription = null;
                Mode = "loop";
                _loopTask = Task.Run(() => RunLoop(cam, ct));
            }
        }, ct);
    }

    private void StartLoop(ICamera cam, CancellationToken ct) {
        Mode = "loop";
        _loopTask = Task.Run(() => RunLoop(cam, ct));
    }

    private async Task RunLoop(ICamera cam, CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try {
                var opts = new CaptureOptions(
                    Gain: Gain != cam.Gain ? Gain : null,
                    BinX: BinX, BinY: BinY,
                    ImageType: "STREAM");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // Track the in-flight exposure so the LIVE/PREVIEW shutter
                // countdown is server-driven and survives a reconnect. Only
                // worth surfacing for real (>=1s) exposures; sub-second video
                // frames would just flicker the countdown.
                IImageData image;
                if (ExposureSeconds >= 1.0) {
                    using (_captureProgress.Begin("stream", ExposureSeconds))
                        image = await CameraCaptureGate.RunAsync(
                            () => cam.CaptureAsync(ExposureSeconds, opts, ct), ct);
                } else {
                    image = await CameraCaptureGate.RunAsync(
                        () => cam.CaptureAsync(ExposureSeconds, opts, ct), ct);
                }
                sw.Stop();
                LastCaptureMs = sw.Elapsed.TotalMilliseconds;
                OnStreamFrame(image);
            } catch (OperationCanceledException) { break; }
            catch (Exception ex) {
                _logger.LogDebug(ex, "Stream loop frame failed, backing off 200ms");
                LastError = ex.Message;
                try { await Task.Delay(200, ct); } catch { break; }
            }
        }
    }

    // ---- PLAN8-2: live sharpness (focus aid) ---------------------------
    //
    // Focusing a planet by eye on a small screen is guesswork, which is why
    // every planetary capture program shows a contrast number that peaks at
    // best focus. Polaris already computes exactly that for the stacker's
    // frame ranking (Laplacian variance); it just never ran while the operator
    // was actually turning the focuser.
    //
    // The metric is RELATIVE by nature: its absolute value depends on the
    // target, the exposure and the gain. It is meant to be maximised, so the
    // panel plots the trend and normalises against the best seen since the
    // stream started.
    private readonly object _sharpLock = new();
    private readonly Queue<double> _sharpHistory = new();
    private const int SharpHistoryLength = 120;   // ~a minute at 2 Hz sampling
    private DateTime _lastSharpAt = DateTime.MinValue;

    /// <summary>Newest first is NOT the convention here: oldest first, so the
    /// UI can draw it left to right without reversing.</summary>
    public double[] SharpnessHistory {
        get { lock (_sharpLock) return _sharpHistory.ToArray(); }
    }
    public double Sharpness { get; private set; }
    public double SharpnessBest { get; private set; }

    /// <summary>Percent of the last sampled frame at or near the sensor ceiling.
    /// The video preview auto-stretches every frame, so a blown highlight still
    /// looks correctly exposed on screen — this is the honest read of exposure.
    /// ~0 is good; a high value on a planetary target means gain/exposure is too
    /// high and detail is being clipped away before it reaches the SER.</summary>
    public double ClipPercent { get; private set; }

    private void UpdateSharpness(IImageData frame) {
        // Sampling twice a second is plenty for a human turning a knob, and it
        // keeps a 130 fps stream from spending every core on the metric: on a
        // 640x640 ROI one pass is cheap, on a full frame it is not.
        var now = DateTime.UtcNow;
        if ((now - _lastSharpAt).TotalMilliseconds < 500) return;
        _lastSharpAt = now;
        try {
            var px = frame.Data;
            if (px == null || px.Length == 0) return;
            int w = frame.Properties.Width, h = frame.Properties.Height;
            // Centre window: the planet is what is being focused, and the
            // corners are sky that only adds noise to the number.
            int roi = Math.Min(w, h) / 2;
            double v = NINA.Polaris.Services.Planetary.FrameQualityAnalyzer
                          .LaplacianVariance(px, w, h, roi);
            Sharpness = v;
            if (v > SharpnessBest) SharpnessBest = v;
            lock (_sharpLock) {
                _sharpHistory.Enqueue(v);
                while (_sharpHistory.Count > SharpHistoryLength) _sharpHistory.Dequeue();
            }
            // Clipping meter over the same frame: fraction of pixels at/near the
            // sensor ceiling. Stride-sampled (every 4th pixel) so a fast stream
            // doesn't pay for a full scan; ~98% of full scale counts as
            // effectively saturated. The ceiling follows the frame's bit depth so
            // an 8-bit stream measures against 255, a 16-bit one against 65535.
            int bits = frame.Properties.BitDepth;
            if (bits < 8 || bits > 16) bits = 16;
            int clipThreshold = (int)(((1 << bits) - 1) * 0.98);
            long clipped = 0, total = 0;
            for (int i = 0; i < px.Length; i += 4) { if (px[i] >= clipThreshold) clipped++; total++; }
            ClipPercent = total > 0 ? 100.0 * clipped / total : 0.0;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Sharpness metric failed for a stream frame");
        }
    }

    /// <summary>Forget the running maximum. The operator moved to another
    /// target or changed exposure/gain, so the old best is not a yardstick
    /// for anything any more.</summary>
    public void ResetSharpness() {
        Sharpness = 0;
        SharpnessBest = 0;
        ClipPercent = 0;
        lock (_sharpLock) _sharpHistory.Clear();
    }

    private void OnStreamFrame(IImageData frame) {
        Interlocked.Increment(ref _frameCount);
        _lastFrameAt = DateTime.UtcNow;
        // Record frame geometry + raw on-wire size for diagnostics. RAW
        // streaming ships width*height*2 bytes per frame before LZ4.
        var props = frame.Properties;
        LastFrameWidth = props.Width;
        LastFrameHeight = props.Height;
        LastFrameRawBytes = (long)(frame.Data?.Length ?? 0) * 2;
        UpdateSharpness(frame);
        try {
            // Efficient video path: relay a downscaled JPEG instead of
            // the full RAW buffer. RAW streaming ships W*H*2 bytes/frame
            // (tens of MB on a full-frame OSC) + per-frame LZ4 on the Pi
            // + client WebGL decode -- which capped the stream at <1 fps.
            // The video preview doesn't need pixel-perfect RAW; a small
            // auto-stretched JPEG (default maxDim 1280) is ~100-200 KB and
            // decodes instantly via the browser's headered-JPEG path
            // (routed to videoCaptureCanvas by FrameKind.Video). The
            // full-res RAW frame still reaches recording subscribers
            // below, so the SER recording stays raw + full resolution.
            // Fire-and-forget; RelayVideoJpegAsync drops frames if a
            // render is still in flight (bounded CPU/latency). Count the
            // ones actually transmitted so TransmitFps reflects the real
            // delivery rate (vs the capture rate above).
            _ = _relay.RelayVideoJpegAsync(frame, kind: _broadcastKind).ContinueWith(t => {
                if (t.IsCompletedSuccessfully && t.Result)
                    Interlocked.Increment(ref _transmittedFrames);
            }, TaskScheduler.Default);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Relay of stream frame failed");
        }
        // External fan-out to recording / slew-preview / etc. One bad
        // subscriber shouldn't kill the others or stall the stream.
        foreach (var sub in _externalSubs.Values) {
            try { sub(frame); }
            catch (Exception ex) { _logger.LogDebug(ex, "External stream subscriber threw"); }
        }
    }

    /// <summary>
    /// Subscribe to every stream frame. Dispose the returned handle to
    /// unsubscribe. Multiple subscribers coexist; the underlying camera
    /// stream lifecycle (start/stop) is NOT affected by subscription
    /// presence, callers must still trigger Start / StopAsync.
    /// </summary>
    public IDisposable SubscribeFrames(Action<IImageData> handler) {
        var id = Interlocked.Increment(ref _nextSubId);
        _externalSubs[id] = handler;
        return new FrameSub(this, id);
    }

    private sealed class FrameSub : IDisposable {
        private readonly CameraStreamService _svc;
        private readonly int _id;
        public FrameSub(CameraStreamService svc, int id) { _svc = svc; _id = id; }
        public void Dispose() => _svc._externalSubs.TryRemove(_id, out _);
    }

    public void Dispose() {
        try { StopAsync().Wait(2000); } catch { }
    }
}

/// <summary>Start-time configuration for a camera stream.</summary>
public record StreamConfig(
    double ExposureSeconds,
    int? Gain = null,
    int? BinX = null,
    int? BinY = null,
    bool ForceLoop = false,
    // Which canvas the broadcast JPEG frames are tagged for. VIDEO/PREVIEW
    // leave it Video (routes to videoCaptureCanvas); SlewPreviewService sets
    // SlewPreview so frames land on the SKY-tab inset canvas instead.
    FrameKind Kind = FrameKind.Video);