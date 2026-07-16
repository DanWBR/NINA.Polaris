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

namespace NINA.Polaris.Services.External;

/// <summary>
/// THUMBPACK: on-demand downloader for the full DSO thumbnail set.
///
/// The ~215 MB of DSS2 cutouts (one per catalogued object) is EXCLUDED from the
/// distribution package (NINA.Polaris.csproj) — shipping it took the .deb from
/// ~80 MB to ~450 MB. A small curated subset (Messier + named showpieces) ships
/// bundled in <c>wwwroot/…/dso-thumbs-core</c> so common targets render offline
/// out of the box; the full set is downloaded here, once, into the writable data
/// dir and used automatically thereafter.
///
/// Mirrors <see cref="DssDownloadService"/> (the offline DSS survey downloader):
/// same status/start/cancel shape, same "serve from the downloaded dir, else the
/// bundled dir" resolution, same writable/preflight guards. The difference is the
/// payload — one zip asset from the GitHub release rather than thousands of tiles
/// — so progress is download bytes then extraction entries.
/// </summary>
public sealed class DsoThumbPackService {
    // Default: a stable, version-independent release tag so the ~215 MB asset is
    // uploaded once and every app version downloads the same pack. Overridable via
    // config (DsoThumbPack:Url) for a mirror or a pinned version.
    private const string DefaultUrl =
        "https://github.com/DanWBR/NINA.Polaris/releases/download/data-pack/polaris-dso-thumbs.zip";

    private static readonly HttpClient Http = new() {
        Timeout = TimeSpan.FromMinutes(30)   // a 215 MB pack on a slow field link
    };

    private readonly string _coreDir;   // bundled curated subset (read-only, shipped)
    private readonly string _packDir;   // downloaded full set (writable data dir)
    private readonly string _url;
    private readonly ILogger<DsoThumbPackService> _logger;

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private volatile DsoThumbPackStatus _status = new();

    public DsoThumbPackService(IWebHostEnvironment env, ProfileService profiles,
                               IConfiguration config, ILogger<DsoThumbPackService> logger) {
        _logger = logger;
        var webRoot = env.WebRootPath ?? Directory.GetCurrentDirectory();
        _coreDir = Path.Combine(webRoot, "sky", "data", "skydata", "dso-thumbs-core");
        _packDir = Path.Combine(profiles.DataDir, "sky", "dso-thumbs");
        _url = config.GetValue<string>("DsoThumbPack:Url") ?? DefaultUrl;
    }

    /// <summary>The writable directory the pack extracts into. Exposed so the
    /// static-file middleware can serve <c>/…/dso-thumbs/{slug}.jpg</c> from it.</summary>
    public string PackDir => _packDir;

    /// <summary>The bundled curated subset, the fallback when the full pack isn't
    /// installed. Exposed for the same middleware.</summary>
    public string CoreDir => _coreDir;

    /// <summary>Resolve a thumbnail slug to a file on disk: the downloaded pack
    /// first, then the bundled core subset, else null. The <c>slug</c> is already
    /// sanitised by the caller (middleware / endpoint) against path traversal.</summary>
    public string? Resolve(string slug) {
        var fromPack = Path.Combine(_packDir, slug + ".jpg");
        if (File.Exists(fromPack)) return fromPack;
        var fromCore = Path.Combine(_coreDir, slug + ".jpg");
        if (File.Exists(fromCore)) return fromCore;
        return null;
    }

    /// <summary>Rough "is the full pack installed" test: the marker written after a
    /// successful extract. A partial/aborted extract leaves no marker, so it reads
    /// as not-installed and the download offer stays.</summary>
    public bool IsInstalled() => File.Exists(Path.Combine(_packDir, ".pack-complete"));

    public int CoreCount() => CountJpgs(_coreDir);
    public int PackCount() => CountJpgs(_packDir);

    private static int CountJpgs(string dir) {
        try {
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*.jpg", SearchOption.TopDirectoryOnly).Count()
                : 0;
        } catch { return 0; }
    }

    public DsoThumbPackStatus GetStatus() {
        var s = _status;
        return new DsoThumbPackStatus {
            Running = s.Running,
            Phase = s.Phase,
            BytesDownloaded = s.BytesDownloaded,
            BytesTotal = s.BytesTotal,
            EntriesExtracted = s.EntriesExtracted,
            EntriesTotal = s.EntriesTotal,
            Error = s.Error,
            Installed = IsInstalled(),
            InstalledCount = PackCount(),
            CoreCount = CoreCount(),
            StartedAt = s.StartedAt,
            FinishedAt = s.FinishedAt
        };
    }

