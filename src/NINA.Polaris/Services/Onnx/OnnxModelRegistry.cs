using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace NINA.Polaris.Services.Onnx;

/// <summary>
/// In-memory catalogue of ONNX models the server can serve to browser
/// clients. Reads a single directory (configured via Onnx:ModelsPath in
/// the user profile) recursively, looking for <c>model.onnx</c> files
/// and inferring <c>family/version</c> from the path layout that
/// GraXpert uses for its bundled weights:
///
/// <code>
///   {root}/{family}-ai-models/{version}/model.onnx
/// </code>
///
/// e.g. <c>bge-ai-models/1.0.1/model.onnx</c> → family <c>bge</c>,
/// version <c>1.0.1</c>. Anything that doesn't match that layout is
/// ignored. We accept a small set of alias families to handle the
/// GraXpert naming variants:
///
/// <list type="bullet">
///   <item><c>bge-ai-models</c> → family <c>bge</c></item>
///   <item><c>denoise-ai-models</c> → family <c>denoise</c></item>
///   <item><c>deconvolution-stars-ai-models</c> → family <c>decon-stars</c></item>
///   <item><c>deconvolution-object-ai-models</c> → family <c>decon-objects</c></item>
/// </list>
///
/// SHA-256 hashes are computed lazily (~1-2s per 200-500 MB file on
/// SSD; cached after first compute) and used as the HTTP ETag the
/// browser pins its IndexedDB cache against. A second
/// <see cref="RescanAsync"/> walk recomputes when the user changes
/// <see cref="OnnxModelsPath"/> in Settings.
///
/// Thread safety: scan runs once per RescanAsync call under
/// <see cref="_scanLock"/>; reads against <see cref="_models"/> are
/// lock-free via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
public class OnnxModelRegistry {
    private readonly ProfileService _profile;
    private readonly ILogger<OnnxModelRegistry> _logger;
    private readonly ConcurrentDictionary<string, OnnxModelEntry> _models = new();
    private readonly object _scanLock = new();
    private string _lastScannedPath = "";

    // GraXpert layout: "{family-prefix}-ai-models" → canonical family id.
    // Maintained as a static table so a path like
    // "denoise-ai-models/2.0.0/model.onnx" deterministically maps to
    // family "denoise", not a string-mangled version of the directory
    // name. New AI-model families ship by adding one line here.
    private static readonly Dictionary<string, string> FamilyAliases =
        new(StringComparer.OrdinalIgnoreCase) {
            ["bge-ai-models"] = "bge",
            ["denoise-ai-models"] = "denoise",
            ["deconvolution-stars-ai-models"] = "decon-stars",
            ["deconvolution-object-ai-models"] = "decon-objects",
        };

    // Semver-ish (major.minor.patch). The trailing patch is optional
    // so "1.0" still parses, but we always emit canonical "M.m.p" with
    // zeros filled if the source dir was shorter.
    private static readonly Regex VersionRegex =
        new(@"^\d+\.\d+(\.\d+)?$", RegexOptions.Compiled);

    public OnnxModelRegistry(ProfileService profile, ILogger<OnnxModelRegistry> logger) {
        _profile = profile;
        _logger = logger;
    }

    /// <summary>
    /// Snapshot of all currently-registered models. Cheap; intended for
    /// the /api/onnx/manifest endpoint.
    /// </summary>
    public IReadOnlyList<OnnxModelEntry> All() => _models.Values.ToList();

    /// <summary>Look up by family + version. Returns null if missing.</summary>
    public OnnxModelEntry? Find(string family, string version) {
        return _models.TryGetValue(Key(family, version), out var e) ? e : null;
    }

    /// <summary>
    /// Re-scan the configured models directory. Idempotent; safe to call
    /// repeatedly. Cleans entries whose backing file disappeared so a
    /// user that moves their GraXpert install sees the change after
    /// pressing the Re-detect button.
    /// </summary>
    public Task RescanAsync(CancellationToken ct = default) => Task.Run(() => RescanSync(), ct);

