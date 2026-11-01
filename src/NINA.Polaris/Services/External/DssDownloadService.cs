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
using System.Net;

namespace NINA.Polaris.Services.External;

/// <summary>
/// On-demand downloader for the DSS Color HiPS deep-sky imagery, the "real
/// nebulae/galaxies" background of the SKY map. The repo ships HEALPix order
/// 0-3 (~46 MB) as an offline baseline; this service lets the operator pull
/// the higher orders (4 ~110 MB, 5 ~400 MB) from the Settings tab so the sky
/// reaches ASIAIR-grade detail without committing hundreds of MB to git.
///
/// Tiles are fetched from CDS Strasbourg, or from the NASA/IPAC IRSA mirror
/// of the same survey when CDS does not answer, into the same bundled
/// directory the static file middleware serves
/// (<c>wwwroot/sky/data/skydata/surveys/dss</c>), so the moment a tile lands
/// the engine can use it on the next zoom. The download is resumable
/// (existing non-empty tiles are skipped) and cancellable. Mirrors the
/// standalone scripts/fetch-stellarium-dss.sh.
///
/// Attribution: DSS Color, STScI/NASA, HEALPixed by CDS Strasbourg.
/// </summary>
public sealed class DssDownloadService {
    // The same HiPS (ivo://CDS/P/DSS2/color), byte-identical tiles. The first
    // one that answers the preflight serves the run; tiles that fail are
    // retried against every mirror before the run gives up on them.
    internal static readonly string[] Remotes = {
        "https://alasky.cds.unistra.fr/DSS/DSSColor",
        "https://irsa.ipac.caltech.edu/data/hips/CDS/DSS2/color",
    };
    // Shared client: HiPS fetch is thousands of small GETs; reusing one
    // client (and its connection pool) avoids socket exhaustion.
    private static readonly HttpClient Http = new(new SocketsHttpHandler {
        ConnectTimeout = TimeSpan.FromSeconds(15)
    }) {
        Timeout = TimeSpan.FromSeconds(60)
    };

    // Downloads land in a WRITABLE data directory (next to the profiles),
    // never in the install's wwwroot, on a packaged install (.deb under
    // /opt/polaris) wwwroot is root-owned/read-only, so writing there fails
    // with UnauthorizedAccess. Program.cs serves this dir at the same request
    // path the engine fetches (/sky/data/skydata/surveys/dss) with priority
    // over the bundled baseline, so a downloaded tile is picked up live.
    private readonly string _dssDir;
    // The read-only baseline shipped in wwwroot (HEALPix orders 0-3). Used to
    // skip re-downloading bundled tiles and to report the installed order.
    private readonly string _bundledDir;
    private readonly ILogger<DssDownloadService> _logger;
    private readonly object _lock = new();

    private CancellationTokenSource? _cts;
    private volatile DssDownloadStatus _status = new();

    public DssDownloadService(IWebHostEnvironment env, ProfileService profiles, ILogger<DssDownloadService> logger) {
        _logger = logger;
        _bundledDir = Path.Combine(env.WebRootPath ?? Directory.GetCurrentDirectory(),
            "sky", "data", "skydata", "surveys", "dss");
        _dssDir = Path.Combine(profiles.DataDir, "sky", "dss");
    }

    /// <summary>The writable directory downloaded DSS tiles are stored in;
    /// Program.cs serves it at /sky/data/skydata/surveys/dss with priority
    /// over the bundled baseline.</summary>
    public string DownloadDir => _dssDir;

    /// <summary>12 * 4^order tiles per HEALPix order.</summary>
    private static long TilesAt(int order) => 12L * (long)Math.Pow(4, order);
    private static long CumulativeTiles(int maxOrder) {
        long t = 0;
        for (int o = 0; o <= maxOrder; o++) t += TilesAt(o);
        return t;
    }

