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

namespace NINA.Polaris.Services.External;

/// <summary>
/// On-demand downloader for the DSS Color HiPS deep-sky imagery, the "real
/// nebulae/galaxies" background of the SKY map. The repo ships HEALPix order
/// 0-3 (~46 MB) as an offline baseline; this service lets the operator pull
/// the higher orders (4 ~110 MB, 5 ~400 MB) from the Settings tab so the sky
/// reaches ASIAIR-grade detail without committing hundreds of MB to git.
///
/// Tiles are fetched from CDS Strasbourg into the same bundled directory the
/// static file middleware serves (<c>wwwroot/sky/data/skydata/surveys/dss</c>),
/// so the moment a tile lands the engine can use it on the next zoom. The
/// download is resumable (existing non-empty tiles are skipped) and
/// cancellable. Mirrors the standalone scripts/fetch-stellarium-dss.sh.
///
/// Attribution: DSS Color, STScI/NASA, HEALPixed by CDS Strasbourg.
/// </summary>
public sealed class DssDownloadService {
    private const string Remote = "https://alasky.cds.unistra.fr/DSS/DSSColor";
    // Shared client: HiPS fetch is thousands of small GETs; reusing one
    // client (and its connection pool) avoids socket exhaustion.
    private static readonly HttpClient Http = new() {
        Timeout = TimeSpan.FromSeconds(60)
    };

    private readonly string _dssDir;
    private readonly ILogger<DssDownloadService> _logger;
    private readonly object _lock = new();

    private CancellationTokenSource? _cts;
    private volatile DssDownloadStatus _status = new();

    public DssDownloadService(IWebHostEnvironment env, ILogger<DssDownloadService> logger) {
        _logger = logger;
        _dssDir = Path.Combine(env.WebRootPath ?? Directory.GetCurrentDirectory(),
            "sky", "data", "skydata", "surveys", "dss");
    }

    /// <summary>12 * 4^order tiles per HEALPix order.</summary>
    private static long TilesAt(int order) => 12L * (long)Math.Pow(4, order);
    private static long CumulativeTiles(int maxOrder) {
        long t = 0;
        for (int o = 0; o <= maxOrder; o++) t += TilesAt(o);
        return t;
    }

    /// <summary>Highest contiguous order already present on disk (a Norder{n}
    /// dir that holds at least one tile). -1 when nothing is bundled.</summary>
    public int InstalledMaxOrder() {
        int max = -1;
        for (int o = 0; o <= 8; o++) {
            var dir = Path.Combine(_dssDir, $"Norder{o}");
            bool has = Directory.Exists(dir) &&
                       Directory.EnumerateFiles(dir, "*.jpg", SearchOption.AllDirectories).Any();
            if (has) max = o; else break;
        }
        return max;
    }

    public DssDownloadStatus GetStatus() {
        var s = _status;
        return new DssDownloadStatus {
            Running = s.Running,
            TargetOrder = s.TargetOrder,
            TotalTiles = s.TotalTiles,
            CompletedTiles = s.CompletedTiles,
            FailedTiles = s.FailedTiles,
            Error = s.Error,
            InstalledOrder = InstalledMaxOrder(),
            StartedAt = s.StartedAt,
            FinishedAt = s.FinishedAt
        };
    }

    /// <summary>Begin (or refuse, if already running) a download up to
    /// <paramref name="maxOrder"/>. Returns false if a job is in flight.</summary>
    public bool Start(int maxOrder) {
        if (maxOrder < 0 || maxOrder > 6) throw new ArgumentOutOfRangeException(nameof(maxOrder));
        lock (_lock) {
            if (_status.Running) return false;
            _cts = new CancellationTokenSource();
            _status = new DssDownloadStatus {
                Running = true,
                TargetOrder = maxOrder,
                TotalTiles = CumulativeTiles(maxOrder),
                CompletedTiles = 0,
                FailedTiles = 0,
                StartedAt = DateTime.UtcNow
            };
        }
        _ = Task.Run(() => RunAsync(maxOrder, _cts!.Token));
        return true;
    }

