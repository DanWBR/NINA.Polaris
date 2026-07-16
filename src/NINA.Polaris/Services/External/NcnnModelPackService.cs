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

using System.IO.Compression;
using System.Net.Http;
using NINA.Polaris.Services.Onnx;

namespace NINA.Polaris.Services.External;

/// <summary>
/// THUMBPACK-4: on-demand downloader for the converted ncnn GPU-Vulkan models,
/// which were excluded from the package (THUMBPACK-3) to keep the .deb slim.
///
/// The trick that keeps this simple: <see cref="NcnnInferenceService"/> resolves a
/// model RELATIVE TO ITS ONNX SIBLING — it accepts a "parallel" layout
/// <c>{modelsRoot}/ncnn/{family}-ai-models/{version}/model.ncnn.param</c>. And the
/// onnx models are themselves downloaded (by <see cref="ModelDownloadService"/>)
/// into the writable models root that <see cref="OnnxModelRegistry.ResolveDownloadTargetDir"/>
/// returns. So we extract the ncnn pack into <c>{that root}/ncnn/…</c> and the
/// EXISTING resolver finds it — no resolver change, no static middleware, no path
/// plumbing. Mirror of <see cref="DsoThumbPackService"/> otherwise (status /
/// start / cancel, one zip asset from the fixed data-pack release).
///
/// GPU accel needs both halves: the onnx model must be downloaded+registered for
/// the resolver to derive the ncnn path from it. This pack supplies the ncnn side.
/// </summary>
public sealed class NcnnModelPackService {
    private const string DefaultUrl =
        "https://github.com/DanWBR/NINA.Polaris/releases/download/data-pack/polaris-ncnn-models.zip";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(20) };

    private readonly OnnxModelRegistry _registry;
    private readonly string _url;
    private readonly ILogger<NcnnModelPackService> _logger;

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private volatile NcnnModelPackStatus _status = new();

    public NcnnModelPackService(OnnxModelRegistry registry, IConfiguration config,
                                ILogger<NcnnModelPackService> logger) {
        _registry = registry;
        _logger = logger;
        _url = config.GetValue<string>("NcnnModelPack:Url") ?? DefaultUrl;
    }

    /// <summary>Where the pack extracts to: the ncnn/ subtree of the same writable
    /// models root the onnx downloader targets, so the resolver's parallel layout
    /// finds it.</summary>
    private string NcnnRoot => Path.Combine(_registry.ResolveDownloadTargetDir(), "ncnn");

    /// <summary>True when a completed ncnn pack is installed (marker written last).</summary>
    public bool IsInstalled() => File.Exists(Path.Combine(NcnnRoot, ".pack-complete"));

    public int InstalledModelCount() {
        try {
            return Directory.Exists(NcnnRoot)
                ? Directory.EnumerateFiles(NcnnRoot, "*.bin", SearchOption.AllDirectories).Count()
                : 0;
        } catch { return 0; }
    }

    public NcnnModelPackStatus GetStatus() {
        var s = _status;
        return s with {
            Installed = IsInstalled(),
            InstalledModelCount = InstalledModelCount()
        };
    }

    public bool Start() {
        lock (_lock) {
            if (_status.Running) return false;
            _cts = new CancellationTokenSource();
            _status = new NcnnModelPackStatus { Running = true, Phase = "starting", StartedAt = DateTime.UtcNow };
        }
        _ = Task.Run(() => RunAsync(_cts!.Token));
        return true;
    }

    public void Cancel() { lock (_lock) { _cts?.Cancel(); } }

    private async Task RunAsync(CancellationToken ct) {
        var root = NcnnRoot;
        var tmpZip = Path.Combine(root, ".ncnn-download.zip.part");
        try {
            try {
                Directory.CreateDirectory(root);
                var probe = Path.Combine(root, ".write-probe");
                await File.WriteAllTextAsync(probe, "ok", ct);
                File.Delete(probe);
            } catch (Exception ex) {
                Finish($"Models folder is not writable ({ex.GetType().Name}: {ex.Message}). " +
                       $"Polaris could not write to '{root}'.");
                return;
            }

            SetPhase("downloading");
            using (var resp = await Http.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, ct)) {
                if (!resp.IsSuccessStatusCode) {
                    Finish($"Download failed: HTTP {(int)resp.StatusCode} from {_url}. " +
                           "The GPU model pack may not be published yet for this build.");
                    return;
                }
                var total = resp.Content.Headers.ContentLength ?? 0;
                lock (_lock) _status = _status with { BytesTotal = total };
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(tmpZip, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[1 << 20];
                long got = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0) {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    got += read;
                    lock (_lock) _status = _status with { BytesDownloaded = got };
                }
            }

            // Extract PRESERVING the tree: the zip holds
            // {family}-ai-models/{version}/model.ncnn.{param,bin}, so extracting
            // into NcnnRoot yields {root}/ncnn/{family}/{version}/… — exactly the
            // parallel layout NcnnInferenceService.SiblingNcnn looks for.
            SetPhase("extracting");
            ct.ThrowIfCancellationRequested();
            var fullRoot = Path.GetFullPath(root);
            using (var zip = ZipFile.OpenRead(tmpZip)) {
                var files = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                lock (_lock) _status = _status with { EntriesTotal = files.Count };
                int done = 0;
                foreach (var entry in files) {
                    ct.ThrowIfCancellationRequested();
                    var outPath = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!outPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        continue;   // zip-slip guard
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    entry.ExtractToFile(outPath, overwrite: true);
                    lock (_lock) _status = _status with { EntriesExtracted = ++done };
                }
            }

            try { File.Delete(tmpZip); } catch { }
            await File.WriteAllTextAsync(Path.Combine(root, ".pack-complete"),
                DateTime.UtcNow.ToString("o"), CancellationToken.None);
            // Refresh the registry so the (already-file-based) ncnn resolver has an
            // up-to-date onnx catalogue to derive ncnn paths from on the next run.
            try { await _registry.RescanAsync(CancellationToken.None); } catch { }
            _logger.LogInformation("ncnn GPU model pack installed: {Count} models in {Dir}",
                InstalledModelCount(), root);
            Finish(null);
        } catch (OperationCanceledException) {
            try { File.Delete(tmpZip); } catch { }
            Finish("cancelled");
        } catch (Exception ex) {
            try { File.Delete(tmpZip); } catch { }
            _logger.LogWarning(ex, "ncnn GPU model pack download failed");
            Finish($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void SetPhase(string phase) { lock (_lock) _status = _status with { Phase = phase }; }

    private void Finish(string? error) {
        lock (_lock) {
            _status = _status with {
                Running = false,
                Phase = error == null ? "done" : (error == "cancelled" ? "cancelled" : "error"),
                Error = error == "cancelled" ? null : error,
                FinishedAt = DateTime.UtcNow
            };
        }
    }
}

/// <summary>Observable state for the ncnn GPU model-pack download.</summary>
public sealed record NcnnModelPackStatus {
    public bool Running { get; init; }
    public string Phase { get; init; } = "idle";
    public long BytesDownloaded { get; init; }
    public long BytesTotal { get; init; }
    public int EntriesExtracted { get; init; }
    public int EntriesTotal { get; init; }
    public string? Error { get; init; }
    public bool Installed { get; init; }
    public int InstalledModelCount { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
}
