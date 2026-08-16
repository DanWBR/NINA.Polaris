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
using System.Threading.Channels;
using NINA.Polaris.Services.Planetary;
using NINA.Polaris.Services.Storage;

namespace NINA.Polaris.Services;

/// <summary>
/// Provider-agnostic engine that auto-pushes every saved capture to network
/// storage (SMB / SFTP / mounted path). Subscribes to
/// <see cref="ImageWriterService.ImageSaved"/> and
/// <see cref="VideoRecordingService.RecordingSaved"/>, mirrors the local
/// capture tree onto the configured target, and keeps the local copy.
/// Failures retry with backoff and then park in a "failed" list the user can
/// re-push.
///
/// Two independent LANES, each with its own queue, connection and circuit
/// breaker: images and planetary recordings. A .ser is routinely several GB,
/// and behind a single consumer it held up every sub captured after it for as
/// long as the transfer took. They are separate transfers now, so a night of
/// FITS keeps flowing while a recording uploads next to it. Bandwidth is
/// still shared, which is the honest limit; head-of-line blocking is not.
/// </summary>
public sealed class StoragePushService : BackgroundService {
    private const int MaxAttempts = 3;
    private const int MaxFailedTracked = 500;

    private readonly ImageWriterService _writer;
    private readonly VideoRecordingService _video;
    private readonly ProfileService _profile;
    private readonly IStorageTargetFactory _factory;
    private readonly ILogger<StoragePushService> _logger;

    private readonly Lane _images;
    private readonly Lane _videos;

    // Status surface (read by endpoints + WebSocket). The unqualified names
    // aggregate both lanes so existing callers keep their meaning; the
    // video-* ones let the UI show a long recording upload separately, which
    // matters when the queue count would otherwise sit at 1 for an hour.
    public string Kind => (_profile.Active.StorageKind ?? "smb").Trim().ToLowerInvariant();
    public bool Enabled => _profile.Active.StoragePushEnabled;
    public bool Connected => _images.Connected || _videos.Connected;
    public int Queued => _images.Queued + _videos.Queued;
    public long Uploaded => _images.Uploaded + _videos.Uploaded;
    public long Failed => _images.Failed + _videos.Failed;
    public string? CurrentFile => _images.CurrentFile ?? _videos.CurrentFile;
    public string? LastError => _images.LastError ?? _videos.LastError;
    public DateTime? LastUploadUtc =>
        _images.LastUploadUtc is { } a && _videos.LastUploadUtc is { } b
            ? (a > b ? a : b)
            : _images.LastUploadUtc ?? _videos.LastUploadUtc;

    public int VideoQueued => _videos.Queued;
    public long VideoUploaded => _videos.Uploaded;
    public string? VideoCurrentFile => _videos.CurrentFile;

    /// <summary>True while pushes are paused because the target tripped a
    /// breaker. Surfaced in status so the UI can show the target is down.</summary>
    public bool CircuitOpen => _images.CircuitOpen || _videos.CircuitOpen;

    private sealed record QueueItem(string LocalPath, string RelPath, int Attempts);

    public StoragePushService(ImageWriterService writer, VideoRecordingService video,
                              ProfileService profile, IStorageTargetFactory factory,
                              ILogger<StoragePushService> logger) {
        _writer = writer;
        _video = video;
        _profile = profile;
        _factory = factory;
        _logger = logger;
        _images = new Lane(this, "images");
        _videos = new Lane(this, "video");
    }

    /// <summary>Enqueue a saved image for push. Public so it is unit-testable
    /// and so a future "backfill" feature can reuse it. No-op when disabled.</summary>
    public void Enqueue(string fullPath) => _images.Enqueue(fullPath);

    /// <summary>Enqueue a finished recording. Separate lane, see the class
    /// note: a multi-GB .ser must not sit in front of the night's subs.</summary>
    public void EnqueueVideo(string fullPath) => _videos.Enqueue(fullPath);

    /// <summary>Re-enqueue everything parked in the failed lists (the "Retry"
    /// button). Each item goes back to the lane it failed in.</summary>
    public int RetryFailed() => _images.RetryFailed() + _videos.RetryFailed();

