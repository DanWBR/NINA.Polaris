using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Polaris.Services;

/// <summary>
/// Persists <see cref="BenchmarkResult"/> runs as JSON files under
/// <c>{ProfileService.DataDir}/benchmarks/</c>, one file per run, so the
/// user can keep a history and export it to compare machines. Mirrors the
/// ProfileService JSON conventions (camelCase, indented, save lock).
/// </summary>
public class BenchmarkResultsStore {
    private readonly ProfileService _profiles;
    private readonly ILogger<BenchmarkResultsStore> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new() {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public BenchmarkResultsStore(ProfileService profiles, ILogger<BenchmarkResultsStore> logger) {
        _profiles = profiles;
        _logger = logger;
    }

    private string Dir => Path.Combine(_profiles.DataDir, "benchmarks");

    public async Task SaveResultAsync(BenchmarkResult result, CancellationToken ct = default) {
        await _saveLock.WaitAsync(ct);
        try {
            Directory.CreateDirectory(Dir);
            // Sortable filename. The result also carries its own ISO
            // timestamp; this is just the file ordering key. Append a
            // counter suffix if two saves land in the same millisecond so
            // one never overwrites the other (the '.' before the extension
            // sorts before '_', so the suffixed file stays newest-last in
            // ordinal order, keeping LoadHistory's newest-first correct).
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            var path = Path.Combine(Dir, $"benchmark_{stamp}.json");
            for (int n = 2; File.Exists(path); n++)
                path = Path.Combine(Dir, $"benchmark_{stamp}_{n}.json");
            var json = JsonSerializer.Serialize(result, JsonOpts);
            await File.WriteAllTextAsync(path, json, ct);
        } finally {
            _saveLock.Release();
        }
    }

    /// <summary>Most-recent-first list of saved runs, capped at
    /// <paramref name="limit"/>. Unreadable files are skipped.</summary>
    public List<BenchmarkResult> LoadHistory(int limit = 50) {
        try {
            if (!Directory.Exists(Dir)) return new();
            return Directory.GetFiles(Dir, "benchmark_*.json")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .Take(Math.Max(1, limit))
                .Select(TryRead)
                .OfType<BenchmarkResult>()
                .ToList();
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Failed to read benchmark history");
            return new();
        }
    }

    /// <summary>Whole history as a single JSON array, for download/export.</summary>
    public byte[] ExportAllJson() {
        var all = LoadHistory(int.MaxValue);
        return JsonSerializer.SerializeToUtf8Bytes(all, JsonOpts);
    }

    public int ClearHistory() {
        int n = 0;
        try {
            if (!Directory.Exists(Dir)) return 0;
            foreach (var f in Directory.GetFiles(Dir, "benchmark_*.json")) {
                try { File.Delete(f); n++; } catch { /* held open: skip */ }
            }
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Failed to clear benchmark history");
        }
        return n;
    }

    private BenchmarkResult? TryRead(string path) {
        try {
            return JsonSerializer.Deserialize<BenchmarkResult>(File.ReadAllText(path), JsonOpts);
        } catch {
            return null;
        }
    }
}
