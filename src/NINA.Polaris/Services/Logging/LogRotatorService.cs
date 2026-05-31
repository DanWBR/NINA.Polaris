using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace NINA.Polaris.Services.Logging;

/// <summary>
/// DBGLOG-9: opt-in disk persistence for the debug log ring buffer.
///
/// When <see cref="ProfileService.Active.LogToDisk"/> is true, subscribes
/// to <see cref="LogService.Appended"/> and flushes batched entries every
/// 2 seconds to <c>{LocalAppData}/NINA.Polaris/logs/polaris-yyyy-MM-dd.jsonl</c>
/// (UTF-8, append-only, one JSON object per line). A second timer (hourly)
/// sweeps files older than 7 days.
///
/// The service stays alive even when persistence is OFF — the toggle is
/// re-read each tick so a Settings change takes effect immediately without
/// a restart. When OFF, the buffered queue is drained and discarded so we
/// don't hold onto entries that the operator opted not to persist.
///
/// Errors NEVER use <see cref="Microsoft.Extensions.Logging.ILogger"/>
/// (would recurse through LogBufferLogger → LogService → Appended → this
/// service). Instead they go to <c>Console.Error</c> so the systemd
/// journal still catches them.
/// </summary>
public sealed class LogRotatorService : BackgroundService {
    private readonly LogService _logService;
    private readonly ProfileService _profiles;
    private readonly string _logDir;
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private DateTime _lastRetentionSweep = DateTime.MinValue;
    private bool _subscribed;

    /// <summary>How long a file must go untouched before the retention
    /// sweep deletes it. 7 days strikes a balance: long enough that a
    /// bug reported "last Saturday" is still recoverable, short enough
    /// that the SD card on a Pi doesn't fill up over months of running.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <summary>Cadence of the flush loop. 2 s keeps the queue small
    /// even on chatty servers (~80 entries/min ≈ ~3 per flush) while
    /// still being short enough that a crash loses at most 2 s of log
    /// data.</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    public LogRotatorService(LogService logService, ProfileService profiles) {
        _logService = logService;
        _profiles = profiles;
        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NINA.Polaris", "logs");
    }

    protected override async Task ExecuteAsync(CancellationToken ct) {
        // Subscribe lazily — _logService might not exist at construction
        // time depending on DI ordering, but ExecuteAsync runs after the
        // host is fully composed.
        try {
            Directory.CreateDirectory(_logDir);
        } catch (Exception ex) {
            Console.Error.WriteLine($"[LogRotator] cannot create {_logDir}: {ex.Message}");
        }

        EnsureSubscribed();

        while (!ct.IsCancellationRequested) {
            try {
                var enabled = _profiles.Active?.LogToDisk == true;
                if (enabled) {
                    await FlushQueueAsync(ct);
                    MaybeSweepRetention();
                } else {
                    // Drain + discard so we don't hold entries the
                    // operator opted not to keep.
                    while (_pending.TryDequeue(out _)) { }
                }
                await Task.Delay(FlushInterval, ct);
            } catch (OperationCanceledException) {
                break;
            } catch (Exception ex) {
                Console.Error.WriteLine($"[LogRotator] tick failed: {ex.Message}");
                try { await Task.Delay(FlushInterval, ct); }
                catch { break; }
            }
        }

        // Final flush on shutdown so we don't lose the last ~2 s of
        // entries on a graceful systemd stop.
        try { await FlushQueueAsync(CancellationToken.None); }
        catch (Exception ex) { Console.Error.WriteLine($"[LogRotator] final flush failed: {ex.Message}"); }
    }

    private void EnsureSubscribed() {
        if (_subscribed) return;
        _subscribed = true;
        _logService.Appended += entry => {
            // The handler MUST be fast (LogService fires it inline on
            // every Append). Just enqueue + return; the loop does the
            // actual disk I/O. The toggle is evaluated at flush time so
            // entries appended while persistence is OFF are dropped
            // when the loop drains the queue.
            _pending.Enqueue(entry);
        };
    }

    private async Task FlushQueueAsync(CancellationToken ct) {
        if (_pending.IsEmpty) return;
        var path = Path.Combine(_logDir, $"polaris-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
        try {
            // Stream-style append: open once per flush, write all queued
            // entries, close. Avoids long-held file handles + lets log
            // viewers tail the file safely.
            await using var fs = new FileStream(path, FileMode.Append,
                FileAccess.Write, FileShare.Read,
                bufferSize: 4096, useAsync: true);
            await using var writer = new StreamWriter(fs, new UTF8Encoding(false));
            while (_pending.TryDequeue(out var entry)) {
                if (ct.IsCancellationRequested) break;
                var json = JsonSerializer.Serialize(entry, JsonOpts);
                await writer.WriteLineAsync(json);
            }
            await writer.FlushAsync(ct);
        } catch (Exception ex) {
            Console.Error.WriteLine($"[LogRotator] flush to {path} failed: {ex.Message}");
        }
    }

    private void MaybeSweepRetention() {
        // Hourly cadence. Cheap enough we could check every flush, but
        // the disk hit (enumerate + stat) is wasted work — files don't
        // expire on a 2-s tempo.
        if (DateTime.UtcNow - _lastRetentionSweep < TimeSpan.FromHours(1)) return;
        _lastRetentionSweep = DateTime.UtcNow;
        try {
            var cutoff = DateTime.UtcNow - Retention;
            foreach (var path in Directory.EnumerateFiles(_logDir, "polaris-*.jsonl")) {
                try {
                    var info = new FileInfo(path);
                    if (info.LastWriteTimeUtc < cutoff) {
                        info.Delete();
                    }
                } catch (Exception ex) {
                    Console.Error.WriteLine($"[LogRotator] retention sweep {path}: {ex.Message}");
                }
            }
        } catch (Exception ex) {
            Console.Error.WriteLine($"[LogRotator] retention sweep failed: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}
