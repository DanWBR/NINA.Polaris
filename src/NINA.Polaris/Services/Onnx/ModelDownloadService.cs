// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Polaris.Services.Onnx;

/// <summary>
/// On-demand downloader for ONNX models hosted in a public bucket (e.g.
/// Supabase Storage). For devices / OS images that ship the app WITHOUT the
/// large bundled models, this pulls a model into a writable models directory
/// (see <see cref="OnnxModelRegistry.ResolveDownloadTargetDir"/>), verifies
/// its SHA-256, and rescans the registry so the browser flow keeps working
/// unchanged (it fetches from the server + caches by hash).
///
/// The bucket layout mirrors wwwroot/graxpert/models:
///   {baseUrl}/models-index.json
///   {baseUrl}/{familyDir}/{version}/model.onnx     (familyDir e.g. "nox-color-ai-models")
///
/// models-index.json is an array of entries:
///   [{ "dir": "nox-color-ai-models", "version": "1.0.0",
///      "bytes": 109212345, "sha256": "abc…", "label": "nox colour (FP16)",
///      "url": "(optional absolute override)" }, …]
///
/// Manual, one download at a time. Progress is a single in-memory slot the UI
/// polls via GET /api/onnx/download-status.
/// </summary>
public sealed class ModelDownloadService {
    private readonly IHttpClientFactory _http;
    private readonly ProfileService _profile;
    private readonly OnnxModelRegistry _registry;
    private readonly ILogger<ModelDownloadService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile DownloadState _state = DownloadState.Idle;

    public ModelDownloadService(IHttpClientFactory http, ProfileService profile,
                                OnnxModelRegistry registry,
                                ILogger<ModelDownloadService> logger) {
        _http = http; _profile = profile; _registry = registry; _logger = logger;
    }

    public DownloadState Status => _state;

    /// <summary>Configured when a custom bucket URL is set OR the app ships a
    /// bundled <c>models-index.json</c> (the default, pointing at the public
    /// SourceForge model files). Either way the downloader has a catalogue.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) || File.Exists(BundledIndexPath);

    private string BaseUrl => (_profile.Active?.OnnxModelsBucketUrl ?? "").Trim().TrimEnd('/');

    /// <summary>The bundled catalogue beside the bundled models dir, i.e.
    /// <c>wwwroot/graxpert/models-index.json</c>.</summary>
    private string BundledIndexPath =>
        Path.Combine(Path.GetDirectoryName(_registry.BundledModelsPath) ?? "", "models-index.json");

    /// <summary>Load the catalogue index. When a custom bucket URL is set it is
    /// fetched from <c>{base}/models-index.json</c>; otherwise the bundled
    /// <c>models-index.json</c> is read from disk. Entries carry an absolute
    /// <c>url</c> for hosts (like SourceForge) that don't expose a flat
    /// <c>{base}/{dir}/{version}/model.onnx</c> path.</summary>
    private async Task<List<IndexEntry>> LoadIndexAsync(CancellationToken ct) {
        string json;
        if (!string.IsNullOrWhiteSpace(BaseUrl)) {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            json = await client.GetStringAsync($"{BaseUrl}/models-index.json", ct);
        } else if (File.Exists(BundledIndexPath)) {
            json = await File.ReadAllTextAsync(BundledIndexPath, ct);
        } else {
            throw new InvalidOperationException("No model catalogue available.");
        }
        return JsonSerializer.Deserialize<List<IndexEntry>>(json, JsonOpts) ?? new();
    }

