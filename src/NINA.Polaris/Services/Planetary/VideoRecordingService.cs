using NINA.Image.Interfaces;

namespace NINA.Polaris.Services.Planetary;

/// <summary>
/// Records the live camera stream to a SER file. Subscribes to
/// CameraStreamService frames and writes each one as it arrives.
///
/// File path convention: {ImageOutputDir}/planetary/{TargetName}/{ISO-timestamp}.ser
/// Auto-stops at MaxFrames or MaxDuration if either is configured.
/// Drops frames silently if the writer falls behind (logged at debug,
/// SER format can't tolerate gaps in the frame stream).
/// </summary>
public class VideoRecordingService : IDisposable {
    private readonly CameraStreamService _stream;
    private readonly EquipmentManager _equip;
    private readonly ProfileService _profiles;
    private readonly ILogger<VideoRecordingService> _logger;

    private readonly object _lock = new();
    private SerFileWriter? _writer;
    private IDisposable? _subscription;
    private RecordingConfig? _activeConfig;
    private DateTime _startedAt;
    private int _droppedFrames;
    private readonly object _writeLock = new();

    public bool IsRecording { get; private set; }
    public string? OutputPath => _writer?.Path;
    public int FrameCount => _writer?.FrameCount ?? 0;
    public long BytesWritten => _writer?.BytesWritten ?? 0;
    public TimeSpan Duration => IsRecording ? DateTime.UtcNow - _startedAt : TimeSpan.Zero;
    public int DroppedFrames => _droppedFrames;
    public string? LastError { get; private set; }

    public VideoRecordingService(CameraStreamService stream,
                                 EquipmentManager equip,
                                 ProfileService profiles,
                                 ILogger<VideoRecordingService> logger) {
        _stream = stream;
        _equip = equip;
        _profiles = profiles;
        _logger = logger;
    }

    public void Start(RecordingConfig cfg) {
        lock (_lock) {
            if (IsRecording)
                throw new InvalidOperationException("Recording already in progress, stop first");
            var cam = _equip.Camera
                ?? throw new InvalidOperationException("No camera connected");
            if (!_stream.IsRunning)
                throw new InvalidOperationException(
                    "Camera stream not running, start the stream first via /api/camera/stream/start");

            // Pick frame geometry from the camera's current state. SER's
            // header is locked at open time, so the user must not change
            // ROI/binning while recording.
            var w = cam.MaxX > 0 ? cam.MaxX : 1024;
            var h = cam.MaxY > 0 ? cam.MaxY : 1024;
            var bitDepth = cam.BitDepth > 0 ? cam.BitDepth : 16;
            var colorMode = cfg.ColorMode ?? SerColorMode.Mono;
            var target = SanitizeFolder(string.IsNullOrWhiteSpace(cfg.TargetName) ? "planet" : cfg.TargetName);
            var baseDir = Path.Combine(_profiles.Active.ImageOutputDir, "planetary", target);
            var path = Path.Combine(baseDir, $"{DateTime.UtcNow:yyyy-MM-ddTHH-mm-ss}.ser");

            _writer = new SerFileWriter(path, w, h, bitDepth, colorMode,
                observer: "Polaris",
                instrument: cam.DeviceName,
                telescope: _equip.Telescope?.DeviceName ?? "");
            _activeConfig = cfg;
            _startedAt = DateTime.UtcNow;
            _droppedFrames = 0;
            LastError = null;
            IsRecording = true;
            _subscription = _stream.SubscribeFrames(OnFrame);
            _logger.LogInformation("Recording started → {Path} ({W}×{H}×{Bits})", path, w, h, bitDepth);
        }
    }

    public Task StopAsync() {
        lock (_lock) {
            if (!IsRecording) return Task.CompletedTask;
            IsRecording = false;
            try { _subscription?.Dispose(); } catch { }
            _subscription = null;
            try { _writer?.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Writer dispose failed"); }
            var path = _writer?.Path;
            var frames = _writer?.FrameCount ?? 0;
            _writer = null;
            _activeConfig = null;
            _logger.LogInformation("Recording stopped: {Path} ({N} frames, {Dropped} dropped)",
                path, frames, _droppedFrames);
        }
        return Task.CompletedTask;
    }

    private void OnFrame(IImageData frame) {
        SerFileWriter? writer;
        RecordingConfig? cfg;
        lock (_lock) {
            writer = _writer;
            cfg = _activeConfig;
            if (writer == null || cfg == null) return;
        }

        // Auto-stop: max frames OR max duration.
        if (cfg.MaxFrames is int maxF && writer.FrameCount >= maxF) {
            _ = Task.Run(StopAsync);
            return;
        }
        if (cfg.MaxDuration is TimeSpan maxD && DateTime.UtcNow - _startedAt >= maxD) {
            _ = Task.Run(StopAsync);
            return;
        }

        // Single-threaded writer guard. If we can't get the lock fast
        // enough we drop this frame rather than block the stream loop.
        if (!Monitor.TryEnter(_writeLock, 5)) {
            Interlocked.Increment(ref _droppedFrames);
            return;
        }
        try {
            // SER expects a contiguous uint16 buffer when BitDepth=16.
            // imageData.Data is the right shape (ushort[]) for INDI / Alpaca
            // camera output, but if a vendor backend ever returns something
            // exotic we'd surface it as a writer ArgumentException.
            writer.WriteFrame(frame.Data, DateTime.UtcNow);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Frame write failed, dropping");
            LastError = ex.Message;
            Interlocked.Increment(ref _droppedFrames);
        } finally { Monitor.Exit(_writeLock); }
    }

    private static string SanitizeFolder(string s) {
        var bad = Path.GetInvalidFileNameChars();
        var chars = s.Select(c => bad.Contains(c) || c == ' ' ? '_' : c).ToArray();
        return new string(chars);
    }

    public void Dispose() {
        try { StopAsync().Wait(2000); } catch { }
    }
}

/// <summary>Recording configuration.</summary>
public record RecordingConfig(
    string TargetName,
    int? MaxFrames = null,
    TimeSpan? MaxDuration = null,
    SerColorMode? ColorMode = null);
