using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace NINA.Relay.Server;

/// <summary>
/// Append-only audit log of every proxied request. Records land in a
/// JSON-lines file (one event per line) so they're trivially grep-able and
/// streamable. The default location is <c>audit.log</c> next to the
/// executable; override via <c>Audit:Path</c>.
///
/// Records are kept in an in-memory ring buffer (default 5000) so the
/// admin UI can show recent traffic without re-reading the file. Writes
/// to disk are debounced and batched.
///
/// Set <c>Audit:Enabled=false</c> to disable entirely (useful for
/// load-testing or strict privacy deployments).
/// </summary>
public class AuditLog : IDisposable {
    private readonly ILogger<AuditLog> _logger;
    private readonly bool _enabled;
    private readonly string _path;
    private readonly long _maxFileBytes;
    private readonly int _ringSize;
    private readonly ConcurrentQueue<AuditRecord> _ring = new();
    private readonly Channel<AuditRecord> _writeQueue;
    private readonly Task _writer;
    private readonly CancellationTokenSource _cts = new();

    public AuditLog(IConfiguration config, ILogger<AuditLog> logger) {
        _logger = logger;
        _enabled = config.GetValue("Audit:Enabled", true);
        _path = config.GetValue<string?>("Audit:Path") ?? "audit.log";
        _maxFileBytes = config.GetValue("Audit:MaxFileBytes", 50L * 1024 * 1024); // 50 MB
        _ringSize = config.GetValue("Audit:RingBufferSize", 5000);

        if (_enabled) {
            // Ensure the parent dir exists once at startup so writes don't fail later
            try {
                var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Could not create audit log dir for {Path}", _path);
            }
        }

        _writeQueue = Channel.CreateBounded<AuditRecord>(new BoundedChannelOptions(4096) {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _writer = Task.Run(WriterLoopAsync);
    }

    public bool Enabled => _enabled;
    public string Path => _path;

    /// <summary>Enqueue a record. Non-blocking; drops on overflow.</summary>
    public void Record(AuditRecord rec) {
        if (!_enabled) return;
        // Ring buffer (in-memory)
        _ring.Enqueue(rec);
        while (_ring.Count > _ringSize && _ring.TryDequeue(out _)) { }
        // Disk write (background)
        _writeQueue.Writer.TryWrite(rec);
    }

    /// <summary>Recent records (newest last). Optional tenant filter.</summary>
    public IEnumerable<AuditRecord> Snapshot(string? tenant = null, int? limit = null) {
        IEnumerable<AuditRecord> q = _ring.ToArray();
        if (!string.IsNullOrEmpty(tenant))
            q = q.Where(r => r.Tenant.Equals(tenant, StringComparison.OrdinalIgnoreCase));
        var arr = q.ToArray();
        if (limit.HasValue && limit.Value > 0 && arr.Length > limit.Value)
            arr = arr[^limit.Value..];
        return arr;
    }

    private async Task WriterLoopAsync() {
        var batch = new List<AuditRecord>(64);
        var ct = _cts.Token;
        try {
            while (!ct.IsCancellationRequested && await _writeQueue.Reader.WaitToReadAsync(ct)) {
                batch.Clear();
                while (batch.Count < 256 && _writeQueue.Reader.TryRead(out var rec)) {
                    batch.Add(rec);
                }
                if (batch.Count > 0) await FlushAsync(batch);
            }
        } catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task FlushAsync(IList<AuditRecord> records) {
        try {
            RotateIfNeeded();
            await using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var sw = new StreamWriter(stream, Encoding.UTF8);
            foreach (var r in records) {
                await sw.WriteLineAsync(JsonSerializer.Serialize(r));
            }
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Audit flush of {Count} records failed", records.Count);
        }
    }

    private void RotateIfNeeded() {
        try {
            var fi = new FileInfo(_path);
            if (!fi.Exists || fi.Length < _maxFileBytes) return;
            // Rotate: audit.log → audit.log.1 (overwrites any previous .1)
            var rotated = _path + ".1";
            if (File.Exists(rotated)) File.Delete(rotated);
            File.Move(_path, rotated);
            _logger.LogInformation("Audit log rotated at {Bytes} bytes → {Rotated}", fi.Length, rotated);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Audit log rotation failed");
        }
    }

    public void Dispose() {
        _cts.Cancel();
        try { _writer.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}

/// <summary>One proxied request observed by <see cref="PublicProxy"/>.</summary>
public class AuditRecord {
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Tenant { get; set; } = "";
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public int Status { get; set; }
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
    public int DurationMs { get; set; }
    public string? RemoteIp { get; set; }
    public string? UserAgent { get; set; }
    /// <summary>Optional reason for non-2xx outcomes (rate_limited, quota_exhausted, tunnel_down, …).</summary>
    public string? Outcome { get; set; }
}