    /// <summary>One-way backfill (the "Sync past sessions" button): walk the
    /// whole capture tree and enqueue every file so anything captured while the
    /// share was disabled or unreachable gets pushed now. Reuses the normal
    /// lanes, so uploads stay paced (LinkSharePercent) and the targets skip
    /// files already present with the same size — the sync only copies what is
    /// missing. Returns how many files were queued. No-op when disabled or the
    /// capture root doesn't exist.</summary>
    public int Backfill() {
        if (!Enabled) return 0;
        var root = _profile.Active.ImageOutputDir;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return 0;
        int n = 0;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
        catch (Exception ex) {
            _logger.LogWarning(ex, "Backfill: could not enumerate {Root}", root);
            return 0;
        }
        foreach (var file in files) {
            try {
                if (IsVideoFile(file)) _videos.Enqueue(file);
                else _images.Enqueue(file);
                n++;
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Backfill: could not queue {File}", file);
            }
        }
        _logger.LogInformation("Backfill queued {Count} file(s) from {Root}", n, root);
        return n;
    }

    /// <summary>Recordings go through the video lane so a multi-GB .ser can't sit
    /// in front of the night's subs; everything else is an image. Public + static
    /// so the routing decision is unit-testable without standing up the service.</summary>
    public static bool IsVideoFile(string path) {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".ser" or ".avi" or ".mp4" or ".mov";
    }