    /// <summary>Load the index and merge each entry with whether it is already
    /// installed locally. Throws if no catalogue is available.</summary>
    public async Task<IReadOnlyList<CatalogEntry>> GetCatalogAsync(CancellationToken ct = default) {
        if (!IsConfigured) throw new InvalidOperationException("No model catalogue available.");
        var entries = await LoadIndexAsync(ct);
        return entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Dir) && !string.IsNullOrWhiteSpace(e.Version))
            .Select(e => new CatalogEntry(
                e.Dir!, e.Version!, e.Label ?? e.Dir!, e.Bytes,
                _registry.IsInstalled(e.Dir!, e.Version!),
                // Canonical manifest family so the browser can merge a
                // downloadable entry into the matching model dropdown.
                OnnxModelRegistry.FamilyForDir(e.Dir!),
                e.Sha256,
                // Absolute URL for the client-proxy path (browser fetches the
                // bytes when the host itself has no internet). Bundled index
                // entries carry an explicit url; a bucket derives it.
                ResolveUrl(e)))
            .ToList();
    }

    /// <summary>Absolute download URL for a catalogue entry: the explicit
    /// <c>url</c> when present (bundled SourceForge index), else the flat
    /// <c>{base}/{dir}/{version}/model.onnx</c> bucket layout.</summary>
    private string? ResolveUrl(IndexEntry e) {
        if (!string.IsNullOrWhiteSpace(e.Url)) return e.Url;
        if (!string.IsNullOrWhiteSpace(BaseUrl))
            return $"{BaseUrl}/{e.Dir}/{e.Version}/model.onnx";
        return null;
    }

    /// <summary>Start a download in the background. Returns false if one is
    /// already running. Progress is in <see cref="Status"/>.</summary>
    public bool TryStart(string dir, string version) {
        if (!IsConfigured) throw new InvalidOperationException("No model catalogue available.");
        if (!_gate.Wait(0)) return false;   // already downloading
        _state = new DownloadState(dir, version, 0, 0, "downloading", null);
        _ = Task.Run(async () => {
            try { await DownloadAsync(dir, version, CancellationToken.None); }
            catch (Exception ex) {
                _logger.LogWarning(ex, "Model download failed: {Dir}/{Ver}", dir, version);
                _state = _state with { State = "failed", Error = ex.Message };
            } finally { _gate.Release(); }
        });
        return true;
    }

    private async Task DownloadAsync(string dir, string version, CancellationToken ct) {
        // Resolve the index entry (for the URL override + sha256 + size).
        var entries = await LoadIndexAsync(ct);
        var entry = entries.FirstOrDefault(e =>
            string.Equals(e.Dir, dir, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Version, version, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{dir}/{version} not in the bucket index.");

        var url = !string.IsNullOrWhiteSpace(entry.Url)
            ? entry.Url!
            : $"{BaseUrl}/{dir}/{version}/model.onnx";

        var targetRoot = _registry.ResolveDownloadTargetDir();
        var destDir = Path.Combine(targetRoot, dir, version);
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, "model.onnx");
        var tmp = dest + ".part";

        var client = _http.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;   // large files; rely on ct
        using (var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)) {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? entry.Bytes;
            _state = _state with { TotalBytes = total };
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            var buf = new byte[1 << 20];   // 1 MB
            long got = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0) {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                got += n;
                _state = _state with { ReceivedBytes = got };
            }

            // Sanity guard for mirror hosts (e.g. SourceForge) that 302 to a
            // mirror and can occasionally hand back a small HTML interstitial
            // instead of the file. With no sha256 to verify, reject a download
            // that came back far smaller than the catalogue's expected size.
            if (entry.Bytes > 0 && got < entry.Bytes / 2) {
                File.Delete(tmp);
                throw new InvalidOperationException(
                    $"Downloaded {got:N0} bytes but expected ~{entry.Bytes:N0} " +
                    "(mirror may have returned an error page). Try again.");
            }
        }

        // Verify SHA-256 when the index provides one.
        if (!string.IsNullOrWhiteSpace(entry.Sha256)) {
            _state = _state with { State = "verifying" };
            await using var fs = File.OpenRead(tmp);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            if (!string.Equals(hash, entry.Sha256!.Trim().ToLowerInvariant(), StringComparison.Ordinal)) {
                File.Delete(tmp);
                throw new InvalidOperationException(
                    $"SHA-256 mismatch (expected {entry.Sha256}, got {hash}).");
            }
        }

        if (File.Exists(dest)) File.Delete(dest);
        File.Move(tmp, dest);
        await _registry.RescanAsync(ct);
        _state = _state with { State = "done" };
        _logger.LogInformation("Downloaded model {Dir}/{Ver} -> {Dest}", dir, version, dest);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() {
        PropertyNameCaseInsensitive = true
    };

    private sealed class IndexEntry {
        public string? Dir { get; set; }
        public string? Version { get; set; }
        public long Bytes { get; set; }
        public string? Sha256 { get; set; }
        public string? Label { get; set; }
        public string? Url { get; set; }
    }
}

public sealed record CatalogEntry(
    string Dir, string Version, string Label, long Bytes, bool Installed,
    string? Family, string? Sha256, string? Url);

public sealed record DownloadState(
    string? Dir, string? Version, long ReceivedBytes, long TotalBytes,
    [property: JsonPropertyName("state")] string State, string? Error) {
    public static DownloadState Idle => new(null, null, 0, 0, "idle", null);
}