    /// <summary>What is on disk: the highest order up to which every order
    /// holds all of its tiles (-1 when nothing is), and the order above it
    /// when that one is only partly there.</summary>
    internal readonly record struct DiskScan(int InstalledOrder, int PartialOrder, long PartialTiles);

    // Counting ~16k files on an SD card is not free and the Settings card
    // polls every 1.5 s during a download, so a scan is reused for a while.
    private DiskScan? _scan;
    private DateTime _scanAt;

    private DiskScan Scan() {
        lock (_lock) {
            if (_scan is { } s && DateTime.UtcNow - _scanAt < TimeSpan.FromSeconds(10)) return s;
        }
        var fresh = ScanOrders(_dssDir, _bundledDir);
        lock (_lock) { _scan = fresh; _scanAt = DateTime.UtcNow; }
        return fresh;
    }

    /// <summary>Highest order up to which every order is complete on disk.
    /// -1 when nothing is bundled.</summary>
    public int InstalledMaxOrder() => Scan().InstalledOrder;

    internal static DiskScan ScanOrders(string dssDir, string bundledDir) {
        for (int o = 0; o <= 8; o++) {
            long have = CountTiles(dssDir, bundledDir, o);
            if (have >= TilesAt(o)) continue;
            return new DiskScan(o - 1, have > 0 ? o : -1, have);
        }
        return new DiskScan(8, -1, 0);
    }