    public void Cancel() {
        lock (_lock) { _cts?.Cancel(); }
    }

    private async Task RunAsync(int maxOrder, CancellationToken ct) {
        long completed = 0, failed = 0;
        try {
            Directory.CreateDirectory(_dssDir);
            // Root metadata first (cheap, makes the survey self-describing).
            foreach (var meta in new[] { "properties", "Moc.fits" }) {
                await FetchAsync($"{Remote}/{meta}", Path.Combine(_dssDir, meta), ct);
            }

            using var gate = new SemaphoreSlim(8);
            for (int order = 0; order <= maxOrder; order++) {
                if (order <= 3) {
                    await FetchAsync($"{Remote}/Norder{order}/Allsky.jpg",
                        Path.Combine(_dssDir, $"Norder{order}", "Allsky.jpg"), ct);
                }
                long n = TilesAt(order);
                var tasks = new List<Task>();
                for (long npix = 0; npix < n; npix++) {
                    ct.ThrowIfCancellationRequested();
                    long dir = (npix / 10000) * 10000;
                    var url = $"{Remote}/Norder{order}/Dir{dir}/Npix{npix}.jpg";
                    var outPath = Path.Combine(_dssDir, $"Norder{order}", $"Dir{dir}", $"Npix{npix}.jpg");
                    await gate.WaitAsync(ct);
                    tasks.Add(Task.Run(async () => {
                        try {
                            bool ok = await FetchAsync(url, outPath, ct);
                            Interlocked.Increment(ref completed);
                            if (!ok) Interlocked.Increment(ref failed);
                            UpdateProgress(Interlocked.Read(ref completed), Interlocked.Read(ref failed));
                        } finally { gate.Release(); }
                    }, ct));
                }
                await Task.WhenAll(tasks);
            }
            Finish(completed, failed, null);
            _logger.LogInformation("DSS download complete: order {Order}, {Done} tiles ({Failed} missing)",
                maxOrder, completed, failed);
        } catch (OperationCanceledException) {
            Finish(completed, failed, "cancelled");
            _logger.LogInformation("DSS download cancelled at {Done} tiles", completed);
        } catch (Exception ex) {
            Finish(completed, failed, $"{ex.GetType().Name}: {ex.Message}");
            _logger.LogError(ex, "DSS download failed");
        }
    }

    // Returns true on a real download (or already-present), false when the
    // tile is genuinely missing upstream (sparse survey — normal, not fatal).
    private static async Task<bool> FetchAsync(string url, string outPath, CancellationToken ct) {
        try {
            if (File.Exists(outPath) && new FileInfo(outPath).Length > 0) return true;
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return false;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0) return false;
            var tmp = outPath + ".part";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, outPath, overwrite: true);
            return true;
        } catch (OperationCanceledException) {
            throw;
        } catch {
            return false;
        }
    }

    private void UpdateProgress(long completed, long failed) {
        var s = _status;
        s.CompletedTiles = completed;
        s.FailedTiles = failed;
    }

    private void Finish(long completed, long failed, string? error) {
        lock (_lock) {
            var s = _status;
            s.CompletedTiles = completed;
            s.FailedTiles = failed;
            s.Running = false;
            s.Error = error;
            s.FinishedAt = DateTime.UtcNow;
        }
    }
}

/// <summary>Snapshot of the DSS download for the Settings UI poll.</summary>
public sealed class DssDownloadStatus {
    public bool Running { get; set; }
    public int TargetOrder { get; set; }
    public long TotalTiles { get; set; }
    public long CompletedTiles { get; set; }
    public long FailedTiles { get; set; }
    public string? Error { get; set; }
    public int InstalledOrder { get; set; } = -1;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}
