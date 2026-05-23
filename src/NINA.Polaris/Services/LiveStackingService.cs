using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>Async handler invoked once per integrated frame. Handlers
/// run sequentially inside the caller's await chain — a long-running
/// handler (e.g. an auto-focus run) naturally pauses the next capture
/// because the caller is awaiting AddFrameAsync. This is the
/// LiveStackTriggersService integration point (LSTR-1).</summary>
public delegate Task LiveStackFrameHandler(LiveStackFrameInfo info);

public record LiveStackFrameInfo(
    int FrameCount,        // count AFTER this integration
    IImageData Frame,      // the raw frame integrated (not the running stack)
    double MedianHfr,      // median HFR of stars detected in this frame
    int StarCount,
    DateTime At);

/// <summary>
/// Where the per-frame stacking math runs.
/// <list type="bullet">
/// <item><b>Full</b> (default): the server runs the whole pipeline —
/// StarDetector + StarMatcher + AffineTransform + ImageResampler +
/// running-mean accumulator. Server holds the accumulated stack and
/// pushes it as the live preview. This is the historical behaviour
/// and stays the safe fallback.</item>
/// <item><b>MetricsOnly</b>: the server still runs StarDetector (so
/// the trigger orchestrator gets HFR/star count + the reference solve
/// on frame 1 still happens), but skips matching/warping/accumulating.
/// The raw frame is still relayed to clients via ImageRelayService;
/// a client-side WASM module is expected to do the actual stacking
/// and render its own preview. Used by the CLST offloading work —
/// see plan file.</item>
/// </list>
/// </summary>
public enum StackMode {
    Full,
    MetricsOnly
}

public class LiveStackingService {
    private readonly ImageRelayService _relay;
    private readonly ILogger<LiveStackingService> _logger;
    private readonly StarDetector _detector = new() { MaxStars = 200 };
    private readonly object _lock = new();

    private float[]? _stackBuffer;
    private int[]? _countBuffer;
    private int _width;
    private int _height;
    private int _frameCount;
    private List<DetectedStar>? _referenceStars;
    private bool _isRunning;

    // Frame-integrated subscribers (LSTR-1). Append-only list guarded
    // by _handlersLock for snapshotting; handlers awaited sequentially
    // inside AddFrameAsync so a slow handler (AF run, recenter) blocks
    // the caller and naturally pauses the next capture.
    private readonly List<LiveStackFrameHandler> _frameHandlers = new();
    private readonly object _handlersLock = new();

    public bool IsRunning => _isRunning;
    public int FrameCount => _frameCount;
    public int Width => _width;
    public int Height => _height;
    public double LastFrameMedianHfr { get; private set; }
    public int LastFrameStarCount { get; private set; }

    /// <summary>Where the per-frame math runs. Default <see cref="StackMode.Full"/>.
    /// Switched to <see cref="StackMode.MetricsOnly"/> by the WASM
    /// handshake (CLST-5) when a WASM-capable client is connected and
    /// the active rig hasn't forced server-side.</summary>
    public StackMode Mode { get; set; } = StackMode.Full;

    public LiveStackingService(ImageRelayService relay, ILogger<LiveStackingService> logger) {
        _relay = relay;
        _logger = logger;
    }

    /// <summary>Subscribe to per-frame integration events. Handlers
    /// are awaited sequentially inside <see cref="AddFrameAsync"/>;
    /// a slow handler pauses the upstream capture loop. Returns an
    /// IDisposable that removes the subscription.</summary>
    public IDisposable SubscribeFrameIntegrated(LiveStackFrameHandler handler) {
        lock (_handlersLock) _frameHandlers.Add(handler);
        return new HandlerSub(this, handler);
    }

    private sealed class HandlerSub : IDisposable {
        private readonly LiveStackingService _svc;
        private readonly LiveStackFrameHandler _h;
        public HandlerSub(LiveStackingService svc, LiveStackFrameHandler h) { _svc = svc; _h = h; }
        public void Dispose() {
            lock (_svc._handlersLock) _svc._frameHandlers.Remove(_h);
        }
    }

    public void Reset() {
        lock (_lock) {
            _stackBuffer = null;
            _countBuffer = null;
            _referenceStars = null;
            _frameCount = 0;
            _width = 0;
            _height = 0;
            _isRunning = false;
            LastFrameMedianHfr = 0;
            LastFrameStarCount = 0;
            _logger.LogInformation("Live stacking reset");
        }
    }

    public void Start() {
        Reset();
        _isRunning = true;
        _logger.LogInformation("Live stacking started");
    }

    public void Stop() {
        _isRunning = false;
        _logger.LogInformation("Live stacking stopped after {Count} frames", _frameCount);
    }