    /// <summary>Distinct non-empty tiles of one order across the writable
    /// dir and the bundled baseline (a tile may live in either).</summary>
    internal static long CountTiles(string dssDir, string bundledDir, int order) {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { dssDir, bundledDir }) {
            var dir = new DirectoryInfo(Path.Combine(root, $"Norder{order}"));
            if (!dir.Exists) continue;
            foreach (var f in dir.EnumerateFiles("Npix*.jpg", SearchOption.AllDirectories))
                if (f.Length > 0) seen.Add(f.Name);
        }
        return seen.Count;
    }

    public DssDownloadStatus GetStatus() {
        var s = _status;
        var disk = Scan();
        return new DssDownloadStatus {
            Running = s.Running,
            TargetOrder = s.TargetOrder,
            TotalTiles = s.TotalTiles,
            CompletedTiles = s.CompletedTiles,
            FailedTiles = s.FailedTiles,
            Error = s.Error,
            InstalledOrder = disk.InstalledOrder,
            PartialOrder = disk.PartialOrder,
            PartialTiles = disk.PartialTiles,
            PartialTotal = disk.PartialOrder >= 0 ? TilesAt(disk.PartialOrder) : 0,
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

    private enum FetchResult { Ok, Missing, Error }

    private async Task RunAsync(int maxOrder, CancellationToken ct) {
        long ok = 0, missing = 0, errors = 0;
        try {
            // Preflight 1: the destination must be writable. On a packaged
            // install wwwroot can be read-only / root-owned; fail loudly
            // instead of "downloading" thousands of doomed writes.
            try {
                Directory.CreateDirectory(_dssDir);
                var probe = Path.Combine(_dssDir, ".write-probe");
                await File.WriteAllTextAsync(probe, "ok", ct);
                File.Delete(probe);
            } catch (Exception ex) {
                Finish(0, 0, $"Sky data folder is not writable ({ex.GetType().Name}: {ex.Message}). "
                    + $"Polaris could not write to '{_dssDir}'. Check that the service user owns "
                    + "its data directory, or use scripts/fetch-stellarium-dss.sh.");
                return;
            }

            // Preflight 2: we need internet to fetch tiles. Probe a known
            // order-4 tile fresh (bypassing the already-present check) on
            // each mirror in turn and use the first that answers. No answer
            // at all is the common field failure: downloading at the
            // telescope with no connection.
            string? remote = null;
            var probeErrors = new List<string>();
            foreach (var candidate in Remotes) {
                try {
                    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    probeCts.CancelAfter(TimeSpan.FromSeconds(20));
                    using var probe = await Http.GetAsync($"{candidate}/Norder4/Dir0/Npix0.jpg", probeCts.Token);
                    if (probe.IsSuccessStatusCode) { remote = candidate; break; }
                    probeErrors.Add($"{new Uri(candidate).Host}: HTTP {(int)probe.StatusCode}");
                } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                    probeErrors.Add($"{new Uri(candidate).Host}: timed out");
                } catch (HttpRequestException ex) {
                    probeErrors.Add($"{new Uri(candidate).Host}: {ex.Message}");
                }
            }
            if (remote == null) {
                Finish(0, 0, "Could not reach the sky imagery servers, an internet connection is "
                    + $"required to download (you can be offline afterwards). [{string.Join("; ", probeErrors)}]");
                return;
            }
            if (remote != Remotes[0])
                _logger.LogInformation("DSS download: {Primary} did not answer, using {Mirror}",
                    new Uri(Remotes[0]).Host, new Uri(remote).Host);

            // Root metadata first (cheap, makes the survey self-describing).
            // Force these into the WRITABLE dir even if the bundled baseline
            // already has a copy: the sky engine decides LOCAL-vs-remote by
            // probing <dss>/properties, and the writable dir is served with
            // priority. If properties only lived in the (maybe-not-shipped)
            // bundle, an offline Pi could silently fall back to remote CDS and
            // render nothing. A guaranteed local properties makes the offline
            // path deterministic.
            foreach (var meta in new[] { "properties", "Moc.fits" }) {
                await FetchAsync($"{remote}/{meta}", Path.Combine(_dssDir, meta), ct, forceLocal: true);
            }

            // Tiles that fail on the first pass, as paths relative to the
            // survey root ("Norder5/Dir0/Npix123.jpg").
            var failed = new ConcurrentBag<string>();
            // A mirror that stops answering mid-run would make every tile wait
            // out its timeout; after a run of straight failures the rest of
            // the pass moves to the next mirror.
            int streak = 0;
            var switchLock = new object();
            using var gate = new SemaphoreSlim(8);
            for (int order = 0; order <= maxOrder; order++) {
                if (order <= 3) {
                    await FetchAsync($"{remote}/Norder{order}/Allsky.jpg",
                        Path.Combine(_dssDir, $"Norder{order}", "Allsky.jpg"), ct);
                }
                long n = TilesAt(order);
                var tasks = new List<Task>();
                for (long npix = 0; npix < n; npix++) {
                    ct.ThrowIfCancellationRequested();
                    long dir = (npix / 10000) * 10000;
                    var rel = $"Norder{order}/Dir{dir}/Npix{npix}.jpg";
                    await gate.WaitAsync(ct);
                    tasks.Add(Task.Run(async () => {
                        try {
                            var used = remote;
                            var r = await FetchAsync($"{used}/{rel}", Path.Combine(_dssDir, rel), ct);
                            if (r == FetchResult.Ok) Interlocked.Increment(ref ok);
                            else if (r == FetchResult.Missing) Interlocked.Increment(ref missing);
                            else { Interlocked.Increment(ref errors); failed.Add(rel); }
                            if (r != FetchResult.Error) Interlocked.Exchange(ref streak, 0);
                            else if (Interlocked.Increment(ref streak) >= 40) {
                                lock (switchLock) {
                                    if (remote == used) {
                                        remote = Remotes[(Array.IndexOf(Remotes, used) + 1) % Remotes.Length];
                                        streak = 0;
                                        _logger.LogWarning("DSS download: {Host} keeps failing, switching to {Next}",
                                            new Uri(used).Host, new Uri(remote).Host);
                                    }
                                }
                            }
                            UpdateProgress(Interlocked.Read(ref ok)
                                + Interlocked.Read(ref missing)
                                + Interlocked.Read(ref errors),
                                Interlocked.Read(ref errors));
                        } finally { gate.Release(); }
                    }, ct));
                }
                await Task.WhenAll(tasks);
            }

            // Second chance for the failures, on every mirror in turn, with a
            // pause so a server that was throttling us has time to recover.
            for (int round = 0; round < 2 && !failed.IsEmpty; round++) {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                var retry = failed.ToArray();
                failed.Clear();
                var tasks = retry.Select(async rel => {
                    await gate.WaitAsync(ct);
                    try {
                        var r = FetchResult.Error;
                        foreach (var mirror in Remotes.OrderBy(m => m == remote ? 0 : 1)) {
                            r = await FetchAsync($"{mirror}/{rel}", Path.Combine(_dssDir, rel), ct);
                            if (r != FetchResult.Error) break;
                        }
                        if (r == FetchResult.Error) { failed.Add(rel); return; }
                        Interlocked.Decrement(ref errors);
                        if (r == FetchResult.Ok) Interlocked.Increment(ref ok);
                        else Interlocked.Increment(ref missing);
                        UpdateProgress(Interlocked.Read(ref ok)
                            + Interlocked.Read(ref missing)
                            + Interlocked.Read(ref errors),
                            Interlocked.Read(ref errors));
                    } finally { gate.Release(); }
                }).ToList();
                await Task.WhenAll(tasks);
            }

            // Tiles that never arrived leave the order incomplete; the
            // Settings card then shows it as partly installed, and pressing
            // the button again fetches only what is missing.
            string? err = null;
            if (ok == 0 && errors > 0)
                err = "Download failed, the connection appears to have dropped (no tiles were saved).";
            else if (errors > 0)
                err = $"{errors:N0} tiles could not be downloaded. Press the button again to fetch the missing ones.";
            Finish(ok + missing + errors, errors, err);
            _logger.LogInformation("DSS download done: order {Order}, {Ok} saved, {Missing} sparse, {Err} errors",
                maxOrder, ok, missing, errors);
        } catch (OperationCanceledException) {
            Finish(ok + missing + errors, errors, "cancelled");
            _logger.LogInformation("DSS download cancelled at {Done} tiles", ok + missing + errors);
        } catch (Exception ex) {
            Finish(ok + missing + errors, errors, $"{ex.GetType().Name}: {ex.Message}");
            _logger.LogError(ex, "DSS download failed");
        }
    }

    // Ok    = downloaded now, or already present (in the writable dir OR the
    //         bundled wwwroot baseline, so we never re-download orders 0-3).
    // Missing = upstream 404 / empty (sparse survey, normal, not fatal).
    // Error = network/IO failure (the caller treats a run of these as fatal).
    private async Task<FetchResult> FetchAsync(string url, string outPath, CancellationToken ct,
            bool forceLocal = false) {
        try {
            if (File.Exists(outPath) && new FileInfo(outPath).Length > 0) return FetchResult.Ok;
            // Already shipped in the read-only baseline? Don't re-fetch it,
            // unless forceLocal, used for the survey metadata that MUST exist
            // in the writable dir for the engine's local-source probe.
            if (!forceLocal) {
                var rel = Path.GetRelativePath(_dssDir, outPath);
                var bundled = Path.Combine(_bundledDir, rel);
                if (File.Exists(bundled) && new FileInfo(bundled).Length > 0) return FetchResult.Ok;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            using var resp = await Http.GetAsync(url, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return FetchResult.Missing;
            if (!resp.IsSuccessStatusCode) return FetchResult.Error;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0) return FetchResult.Missing;
            var tmp = outPath + ".part";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, outPath, overwrite: true);
            return FetchResult.Ok;
        } catch (OperationCanceledException) {
            throw;
        } catch {
            return FetchResult.Error;
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
            _scan = null;
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
    /// <summary>The order above <see cref="InstalledOrder"/> when some of its
    /// tiles are on disk but not all; -1 otherwise.</summary>
    public int PartialOrder { get; set; } = -1;
    public long PartialTiles { get; set; }
    public long PartialTotal { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}