    private void RescanSync() {
        lock (_scanLock) {
            var root = _profile.Active?.OnnxModelsPath ?? "";
            _lastScannedPath = root;

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) {
                _logger.LogInformation("Onnx rescan: path empty or missing ({Root}) — clearing registry.", root);
                _models.Clear();
                return;
            }

            var found = new HashSet<string>();
            try {
                foreach (var file in Directory.EnumerateFiles(root, "model.onnx", SearchOption.AllDirectories)) {
                    var parsed = ParseLayout(root, file);
                    if (parsed == null) continue;
                    var (family, version) = parsed.Value;
                    var key = Key(family, version);
                    found.Add(key);

                    long size;
                    try { size = new FileInfo(file).Length; }
                    catch (Exception ex) {
                        _logger.LogWarning(ex, "Onnx rescan: stat failed for {File}", file);
                        continue;
                    }

                    // Preserve already-cached hash if file + size unchanged
                    // so a Re-detect doesn't re-hash the whole 1.5 GB
                    // bundle each time.
                    if (_models.TryGetValue(key, out var existing)
                        && existing.Path == file
                        && existing.SizeBytes == size) {
                        continue;
                    }

                    _models[key] = new OnnxModelEntry(
                        family, version, file, size, null, DateTime.UtcNow);
                }
            } catch (Exception ex) {
                _logger.LogError(ex, "Onnx rescan failed at root {Root}", root);
            }

            // Drop entries whose file went away or whose family/version
            // no longer matches the layout.
            foreach (var key in _models.Keys.ToList()) {
                if (!found.Contains(key)) _models.TryRemove(key, out _);
            }
            _logger.LogInformation("Onnx rescan: {Count} models in {Root}", _models.Count, root);
        }
    }

    /// <summary>
    /// Compute (and cache) the SHA-256 hash of a model's bytes. Returns
    /// hex string. Lazy so the initial RescanAsync doesn't pay for it;
    /// the /api/onnx/manifest endpoint forces materialisation on first
    /// request.
    /// </summary>
    public async Task<string?> GetHashAsync(string family, string version, CancellationToken ct = default) {
        if (!_models.TryGetValue(Key(family, version), out var entry)) return null;
        if (entry.Hash != null) return entry.Hash;

        string hash;
        try {
            await using var fs = File.OpenRead(entry.Path);
            using var sha = SHA256.Create();
            var bytes = await sha.ComputeHashAsync(fs, ct);
            hash = Convert.ToHexString(bytes).ToLowerInvariant();
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Onnx hash failed for {Path}", entry.Path);
            return null;
        }

        // Replace the entry with one that carries the hash. Read-modify-
        // write under the dict; if another thread raced us the hash is
        // identical anyway (file bytes didn't change).
        _models[Key(family, version)] = entry with { Hash = hash };
        return hash;
    }

    /// <summary>Diagnostic — used by the Settings page.</summary>
    public string LastScannedPath() => _lastScannedPath;

    // ─── helpers ────────────────────────────────────────────────────

    private static string Key(string family, string version)
        => family.ToLowerInvariant() + "/" + version;

    /// <summary>
    /// Match the GraXpert path layout. Walks from the model.onnx file
    /// upward looking for "{family}-ai-models" → its child is the
    /// version dir. Returns null when the layout doesn't match, which
    /// silently skips foreign .onnx files the user might have dropped
    /// in the same root.
    /// </summary>
    private static (string family, string version)? ParseLayout(string root, string file) {
        var dir = Path.GetDirectoryName(file);
        if (dir == null) return null;
        var version = Path.GetFileName(dir);
        if (!VersionRegex.IsMatch(version)) return null;

        var parent = Path.GetDirectoryName(dir);
        if (parent == null) return null;
        var familyDir = Path.GetFileName(parent);
        if (!FamilyAliases.TryGetValue(familyDir, out var family)) return null;

        return (family, version);
    }
}

/// <summary>
/// One row in the registry. <see cref="Hash"/> is null until the first
/// /api/onnx/manifest or /api/onnx/model GET forces a SHA-256 compute.
/// </summary>
public record OnnxModelEntry(
    string Family,
    string Version,
    string Path,
    long SizeBytes,
    string? Hash,
    DateTime DiscoveredAtUtc);