    public bool Start() {
        lock (_lock) {
            if (_status.Running) return false;
            _cts = new CancellationTokenSource();
            _status = new DsoThumbPackStatus { Running = true, Phase = "starting", StartedAt = DateTime.UtcNow };
        }
        _ = Task.Run(() => RunAsync(_cts!.Token));
        return true;
    }

    public void Cancel() { lock (_lock) { _cts?.Cancel(); } }

    private async Task RunAsync(CancellationToken ct) {
        var tmpZip = Path.Combine(_packDir, ".pack-download.zip.part");
        try {
            // Preflight: writable data dir. On a packaged install this is the
            // per-user data dir (not wwwroot), so it should be writable — but fail
            // loudly rather than stream 215 MB into a doomed write.
            try {
                Directory.CreateDirectory(_packDir);
                var probe = Path.Combine(_packDir, ".write-probe");
                await File.WriteAllTextAsync(probe, "ok", ct);
                File.Delete(probe);
            } catch (Exception ex) {
                Finish($"Data folder is not writable ({ex.GetType().Name}: {ex.Message}). " +
                       $"Polaris could not write to '{_packDir}'.");
                return;
            }

            // Download to a .part file with byte progress.
            SetPhase("downloading");
            using (var resp = await Http.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, ct)) {
                if (!resp.IsSuccessStatusCode) {
                    Finish($"Download failed: HTTP {(int)resp.StatusCode} from {_url}. " +
                           "The thumbnail pack may not be published yet for this build.");
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

            // Extract with entry progress. Writes directly into _packDir; the
            // completion marker is written LAST so an interrupted extract reads as
            // not-installed and can be retried.
            SetPhase("extracting");
            ct.ThrowIfCancellationRequested();
            using (var zip = ZipFile.OpenRead(tmpZip)) {
                var jpgEntries = zip.Entries.Where(e => e.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)).ToList();
                lock (_lock) _status = _status with { EntriesTotal = jpgEntries.Count };
                var fullPack = Path.GetFullPath(_packDir);
                int done = 0;
                foreach (var entry in jpgEntries) {
                    ct.ThrowIfCancellationRequested();
                    // Flatten to the pack root by file name and guard traversal —
                    // the archive is trusted, but never write outside _packDir.
                    var name = Path.GetFileName(entry.Name);
                    if (string.IsNullOrEmpty(name)) continue;
                    var outPath = Path.GetFullPath(Path.Combine(_packDir, name));
                    if (!outPath.StartsWith(fullPack + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        continue;
                    entry.ExtractToFile(outPath, overwrite: true);
                    if ((++done & 511) == 0) lock (_lock) _status = _status with { EntriesExtracted = done };
                }
                lock (_lock) _status = _status with { EntriesExtracted = done };
            }

            try { File.Delete(tmpZip); } catch { /* leftover .part is harmless */ }
            await File.WriteAllTextAsync(Path.Combine(_packDir, ".pack-complete"),
                DateTime.UtcNow.ToString("o"), CancellationToken.None);
            _logger.LogInformation("DSO thumbnail pack installed: {Count} files in {Dir}",
                PackCount(), _packDir);
            Finish(null);
        } catch (OperationCanceledException) {
            try { File.Delete(tmpZip); } catch { }
            Finish("cancelled");
        } catch (Exception ex) {
            try { File.Delete(tmpZip); } catch { }
            _logger.LogWarning(ex, "DSO thumbnail pack download failed");
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

/// <summary>Observable state for the thumbnail-pack download.</summary>
public sealed record DsoThumbPackStatus {
    public bool Running { get; init; }
    public string Phase { get; init; } = "idle";   // idle|starting|downloading|extracting|done|error|cancelled
    public long BytesDownloaded { get; init; }
    public long BytesTotal { get; init; }
    public int EntriesExtracted { get; init; }
    public int EntriesTotal { get; init; }
    public string? Error { get; init; }
    public bool Installed { get; init; }
    public int InstalledCount { get; init; }
    public int CoreCount { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
}
