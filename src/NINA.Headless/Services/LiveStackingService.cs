using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Headless.Services;

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

    public bool IsRunning => _isRunning;
    public int FrameCount => _frameCount;
    public int Width => _width;
    public int Height => _height;

    public LiveStackingService(ImageRelayService relay, ILogger<LiveStackingService> logger) {
        _relay = relay;
        _logger = logger;
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

        _logger.LogInformation("Live stack: processing frame {N} ({W}x{H})",
            _frameCount + 1, props.Width, props.Height);

        // Detect stars in the new frame
        var stars = _detector.Detect(data, props.Width, props.Height);
        _logger.LogDebug("Detected {Count} stars in frame", stars.Count);

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

        _logger.LogInformation("Live stack: frame {N} added, {Stars} stars, relayed to clients",
            _frameCount, stars.Count);
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
            ReferenceStarCount = _referenceStars?.Count ?? 0
        };
    }

    public class StackStatus {
        public bool IsRunning { get; set; }
        public int FrameCount { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int ReferenceStarCount { get; set; }
    }
}
