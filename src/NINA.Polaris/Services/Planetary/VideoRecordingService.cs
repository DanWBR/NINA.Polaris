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

using System.Collections.Concurrent;
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
    // collection; a DEDICATED OS THREAD drains it and does the ushort->byte
    // LE encode into a single reused scratch buffer + the SER write.
    //
    // The drain runs on its own Thread (not the thread pool): a slow eMMC/SD
    // write-back blocks in a write() syscall, and on the pool that would tie
    // up a worker the runtime only replaces ~1/sec, starving the HTTP/WS
    // handlers — exactly the field symptom where recording stalled at a few
    // hundred MB and the whole UI froze. A dedicated thread absorbs the stall
    // in isolation; the bounded queue just drops frames while it catches up.
    private BlockingCollection<QueueItem>? _queue;
    private Thread? _writerThread;
    private long _enqueued;
    // The queue is bounded by a RAM budget (bytes in flight), not a frame
    // count, so the headroom is the same whether the ROI is 640×480 or larger.
    // 256 MB rides out multi-second GC/write-back stalls at high fps and is safe
    // on the SBCs Polaris targets (≥4 GB). When over budget OnFrame drops.
    private const long MaxQueuedBytes = 256L * 1024 * 1024;
    private long _queuedBytes;

    private readonly record struct QueueItem(ushort[] Pixels, int ByteLen,
        int Width, int Height, SerColorMode Color, DateTime Utc);

    /// <summary>Raised with the full path once a recording is closed and the
    /// SER header/trailer are final. The mirror of
    /// <see cref="ImageWriterService.ImageSaved"/>: network-storage push hangs
    /// off that one, and a .ser never passes through the image writer, so
    /// without this event planetary captures were the one thing the share
    /// never received.</summary>
    public event Action<string>? RecordingSaved;

    public bool IsRecording { get; private set; }
    // The SER writer opens lazily on the first streamed frame, so right after
    // Start() the writer is still null — report the path Start() settled on
    // (where the file WILL be) so /record/start + /record/status can show it
    // immediately instead of null until the first frame lands.
    private string? _pendingPath;
    public string? OutputPath => _writer?.Path ?? _pendingPath;
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

            // FIELD8-4: refuse before the camera starts feeding, not 40 seconds
            // in. Planetary video is the fastest writer in the app and the
            // captures usually share the root filesystem, so "start anyway and
            // see" risks the whole host, not just the clip.
            long freeAtStart = FreeBytesFor(path);
            if (freeAtStart >= 0 && freeAtStart < MinFreeBytes) {
                // Nothing has been mutated yet (IsRecording is still false, no
                // writer thread, no subscription), so throwing here leaves the
                // service exactly as it was.
                throw new InvalidOperationException(
                    $"Only {freeAtStart / (1024.0 * 1024 * 1024):F1} GB free on the capture disk. "
                    + "Planetary video writes tens of megabytes per second; free up space first.");
            }
            var instrument = cam.DeviceName;
            var telescope = _equip.Telescope?.DeviceName ?? "";
            _pendingPath = path;
            _activeConfig = cfg;
            _startedAt = DateTime.UtcNow;
            _droppedFrames = 0;
            _enqueued = 0;
            LastError = null;
            IsRecording = true;

            // Unbounded collection gated by a RAM budget (MaxQueuedBytes):
            // OnFrame drops once the in-flight bytes exceed the budget, so a GC
            // pause or disk write-back hiccup no longer drops mid-capture until
            // ~256 MB is queued. Count-bounding instead would vary with ROI.
            Interlocked.Exchange(ref _queuedBytes, 0);
            var queue = new BlockingCollection<QueueItem>();
            _queue = queue;
            _writerThread = new Thread(() => WriterLoop(path, instrument, telescope, queue)) {
                IsBackground = true,
                Name = "polaris-ser-writer"
            };
            _writerThread.Start();

            _subscription = _stream.SubscribeFrames(OnFrame);
            _logger.LogInformation("Recording started → {Path} (geometry locked on first frame)", path);
        }
    }

    public async Task StopAsync() {
        SerFileWriter? writer = null;
        IDisposable? sub;
        BlockingCollection<QueueItem>? queue;
        Thread? writerThread;
        lock (_lock) {
            if (!IsRecording) return;
            IsRecording = false;
            sub = _subscription; _subscription = null;
            queue = _queue; _queue = null;
            writerThread = _writerThread; _writerThread = null;
            _activeConfig = null;
        }

        // Unsubscribe first so no more frames are enqueued, then signal the
        // writer thread to drain the queue and exit, then close the SER file
        // (header frame-count patch + timestamp trailer happen on Dispose).
        // Join off the caller thread so a final disk flush can't block the
        // request handler.
        try { sub?.Dispose(); } catch { }
        try { queue?.CompleteAdding(); } catch { }
        if (writerThread != null) {
            try { await Task.Run(() => writerThread.Join(TimeSpan.FromSeconds(15))); } catch { }
        }
        try { queue?.Dispose(); } catch { }

        // Capture the writer AFTER the loop finished: it's opened lazily on the
        // first frame, so reading it before the drain could miss an instance
        // created while a queued frame was still in flight (unfinalised SER).
        lock (_lock) { writer = _writer; _writer = null; }

        var path = writer?.Path;
        var frames = writer?.FrameCount ?? 0;
        try { writer?.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Writer dispose failed"); }
        _pendingPath = null;
        _logger.LogInformation("Recording stopped: {Path} ({N} frames, {Dropped} dropped)",
            path, frames, _droppedFrames);

        // Announce only a finished file with content: Dispose is what patches
        // the frame count into the header and appends the timestamp trailer,
        // so anything raised earlier would ship a SER no tool can read. A
        // zero-frame recording (stream died before the first frame) leaves a
        // file nobody wants on the share.
        if (!string.IsNullOrEmpty(path) && frames > 0 && File.Exists(path)) {
            WriteCaptureLog(path!, writer, frames);
            try { RecordingSaved?.Invoke(path); }
            catch (Exception ex) { _logger.LogDebug(ex, "RecordingSaved handler threw"); }
        }
    }

    /// <summary>PLANLOG: a plain-text companion beside the .ser, the way
    /// FireCapture and the rest of the planetary world do it.
    ///
    /// <para>A SER header holds geometry and little else, so a month later the
    /// only record of the gain, the exposure, the scope or how many frames
    /// were dropped is the operator's memory. Stacking software will not tell
    /// you either. This costs a few hundred bytes next to a multi-gigabyte
    /// recording and answers "what did I do that night" without asking anyone
    /// to remember.</para>
    ///
    /// <para>Written after the SER is closed and only for a recording that
    /// produced frames, so the log's existence means the file beside it is
    /// readable. Any failure here is logged and swallowed: a missing companion
    /// must never cost the recording.</para></summary>
    private void WriteCaptureLog(string serPath, SerFileWriter? writer, int frames) {
        try {
            var started = _startedAt;
            var ended = DateTime.UtcNow;
            var seconds = Math.Max(0.001, (ended - started).TotalSeconds);
            var cam = _equip.Camera;
            var rig = _profiles.ActiveEquipmentProfile;
            var fi = new FileInfo(serPath);

            var sb = new System.Text.StringBuilder();
            void Line(string k, object? v) {
                if (v == null) return;
                var s = v.ToString();
                if (!string.IsNullOrWhiteSpace(s)) sb.AppendLine($"{k,-22}{s}");
            }

            sb.AppendLine("Polaris Astro Controller, planetary capture log");
            sb.AppendLine(new string('-', 58));
            Line("File", Path.GetFileName(serPath));
            Line("Started (UTC)", started.ToString("yyyy-MM-dd HH:mm:ss"));
            Line("Ended (UTC)", ended.ToString("yyyy-MM-dd HH:mm:ss"));
            Line("Duration", $"{seconds:0.0} s");
            sb.AppendLine();

            Line("Camera", cam?.DeviceName);
            Line("Driver", rig?.CameraDriver);
            if (writer != null) {
                Line("Frame size", $"{writer.Width} x {writer.Height}");
                Line("Recorded depth", $"{writer.BitDepth}-bit");
                Line("Colour mode", writer.ColorMode.ToString());
            }
            Line("Exposure", $"{_stream.ExposureSeconds * 1000:0.###} ms");
            Line("Gain", _stream.Gain);
            if (cam != null && !double.IsNaN(cam.Temperature)) Line("Sensor temp", $"{cam.Temperature:0.0} C");
            Line("Binning", $"{_stream.BinX} x {_stream.BinY}");
            sb.AppendLine();

            Line("Frames written", frames);
            Line("Frames dropped", _droppedFrames);
            Line("Average rate", $"{frames / seconds:0.0} fps");
            Line("File size", $"{fi.Length / (1024.0 * 1024):0.0} MB");
            sb.AppendLine();

            Line("Telescope", _equip.Telescope?.DeviceName ?? rig?.TelescopeModel);
            if (rig != null) {
                if (rig.FocalLengthMm > 0) Line("Focal length", $"{rig.FocalLengthMm:0} mm");
                if (rig.ApertureMm > 0) Line("Aperture", $"{rig.ApertureMm:0} mm");
            }
            var scope = _equip.Telescope;
            if (scope is { IsConnected: true }
                    && !double.IsNaN(scope.RightAscension) && !double.IsNaN(scope.Declination)) {
                Line("Pointing", $"RA {scope.RightAscension:0.0000} h, Dec {scope.Declination:0.0000} deg");
            }
            var focuser = _equip.Focuser;
            if (focuser is { IsConnected: true }) Line("Focuser position", focuser.Position);
            sb.AppendLine();

            Line("Rig", rig?.Name);
            Line("Polaris", typeof(VideoRecordingService).Assembly.GetName().Version?.ToString());

            var logPath = Path.ChangeExtension(serPath, ".txt");
            File.WriteAllText(logPath, sb.ToString());
            _logger.LogInformation("Capture log written → {Path}", Path.GetFileName(logPath));
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Could not write the capture log beside {Path}", serPath);
        }
    }

    /// <summary>Background drain: ushort->byte LE encode into a single
    /// reused scratch buffer + SER write, off the camera delivery thread.
    /// Runs until the channel is completed (StopAsync) and fully drained.</summary>
    private void WriterLoop(string path, string instrument, string telescope,
                            BlockingCollection<QueueItem> queue) {
        SerFileWriter? writer = null;
        byte[]? scratch = null;
        int sinceSpaceCheck = 0;
        int outBits = 16;
        try {
            foreach (var item in queue.GetConsumingEnumerable()) {
                // Item has left the queue → release its bytes from the budget.
                Interlocked.Add(ref _queuedBytes, -item.ByteLen);
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
                        outBits = _activeConfig?.BitDepth == 8 ? 8 : 16;
                        writer = new SerFileWriter(path, item.Width, item.Height, outBits, colorMode,
                            observer: "Polaris", instrument: instrument, telescope: telescope);
                        scratch = new byte[writer.BytesPerFrame];
                        lock (_lock) { _writer = writer; }
                        _logger.LogInformation("Recording geometry locked → {W}×{H}×{Bits} ({Color})",
                            item.Width, item.Height, outBits, colorMode);
                    }
                    // Header geometry is now fixed; a frame whose size changed
                    // mid-recording (e.g. ROI edit) can't be appended. The
                    // comparison is against the SOURCE size: at 8 bits the
                    // scratch buffer is half the incoming frame.
                    int srcBytes = item.Width * item.Height * 2;
                    if (item.ByteLen != srcBytes || srcBytes != scratch!.Length * (16 / outBits)) {
                        Interlocked.Increment(ref _droppedFrames);
                        continue;
                    }
                    if (outBits == 8) {
                        // PLAN8: take the top byte. Every backend widens a RAW8
                        // readout with `px << 8`, so this is that exact
                        // operation inverted, not a lossy rescale of 16-bit
                        // data that was never there.
                        var src = item.Pixels;
                        for (int i = 0; i < scratch.Length; i++) scratch[i] = (byte)(src[i] >> 8);
                        writer.WriteFrame(scratch, scratch.Length, item.Utc);
                    } else {
                        Buffer.BlockCopy(item.Pixels, 0, scratch, 0, item.ByteLen);
                        writer.WriteFrame(scratch, item.ByteLen, item.Utc);
                    }

                    // FIELD8-4: stop while there is still room. Planetary video
                    // writes FAST (640x640x16 at 130 fps is ~106 MB/s), so a
                    // disk with a few GB free fills in well under a minute and
                    // nothing here was watching. On 2026-07-31 a 4.10 GB clip
                    // went onto a root filesystem with 4.2 GB free, the board
                    // came back rebooted, and the clip was unusable.
                    //
                    // Stopping ourselves keeps the file INTACT (frame count
                    // patched, timestamp trailer written) instead of leaving
                    // whatever the OS happened to flush before it ran out, and
                    // it keeps the ROOT filesystem from filling, which takes
                    // the whole appliance down with it.
                    if (++sinceSpaceCheck >= SpaceCheckFrames) {
                        sinceSpaceCheck = 0;
                        long free = FreeBytesFor(path);
                        if (free >= 0 && free < MinFreeBytes) {
                            LastError = $"Recording stopped: only "
                                      + $"{free / (1024.0 * 1024 * 1024):F1} GB left on the capture disk.";
                            _logger.LogWarning(
                                "Recording stopped early: {Free:F2} GB free on the capture disk, "
                                + "reserve is {Reserve:F2} GB", free / (1024.0 * 1024 * 1024),
                                MinFreeBytes / (1024.0 * 1024 * 1024));
                            _ = Task.Run(StopAsync);
                            return;
                        }
                    }
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

    /// <summary>FIELD8-4: how much of the capture disk stays untouched. On an
    /// appliance the captures share the ROOT filesystem, and a root at 100% is
    /// not "no more recordings", it is a host that stops working: logs, the
    /// profile, the SQLite database and systemd itself all need to write.</summary>
    private const long MinFreeBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Checked every N frames rather than per frame. At 130 fps this
    /// is roughly twice a second, and at ~106 MB/s the disk cannot move more
    /// than ~50 MB between checks: far inside the 2 GB reserve.</summary>
    private const int SpaceCheckFrames = 64;

    /// <summary>Free bytes on the volume holding <paramref name="path"/>, or
    /// -1 when it cannot be determined (never block a recording over a
    /// question we failed to ask).</summary>
    internal static long FreeBytesFor(string path) {
        try {
            // The target folder is created lazily by the writer, so walk up to
            // the nearest ancestor that exists: on Unix every path resolves to
            // SOME mounted filesystem, and that is the one we care about.
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                dir = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(dir)) return -1;
            return new DriveInfo(dir).AvailableFreeSpace;
        } catch { return -1; }
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
        BlockingCollection<QueueItem>? queue;
        lock (_lock) {
            cfg = _activeConfig;
            queue = _queue;
            if (queue == null || cfg == null) return;
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
        // Drop (counted) when the in-flight RAM budget is exceeded, so a disk
        // write-back stall never back-pressures the camera delivery thread or
        // grows memory without bound.
        if (Interlocked.Read(ref _queuedBytes) + item.ByteLen > MaxQueuedBytes) {
            Interlocked.Increment(ref _droppedFrames);
            return;
        }
        bool added;
        try { added = queue.TryAdd(item); }
        catch (InvalidOperationException) { return; }   // CompleteAdding raced Stop
        if (added) {
            Interlocked.Add(ref _queuedBytes, item.ByteLen);
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
    SerColorMode? ColorMode = null,
    /// <summary>PLAN8: bits per sample WRITTEN TO DISK, 8 or 16 (default 16).
    ///
    /// The camera stream always hands us 16-bit samples, left-aligned the way
    /// every backend widens a RAW8 readout (<c>px &lt;&lt; 8</c>). Writing 8
    /// takes the top byte back off, which is what planetary capture wants:
    /// the target is bright, lucky-imaging recovers the depth by averaging
    /// hundreds of frames, and the file (and the byte rate that fills a disk
    /// in 40 seconds) is halved.
    ///
    /// This is a DISK format choice, not a sensor mode: the USB traffic and
    /// the frame rate ceiling are unchanged, since those are set when the
    /// camera's readout format is chosen at connect.</summary>
    int BitDepth = 16);