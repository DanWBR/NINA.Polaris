using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Polaris.Services;

/// <summary>
/// Persists <see cref="SensorAnalysisResult"/> runs as JSON under
/// <c>{ProfileService.DataDir}/sensor-analysis/</c>, one file per run, so a
/// camera's measured gain/read-noise curve survives restarts and can be
/// re-opened or exported. Mirrors <see cref="BenchmarkResultsStore"/>.
/// </summary>
public class SensorAnalysisStore {
    private readonly ProfileService _profiles;
    private readonly ILogger<SensorAnalysisStore> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new() {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SensorAnalysisStore(ProfileService profiles, ILogger<SensorAnalysisStore> logger) {
        _profiles = profiles;
        _logger = logger;
    }

    private string Dir => Path.Combine(_profiles.DataDir, "sensor-analysis");

    public async Task SaveResultAsync(SensorAnalysisResult result, CancellationToken ct = default) {
        await _saveLock.WaitAsync(ct);
        try {
            Directory.CreateDirectory(Dir);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            var path = Path.Combine(Dir, $"sensor_{stamp}.json");
            for (int n = 2; File.Exists(path); n++)
                path = Path.Combine(Dir, $"sensor_{stamp}_{n}.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, JsonOpts), ct);
        } finally {
            _saveLock.Release();
        }
    }

    /// <summary>Most-recent-first saved runs, optionally filtered to one
    /// camera, capped at <paramref name="limit"/>.</summary>
    public List<SensorAnalysisResult> LoadHistory(string? camera = null, int limit = 50) {
        try {
            if (!Directory.Exists(Dir)) return new();
            var all = Directory.GetFiles(Dir, "sensor_*.json")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .Select(TryRead)
                .OfType<SensorAnalysisResult>();
            if (!string.IsNullOrWhiteSpace(camera))
                all = all.Where(r => string.Equals(r.Camera, camera, StringComparison.OrdinalIgnoreCase));
            return all.Take(Math.Max(1, limit)).ToList();
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Failed to read sensor analysis history");
            return new();
        }
    }

    /// <summary>The latest saved run for a camera, or null.</summary>
    public SensorAnalysisResult? LatestForCamera(string camera) =>
        LoadHistory(camera, 1).FirstOrDefault();

    public byte[] ExportAllJson() =>
        JsonSerializer.SerializeToUtf8Bytes(LoadHistory(null, int.MaxValue), JsonOpts);

    public int ClearHistory() {
        int n = 0;
        try {
            if (!Directory.Exists(Dir)) return 0;
            foreach (var f in Directory.GetFiles(Dir, "sensor_*.json")) {
                try { File.Delete(f); n++; } catch { }
            }
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Failed to clear sensor analysis history");
        }
        return n;
    }

    private SensorAnalysisResult? TryRead(string path) {
        try { return JsonSerializer.Deserialize<SensorAnalysisResult>(File.ReadAllText(path), JsonOpts); }
        catch { return null; }
    }
}
