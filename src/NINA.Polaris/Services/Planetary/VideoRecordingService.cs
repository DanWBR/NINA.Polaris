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

using System.Threading.Channels;
using NINA.Core.Enum;
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

    // #362: background writer queue. OnFrame (on the camera stream's
    // delivery thread) only enqueues a frame reference into a bounded
    // channel; a dedicated writer task drains it and does the ushort->byte
    // LE encode into a single reused scratch buffer + the SER write. This
    // keeps the capture/delivery thread free (one TryWrite, no copy, no
    // alloc) and isolates disk write-back stalls to the writer thread, so a
    // hiccup only costs queued slots instead of dropping on the hot path.
    private Channel<QueueItem>? _channel;
    private Task? _writerTask;
    private long _enqueued;

    private readonly record struct QueueItem(ushort[] Pixels, int ByteLen,
        int Width, int Height, SerColorMode Color, DateTime Utc);

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

            // SER geometry is taken from the FIRST streamed frame, not from the
            // camera's MaxX/MaxY/BitDepth. With an ROI/binning set for high-fps
            // video (the whole point of planetary capture) the streamed frames
            // are smaller than the full sensor, and the streamed buffer is
            // always a 16-bit ushort[] regardless of the camera's 8/16-bit
            // readout. Sizing the writer from the camera's full-frame/native
            // bit-depth state made every frame fail the size check in the writer
            // loop -> all frames dropped, empty file (the field-test symptom on
            // both the native SVBony and INDI drivers). The writer is opened
            // lazily in WriterLoopAsync from the real frame size; here we only
            // settle the output path + config.
            var target = SanitizeFolder(string.IsNullOrWhiteSpace(cfg.TargetName) ? "planet" : cfg.TargetName);
            var baseDir = Path.Combine(_profiles.Active.ImageOutputDir, "planetary", target);
            var path = Path.Combine(baseDir, $"{DateTime.UtcNow:yyyy-MM-ddTHH-mm-ss}.ser");
            var instrument = cam.DeviceName;
            var telescope = _equip.Telescope?.DeviceName ?? "";
            _activeConfig = cfg;
            _startedAt = DateTime.UtcNow;
            _droppedFrames = 0;
            _enqueued = 0;
            LastError = null;
            IsRecording = true;

            // Bounded queue: when full, TryWrite returns false and the
            // frame is dropped (counted) rather than blocking the camera
            // stream. 32 frames of headroom absorbs disk write-back stalls;
            // at 640×480×16-bit that's ~19 MB of buffering.
            _channel = Channel.CreateBounded<QueueItem>(new BoundedChannelOptions(32) {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
            var reader = _channel.Reader;
            _writerTask = Task.Run(() => WriterLoopAsync(path, instrument, telescope, reader));

            _subscription = _stream.SubscribeFrames(OnFrame);
            _logger.LogInformation("Recording started → {Path} (geometry locked on first frame)", path);
        }
    }

    public async Task StopAsync() {
        SerFileWriter? writer = null;
        IDisposable? sub;
        Channel<QueueItem>? channel;
        Task? writerTask;
        lock (_lock) {
            if (!IsRecording) return;
            IsRecording = false;
            sub = _subscription; _subscription = null;
            channel = _channel; _channel = null;
            writerTask = _writerTask; _writerTask = null;
            _activeConfig = null;
        }

        // Unsubscribe first so no more frames are enqueued, then signal the
        // writer loop to drain the queue and exit, then close the SER file
        // (header frame-count patch + timestamp trailer happen on Dispose).
        try { sub?.Dispose(); } catch { }
        channel?.Writer.TryComplete();
        if (writerTask != null) { try { await writerTask; } catch { } }

        // Capture the writer AFTER the loop finished: it's opened lazily on the
        // first frame, so reading it before the drain could miss an instance
        // created while a queued frame was still in flight (unfinalised SER).
        lock (_lock) { writer = _writer; _writer = null; }

        var path = writer?.Path;
        var frames = writer?.FrameCount ?? 0;
        try { writer?.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Writer dispose failed"); }
        _logger.LogInformation("Recording stopped: {Path} ({N} frames, {Dropped} dropped)",
            path, frames, _droppedFrames);
    }

    /// <summary>Background drain: ushort->byte LE encode into a single
    /// reused scratch buffer + SER write, off the camera delivery thread.
    /// Runs until the channel is completed (StopAsync) and fully drained.</summary>
    private async Task WriterLoopAsync(string path, string instrument, string telescope,
                                       ChannelReader<QueueItem> reader) {
        SerFileWriter? writer = null;
        byte[]? scratch = null;
        try {
            await foreach (var item in reader.ReadAllAsync()) {
                try {
                    // Open the SER lazily on the first frame, sized from the
                    // ACTUAL streamed geometry: always 16-bit, single plane (the
                    // stream delivers a ushort[] mosaic). This is what makes
                    // recording robust to ROI/binning and 8/16-bit cameras —
                    // sizing from cam.MaxX/MaxY/BitDepth dropped every frame.
                    if (writer == null) {
                        if (item.Width <= 0 || item.Height <= 0
                                || item.ByteLen != (long)item.Width * item.Height * 2) {
                            Interlocked.Increment(ref _droppedFrames);
                            continue;
                        }
                        var colorMode = _activeConfig?.ColorMode ?? item.Color;
                        writer = new SerFileWriter(path, item.Width, item.Height, 16, colorMode,
                            observer: "Polaris", instrument: instrument, telescope: telescope);
                        scratch = new byte[writer.BytesPerFrame];
                        lock (_lock) { _writer = writer; }
                        _logger.LogInformation("Recording geometry locked → {W}×{H}×16 ({Color})",
                            item.Width, item.Height, colorMode);
                    }
                    // Header geometry is now fixed; a frame whose size changed
                    // mid-recording (e.g. ROI edit) can't be appended.
                    if (item.ByteLen != scratch!.Length) {
                        Interlocked.Increment(ref _droppedFrames);
                        continue;
                    }
                    Buffer.BlockCopy(item.Pixels, 0, scratch, 0, item.ByteLen);
                    writer.WriteFrame(scratch, item.ByteLen, item.Utc);
                } catch (Exception ex) {
                    _logger.LogDebug(ex, "Frame write failed, dropping");
                    LastError = ex.Message;
                    Interlocked.Increment(ref _droppedFrames);
                }
            }
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Recording writer loop terminated unexpectedly");
            LastError = ex.Message;
        }
    }

    private static SerColorMode MapBayerToSer(BayerPatternEnum p) => p switch {
        BayerPatternEnum.RGGB => SerColorMode.BayerRGGB,
        BayerPatternEnum.BGGR => SerColorMode.BayerBGGR,
        BayerPatternEnum.GRBG => SerColorMode.BayerGRBG,
        BayerPatternEnum.GBRG => SerColorMode.BayerGBRG,
        _ => SerColorMode.Mono
    };

    private void OnFrame(IImageData frame) {
        RecordingConfig? cfg;
        Channel<QueueItem>? channel;
        lock (_lock) {
            cfg = _activeConfig;
            channel = _channel;
            if (channel == null || cfg == null) return;
        }

        // Auto-stop: max frames (counted by frames enqueued, not yet
        // written) OR max duration. Either way we stop accepting frames.
        if (cfg.MaxFrames is int maxF && Interlocked.Read(ref _enqueued) >= maxF) {
            _ = Task.Run(StopAsync);
            return;
        }
        if (cfg.MaxDuration is TimeSpan maxD && DateTime.UtcNow - _startedAt >= maxD) {
            _ = Task.Run(StopAsync);
            return;
        }

        // #362: hand the frame to the background writer queue. This is just
        // a reference enqueue (no copy, no alloc, no disk I/O) on the camera
        // delivery thread. The frame's ushort[] is freshly decoded per frame
        // by every backend (INDI BLOB / Alpaca / native capture), so holding
        // the reference until the writer drains it is safe. TryWrite returns
        // false only when the bounded queue is full (disk can't keep up),
        // in which case we drop this frame instead of blocking capture.
        var props = frame.Properties;
        var color = cfg.ColorMode
            ?? (props.IsBayered ? MapBayerToSer(props.BayerPattern) : SerColorMode.Mono);
        var item = new QueueItem(frame.Data, frame.Data.Length * 2,
            props.Width, props.Height, color, DateTime.UtcNow);
        if (channel.Writer.TryWrite(item)) {
            Interlocked.Increment(ref _enqueued);
        } else {
            Interlocked.Increment(ref _droppedFrames);
        }
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