    public async Task<(bool ok, string message)> TestConnectionAsync(StorageConfig cfg, CancellationToken ct) {
        using var target = _factory.Create(cfg.Kind);
        return await target.TestAsync(cfg, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        _writer.ImageSaved += Enqueue;
        // Planetary recordings never go through the image writer, so they need
        // their own subscription — otherwise the share got every FITS of the
        // night and not one .ser.
        _video.RecordingSaved += EnqueueVideo;
        try {
            // Both lanes drain concurrently; each owns its own connection to
            // the target, so a stalled transfer in one cannot block the other.
            await Task.WhenAll(_images.RunAsync(stoppingToken),
                               _videos.RunAsync(stoppingToken));
        } catch (OperationCanceledException) {
            // shutting down
        } finally {
            _writer.ImageSaved -= Enqueue;
            _video.RecordingSaved -= EnqueueVideo;
            _images.DropConnection();
            _videos.DropConnection();
        }
    }

    /// <summary>One independent transfer path: its own queue, its own
    /// connection to the target, its own retry/backoff and circuit breaker.
    /// Nothing here is shared between lanes, which is what keeps them from
    /// interfering (and keeps the breaker counters free of cross-thread
    /// races).</summary>
    private sealed class Lane {
        private readonly StoragePushService _svc;
        private readonly string _name;

        private readonly Channel<QueueItem> _queue =
            Channel.CreateUnbounded<QueueItem>(new UnboundedChannelOptions { SingleReader = true });
        private readonly ConcurrentQueue<QueueItem> _failed = new();

        private IStorageTarget? _target;
        private StorageConfig? _activeConfig;
        private int _queued;

        // Circuit breaker: a dead/slow storage target must not keep stalling
        // this lane's consumer. Each failed SMB attempt blocks for the client
        // timeout (~15s, with SMB2 encryption burning CPU) and, with
        // reconnect-per-failure, steals CPU/network from the capture +
        // live-view pipeline — a failing NAS was starving the
        // /ws/image-stream frame send so the preview went blank. After
        // CircuitThreshold consecutive failures we stop attempting and park
        // new items for a growing cooldown; a single success closes it.
        private const int CircuitThreshold = 3;
        private static readonly TimeSpan InitialCooldown = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(5);
        private int _consecutiveFailures;
        private DateTime _circuitOpenUntil = DateTime.MinValue;
        private TimeSpan _circuitCooldown = InitialCooldown;

        public bool Connected { get; private set; }
        public int Queued => _queued;
        public long Uploaded { get; private set; }
        public long Failed { get; private set; }
        public string? CurrentFile { get; private set; }
        public string? LastError { get; private set; }
        public DateTime? LastUploadUtc { get; private set; }
        public bool CircuitOpen => DateTime.UtcNow < _circuitOpenUntil;

        public Lane(StoragePushService svc, string name) {
            _svc = svc;
            _name = name;
        }

        public void Enqueue(string fullPath) {
            if (!_svc._profile.Active.StoragePushEnabled) return;
            var root = _svc._profile.Active.ImageOutputDir;
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(fullPath)) return;
            string rel;
            try { rel = Path.GetRelativePath(root, fullPath); }
            catch { rel = Path.GetFileName(fullPath); }
            if (string.IsNullOrEmpty(rel) || rel.StartsWith("..")) rel = Path.GetFileName(fullPath);
            if (_queue.Writer.TryWrite(new QueueItem(fullPath, rel, 0)))
                Interlocked.Increment(ref _queued);
        }

        public int RetryFailed() {
            // Explicit user action ("try now"): close the breaker so the retry
            // actually attempts instead of being parked again by the cooldown.
            _circuitOpenUntil = DateTime.MinValue;
            _consecutiveFailures = 0;
            _circuitCooldown = InitialCooldown;
            int n = 0;
            while (_failed.TryDequeue(out var item)) {
                if (_queue.Writer.TryWrite(item with { Attempts = 0 })) { Interlocked.Increment(ref _queued); n++; }
            }
            if (n > 0) { Failed = 0; LastError = null; }
            return n;
        }

        public async Task RunAsync(CancellationToken stoppingToken) {
            await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken)) {
                // Decrement AFTER the upload finishes, not on dequeue. The
                // in-flight file is still outstanding work; counting it only
                // while it sat in the channel made the activity badge read 0
                // for the whole duration of an active transfer.
                try { await ProcessAsync(item, stoppingToken); }
                finally { Interlocked.Decrement(ref _queued); }
            }
        }

        private async Task ProcessAsync(QueueItem item, CancellationToken ct) {
            if (!_svc._profile.Active.StoragePushEnabled) return;   // toggled off while queued
            if (!File.Exists(item.LocalPath)) return;               // local gone, nothing to push

            // Breaker open: the target is known-down. Park the item immediately
            // instead of blocking the consumer on another timeout — this is what
            // keeps a dead NAS from stealing CPU/network from the live view.
            if (CircuitOpen) {
                Failed++;
                if (_failed.Count < MaxFailedTracked) _failed.Enqueue(item with { Attempts = 0 });
                return;
            }

            CurrentFile = item.RelPath;
            try {
                await EnsureConnectedAsync(ct);
                await _target!.UploadAsync(item.LocalPath, item.RelPath, ct);
                Uploaded++;
                LastUploadUtc = DateTime.UtcNow;
                LastError = null;
                CurrentFile = null;
                // Success closes the breaker and resets the backoff.
                _consecutiveFailures = 0;
                _circuitCooldown = InitialCooldown;
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                LastError = ex.Message;
                DropConnection();   // force a fresh connect next time
                _consecutiveFailures++;
                _svc._logger.LogWarning(ex, "Storage push ({Lane}) failed for {Rel} (attempt {N})",
                    _name, item.RelPath, item.Attempts + 1);

                if (_consecutiveFailures >= CircuitThreshold) {
                    // Trip the breaker: stop hammering the dead target. Park this
                    // item and pause pushes for a growing cooldown (capped), so the
                    // capture/preview pipeline gets the CPU + network back.
                    _circuitOpenUntil = DateTime.UtcNow + _circuitCooldown;
                    _svc._logger.LogWarning(
                        "Storage target unreachable ({Lane}); pausing pushes for {Sec:n0}s after {N} consecutive failures",
                        _name, _circuitCooldown.TotalSeconds, _consecutiveFailures);
                    _circuitCooldown = TimeSpan.FromSeconds(
                        Math.Min(MaxCooldown.TotalSeconds, _circuitCooldown.TotalSeconds * 2));
                    Failed++;
                    if (_failed.Count < MaxFailedTracked) _failed.Enqueue(item with { Attempts = 0 });
                } else if (item.Attempts + 1 < MaxAttempts) {
                    // Backoff then requeue. Single consumer per lane, so an
                    // inline delay simply paces retries against a flaky share.
                    try { await Task.Delay(TimeSpan.FromSeconds(3 * (item.Attempts + 1)), ct); }
                    catch (OperationCanceledException) { throw; }
                    if (_queue.Writer.TryWrite(item with { Attempts = item.Attempts + 1 }))
                        Interlocked.Increment(ref _queued);
                } else {
                    Failed++;
                    if (_failed.Count < MaxFailedTracked) _failed.Enqueue(item);
                }
                CurrentFile = null;
            }
        }

        private async Task EnsureConnectedAsync(CancellationToken ct) {
            var cfg = StorageConfig.FromProfile(_svc._profile.Active);
            if (_target != null && Connected && cfg == _activeConfig) return;
            DropConnection();
            var target = _svc._factory.Create(cfg.Kind);
            await target.ConnectAsync(cfg, ct);
            _target = target;
            _activeConfig = cfg;
            Connected = true;
        }

        public void DropConnection() {
            try { _target?.Dispose(); } catch { /* ignore */ }
            _target = null;
            _activeConfig = null;
            Connected = false;
        }
    }
}
