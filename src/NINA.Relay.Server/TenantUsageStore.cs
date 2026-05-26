using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Relay.Server;

/// <summary>
/// Persistent per-tenant usage counters. Tracks total bytes transferred in
/// the current UTC month so the monthly-quota check survives server restarts.
/// State is flushed to <c>tenant-state.json</c> on each charge (debounced).
///
/// Counters auto-reset when the month rolls over, the first charge of a
/// new UTC month sees the stale <see cref="UsageRecord.MonthKey"/> and
/// resets <see cref="UsageRecord.BytesThisMonth"/> to zero before adding.
/// </summary>
public class TenantUsageStore : IDisposable {
    private readonly string _path;
    private readonly ILogger<TenantUsageStore> _logger;
    private readonly ConcurrentDictionary<string, UsageRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writeGate = new();
    private DateTime _lastWrite = DateTime.MinValue;

    public TenantUsageStore(IConfiguration config, ILogger<TenantUsageStore> logger) {
        _logger = logger;
        _path = config.GetValue<string?>("Relay:UsageStateFile") ?? "tenant-state.json";
        Load();
    }

    /// <summary>Snapshot for a tenant (creates a fresh zero record if unseen).</summary>
    public UsageRecord Get(string token) {
        return _records.GetOrAdd(token, _ => new UsageRecord {
            MonthKey = CurrentMonthKey(),
            BytesThisMonth = 0
        });
    }

    /// <summary>
    /// Charge <paramref name="bytes"/> against the tenant's monthly counter.
    /// Returns the post-charge byte total. Triggers a debounced flush to disk.
    /// </summary>
    public long Charge(string token, long bytes) {
        var rec = Get(token);
        var month = CurrentMonthKey();
        lock (rec) {
            if (rec.MonthKey != month) {
                rec.MonthKey = month;
                rec.BytesThisMonth = 0;
            }
            rec.BytesThisMonth += bytes;
        }
        ScheduleFlush();
        return rec.BytesThisMonth;
    }

    /// <summary>
    /// Returns the per-tenant byte total for the current month without changing it.
    /// </summary>
    public long BytesThisMonth(string token) {
        var rec = Get(token);
        var month = CurrentMonthKey();
        lock (rec) {
            return rec.MonthKey == month ? rec.BytesThisMonth : 0;
        }
    }

    /// <summary>Manual reset (e.g. operator forgiving a tenant mid-month).</summary>
    public void Reset(string token) {
        if (_records.TryGetValue(token, out var rec)) {
            lock (rec) {
                rec.MonthKey = CurrentMonthKey();
                rec.BytesThisMonth = 0;
            }
            Flush();
        }
    }

    public IReadOnlyDictionary<string, UsageRecord> Snapshot() =>
        _records.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    private static string CurrentMonthKey() => DateTime.UtcNow.ToString("yyyy-MM");

    private void Load() {
        try {
            if (!File.Exists(_path)) return;
            var text = File.ReadAllText(_path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, UsageRecord>>(text);
            if (parsed != null) {
                foreach (var (k, v) in parsed) _records[k] = v;
                _logger.LogInformation("Loaded usage state for {Count} tenants from {Path}", parsed.Count, _path);
            }
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Could not load usage state from {Path}; starting fresh", _path);
        }
    }

    private void ScheduleFlush() {
        var now = DateTime.UtcNow;
        if ((now - _lastWrite).TotalSeconds < 5) return; // debounce
        _lastWrite = now;
        Task.Run(Flush);
    }

    private void Flush() {
        try {
            lock (_writeGate) {
                var snapshot = _records.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.Never
                });
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _path, overwrite: true);
            }
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to flush usage state to {Path}", _path);
        }
    }

    public void Dispose() {
        Flush();
    }
}

public class UsageRecord {
    /// <summary>UTC year-month bucket, e.g. "2026-05". Rolling key for monthly reset.</summary>
    public string MonthKey { get; set; } = "";

    /// <summary>Total bytes transferred (req + resp combined) in the current month.</summary>
    public long BytesThisMonth { get; set; }
}
