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
using NINA.Polaris.Services.Storage;

namespace NINA.Polaris.Services;

/// <summary>
/// Provider-agnostic engine that auto-pushes every saved image to network
/// storage (SMB / SFTP / mounted path). Subscribes to
/// <see cref="ImageWriterService.ImageSaved"/>, mirrors the local capture tree
/// onto the configured target, and keeps the local copy. A single background
/// consumer drains an unbounded queue so captures never block; failures retry
/// with backoff and then park in a "failed" list the user can re-push.
/// </summary>
public sealed class StoragePushService : BackgroundService {
    private const int MaxAttempts = 3;
    private const int MaxFailedTracked = 500;

    private readonly ImageWriterService _writer;
    private readonly ProfileService _profile;
    private readonly IStorageTargetFactory _factory;
    private readonly ILogger<StoragePushService> _logger;

    private readonly Channel<QueueItem> _queue =
        Channel.CreateUnbounded<QueueItem>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentQueue<QueueItem> _failed = new();

    private IStorageTarget? _target;
    private StorageConfig? _activeConfig;

    // Status surface (read by endpoints + WebSocket).
    public string Kind => (_profile.Active.StorageKind ?? "smb").Trim().ToLowerInvariant();
    public bool Enabled => _profile.Active.StoragePushEnabled;
    public bool Connected { get; private set; }
    public int Queued => _queued;
    public long Uploaded { get; private set; }
    public long Failed { get; private set; }
    public string? CurrentFile { get; private set; }
    public string? LastError { get; private set; }
    public DateTime? LastUploadUtc { get; private set; }

    private int _queued;

    // Circuit breaker: a dead/slow storage target must not keep stalling the
    // single consumer. Each failed SMB attempt blocks for the client timeout
    // (~15s, with SMB2 encryption burning CPU) and, with reconnect-per-failure,
    // steals CPU/network from the capture + live-view pipeline — a failing NAS
    // was starving the /ws/image-stream frame send so the preview went blank.
    // After CircuitThreshold consecutive failures we stop attempting and park
    // new items for a growing cooldown; a single success closes the breaker.
    private const int CircuitThreshold = 3;
    private static readonly TimeSpan InitialCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(5);
    private int _consecutiveFailures;
    private DateTime _circuitOpenUntil = DateTime.MinValue;
    private TimeSpan _circuitCooldown = InitialCooldown;

    /// <summary>True while pushes are paused because the target tripped the
    /// breaker. Surfaced in status so the UI can show the target is down.</summary>
    public bool CircuitOpen => DateTime.UtcNow < _circuitOpenUntil;

    private sealed record QueueItem(string LocalPath, string RelPath, int Attempts);

    public StoragePushService(ImageWriterService writer, ProfileService profile,
                              IStorageTargetFactory factory, ILogger<StoragePushService> logger) {
        _writer = writer;
        _profile = profile;
        _factory = factory;
        _logger = logger;
    }

    /// <summary>Enqueue a saved file for push. Public so it is unit-testable and
    /// so a future "backfill" feature can reuse it. No-op when disabled.</summary>
    public void Enqueue(string fullPath) {
        if (!_profile.Active.StoragePushEnabled) return;
        var root = _profile.Active.ImageOutputDir;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(fullPath)) return;
        string rel;
        try { rel = Path.GetRelativePath(root, fullPath); }
        catch { rel = Path.GetFileName(fullPath); }
        if (string.IsNullOrEmpty(rel) || rel.StartsWith("..")) rel = Path.GetFileName(fullPath);
        if (_queue.Writer.TryWrite(new QueueItem(fullPath, rel, 0)))
            Interlocked.Increment(ref _queued);
    }

    /// <summary>Re-enqueue everything parked in the failed list (the "Retry" button).</summary>
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

    public async Task<(bool ok, string message)> TestConnectionAsync(StorageConfig cfg, CancellationToken ct) {
        using var target = _factory.Create(cfg.Kind);
        return await target.TestAsync(cfg, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        _writer.ImageSaved += Enqueue;
        try {
            await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken)) {
                Interlocked.Decrement(ref _queued);
                await ProcessAsync(item, stoppingToken);
            }
        } catch (OperationCanceledException) {
            // shutting down
        } finally {
            _writer.ImageSaved -= Enqueue;
            DropConnection();
        }
    }

    private async Task ProcessAsync(QueueItem item, CancellationToken ct) {
        if (!_profile.Active.StoragePushEnabled) return;     // toggled off while queued
        if (!File.Exists(item.LocalPath)) return;            // local gone, nothing to push

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
            _logger.LogWarning(ex, "Storage push failed for {Rel} (attempt {N})", item.RelPath, item.Attempts + 1);

            if (_consecutiveFailures >= CircuitThreshold) {
                // Trip the breaker: stop hammering the dead target. Park this
                // item and pause pushes for a growing cooldown (capped), so the
                // capture/preview pipeline gets the CPU + network back.
                _circuitOpenUntil = DateTime.UtcNow + _circuitCooldown;
                _logger.LogWarning(
                    "Storage target unreachable; pausing pushes for {Sec:n0}s after {N} consecutive failures",
                    _circuitCooldown.TotalSeconds, _consecutiveFailures);
                _circuitCooldown = TimeSpan.FromSeconds(
                    Math.Min(MaxCooldown.TotalSeconds, _circuitCooldown.TotalSeconds * 2));
                Failed++;
                if (_failed.Count < MaxFailedTracked) _failed.Enqueue(item with { Attempts = 0 });
            } else if (item.Attempts + 1 < MaxAttempts) {
                // Backoff then requeue. Single consumer, so an inline delay
                // simply paces retries against a flaky/absent share.
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
        var cfg = StorageConfig.FromProfile(_profile.Active);
        if (_target != null && Connected && cfg == _activeConfig) return;
        DropConnection();
        var target = _factory.Create(cfg.Kind);
        await target.ConnectAsync(cfg, ct);
        _target = target;
        _activeConfig = cfg;
        Connected = true;
    }

    private void DropConnection() {
        try { _target?.Dispose(); } catch { /* ignore */ }
        _target = null;
        _activeConfig = null;
        Connected = false;
    }
}