    public async Task AddFrameAsync(IImageData imageData, CancellationToken ct = default) {
        if (!_isRunning) return;

        var props = imageData.Properties;
        var data = imageData.Data;

        var mode = Mode;
        _logger.LogInformation("Live stack: processing frame {N} ({W}x{H}) — mode={Mode}",
            _frameCount + 1, props.Width, props.Height, mode);

        // StarDetector runs in BOTH modes:
        //   - Full: feeds StarMatcher for alignment + provides HFR
        //   - MetricsOnly: trigger orchestrator (LSTR-3) needs HFR +
        //     star count even when stacking happens client-side
        var stars = _detector.Detect(data, props.Width, props.Height);
        _logger.LogDebug("Detected {Count} stars in frame", stars.Count);

        if (mode == StackMode.Full) {
            ushort[] alignedData;

            lock (_lock) {
                if (_frameCount == 0) {
                    // First frame: initialize buffers and set as reference
                    _width = props.Width;
                    _height = props.Height;
                    int pixelCount = _width * _height;
                    _stackBuffer = new float[pixelCount];
                    _countBuffer = new int[pixelCount];
                    _referenceStars = stars;
                    alignedData = data;
                } else {
                    if (props.Width != _width || props.Height != _height) {
                        _logger.LogWarning("Frame size mismatch: {W}x{H} vs {ExpW}x{ExpH}",
                            props.Width, props.Height, _width, _height);
                        return;
                    }

                    // Align to reference
                    var transform = StarMatcher.Match(_referenceStars!, stars);
                    if (transform == null) {
                        _logger.LogWarning("Alignment failed for frame {N}, skipping", _frameCount + 1);
                        return;
                    }

                    alignedData = ImageResampler.ApplyTransform(data, _width, _height, transform);
                    _logger.LogDebug("Frame aligned: dx={Tx:F1} dy={Ty:F1}", transform.Tx, transform.Ty);
                }

                // Accumulate into stack buffer (running average)
                for (int i = 0; i < alignedData.Length && i < _stackBuffer!.Length; i++) {
                    if (alignedData[i] > 0) {
                        _stackBuffer[i] += alignedData[i];
                        _countBuffer![i]++;
                    }
                }

                _frameCount++;
            }

            // Generate stacked result and relay to clients
            var stackedPixels = GetStackedResult();
            var stackedProps = new ImageProperties {
                Width = _width,
                Height = _height,
                BitDepth = props.BitDepth,
                IsBayered = props.IsBayered,
                BayerPattern = props.BayerPattern
            };

            var stackedImage = new BaseImageData(stackedPixels, stackedProps, imageData.MetaData);
            await _relay.RelayImageAsync(stackedImage, ct);
        } else {
            // MetricsOnly: bookkeep frame count + dimensions so triggers
            // and status broadcasts have something to render, but skip
            // the accumulator. The raw frame is still relayed via
            // ImageRelayService elsewhere in the capture path (see
            // SequenceEngine / ImageRelayService.RelayImageAsync from
            // the camera capture endpoint) — the WASM client picks it
            // up from the existing /ws/image-stream raw mode.
            lock (_lock) {
                if (_frameCount == 0) {
                    _width = props.Width;
                    _height = props.Height;
                    _referenceStars = stars;
                }
                _frameCount++;
            }
        }

        // Compute median HFR from the already-detected stars (no extra
        // pixel pass). Falls back to 0 when no stars — handlers that
        // care about HFR should treat 0 as "no data this frame".
        // Computed in BOTH modes so trigger orchestrator (auto-AF based
        // on HFR degradation) still works in MetricsOnly mode.
        double medianHfr = 0;
        if (stars.Count > 0) {
            var sorted = stars.Select(s => s.HFR).Where(h => h > 0).OrderBy(h => h).ToList();
            if (sorted.Count > 0) medianHfr = sorted[sorted.Count / 2];
        }
        LastFrameMedianHfr = medianHfr;
        LastFrameStarCount = stars.Count;

        _logger.LogInformation("Live stack: frame {N} added, {Stars} stars (HFR={Hfr:F2}), mode={Mode}",
            _frameCount, stars.Count, medianHfr, mode);

        // Snapshot handlers + await sequentially. Any handler that
        // throws is logged + swallowed — one bad subscriber can't
        // poison the chain. Slow handlers (AF, recenter) pause the
        // upstream capture loop by extending this await.
        LiveStackFrameHandler[] handlers;
        lock (_handlersLock) handlers = _frameHandlers.ToArray();
        if (handlers.Length > 0) {
            var info = new LiveStackFrameInfo(_frameCount, imageData, medianHfr, stars.Count, DateTime.UtcNow);
            foreach (var h in handlers) {
                try { await h(info); }
                catch (Exception ex) {
                    _logger.LogWarning(ex, "LiveStack frame handler threw (continuing)");
                }
            }
        }
    }

    public ushort[] GetStackedResult() {
        lock (_lock) {
            if (_stackBuffer == null) return [];

            var result = new ushort[_stackBuffer.Length];
            for (int i = 0; i < _stackBuffer.Length; i++) {
                if (_countBuffer![i] > 0) {
                    result[i] = (ushort)Math.Clamp(_stackBuffer[i] / _countBuffer[i], 0, 65535);
                }
            }
            return result;
        }
    }

    public StackStatus GetStatus() {
        return new StackStatus {
            IsRunning = _isRunning,
            FrameCount = _frameCount,
            Width = _width,
            Height = _height,
            ReferenceStarCount = _referenceStars?.Count ?? 0,
            Mode = Mode.ToString().ToLowerInvariant()
        };
    }

    public class StackStatus {
        public bool IsRunning { get; set; }
        public int FrameCount { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int ReferenceStarCount { get; set; }
        /// <summary>"full" or "metricsonly". UI uses this for the
        /// compute-location chip + the "Save current stack" button
        /// gating (only meaningful when a WASM client is actually
        /// doing the accumulation).</summary>
        public string Mode { get; set; } = "full";
    }
}
