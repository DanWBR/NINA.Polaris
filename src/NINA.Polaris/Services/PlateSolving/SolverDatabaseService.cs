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

using System.Diagnostics;
using System.Text.Json;

namespace NINA.Polaris.Services.PlateSolving;

/// <summary>
/// Downloads the star databases / index files the solvers need and installs
/// them.
///
/// <para>Split of privilege: the download runs here, unprivileged, into the
/// polaris cache. The move into <c>/opt/astap</c> or the astrometry.net index
/// directory is done by <c>polaris-solverdb.service</c>, started with
/// <c>systemctl start</c>, which the polaris user is allowed to do without a
/// password through the same manage-units PolicyKit rule the self-update
/// already uses. No new policy file, and the app never runs as root.</para>
///
/// <para>One download at a time, and the state is a snapshot the UI polls:
/// these are hundreds of megabytes over an observatory's uplink, so progress
/// matters and a second concurrent pull would just halve both.</para>
/// </summary>
public sealed class SolverDatabaseService {
    private const string StageDir = "/home/polaris/.cache/polaris-solverdb";
    private const string InstallUnit = "polaris-solverdb.service";
    private const string InstallLog = "/tmp/polaris-solverdb.log";

    private readonly IHttpClientFactory _http;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<SolverDatabaseService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;

    public SolverDatabaseService(IHttpClientFactory http, IWebHostEnvironment env,
                                 ILogger<SolverDatabaseService> logger) {
        _http = http;
        _env = env;
        _logger = logger;
    }

    public sealed record JobState(string Id, string Target, long ReceivedBytes, long TotalBytes,
                                  string State, string? Error) {
        public static readonly JobState Idle = new("", "", 0, 0, "idle", null);
    }

    public JobState State { get; private set; } = JobState.Idle;

    // ---- catalogue ----

    private string CataloguePath =>
        Path.Combine(_env.WebRootPath ?? "wwwroot", "data", "platesolve-databases.json");

    public async Task<JsonDocument> LoadCatalogueAsync(CancellationToken ct) {
        await using var fs = File.OpenRead(CataloguePath);
        return await JsonDocument.ParseAsync(fs, cancellationToken: ct);
    }

    /// <summary>ASTAP databases already unpacked, by the prefix of their data
    /// files (d80_1234.1476 -> "D80"). Reading the directory is the only
    /// truthful answer: a database can also have been installed by hand or by
    /// the vendor's own .deb.</summary>
    public IReadOnlyList<string> InstalledAstapDatabases() {
        try {
            var dir = "/opt/astap";
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            // An ASTAP database is a set of numbered files like d80_0101.1476,
            // so the id is the prefix of a file whose extension is numeric.
            // Keying on "has an underscore" alone reported ACKNOWLEDGEMENT,
            // UNPROCESSED and VARIABLE as installed databases, because the
            // folder also holds acknowledgement_of_databases.txt and friends.
            return Directory.EnumerateFiles(dir)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .Where(n => {
                    var us = n.IndexOf('_');
                    if (us < 2) return false;
                    var ext = Path.GetExtension(n);
                    // ".1476", ".290" and so on: the database shard number.
                    return ext.Length > 1 && ext[1..].All(char.IsDigit);
                })
                .Select(n => n[..n.IndexOf('_')].ToUpperInvariant())
                .Distinct()
                .OrderBy(x => x)
                .ToList();
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Could not list installed ASTAP databases");
            return Array.Empty<string>();
        }
    }

    /// <summary>astrometry.net index files present, as their scale numbers.</summary>
    public IReadOnlyList<int> InstalledAstrometryScales() {
        var scales = new HashSet<int>();
        foreach (var dir in new[] { "/usr/share/astrometry", "/usr/local/astrometry/data" }) {
            try {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "index-*.fits")) {
                    var s = ScaleOf(Path.GetFileNameWithoutExtension(f));
                    if (s >= 0) scales.Add(s);
                }
            } catch { /* unreadable directory is simply "nothing installed" */ }
        }
        return scales.OrderBy(x => x).ToList();
    }

    /// <summary>The scale band an index file belongs to, or -1.
    ///
    /// Two namings are in the wild and only one was understood:
    ///   index-4209.fits        the 42xx series, band = the last two digits
    ///   index-4209-03.fits     the same, split into healpix tiles
    ///   index-2mass-07.fits    the 2mass build, band = the number after the
    ///   index-2mass-07-03.fits catalogue name
    /// A host carrying a complete 2mass set therefore reported nothing
    /// installed, and the card offered to download what was already there.</summary>
    internal static int ScaleOf(string fileNameWithoutExtension) {
        var parts = fileNameWithoutExtension.Split('-');
        foreach (var part in parts.Skip(1)) {
            if (!int.TryParse(part, out var n)) continue;   // "2mass" and the like
            // A 4-digit series number carries the band in its last two digits;
            // a bare 2-digit group already IS the band. Longer groups are
            // healpix tile numbers, which only appear after the band.
            if (part.Length == 4) return n % 100;
            if (part.Length <= 2) return n;
            return -1;
        }
        return -1;
    }

    // ---- install ----

    /// <summary>Stage a download and hand it to the privileged unit. Returns
    /// false when another install is already running.</summary>
    public bool StartInstall(string id, string target, IReadOnlyList<string> urls, long approxBytes) {
        if (!_gate.Wait(0)) return false;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        State = new JobState(id, target, 0, approxBytes, "downloading", null);
        _ = Task.Run(async () => {
            try {
                await RunInstallAsync(id, target, urls, token);
                State = State with { State = "done" };
            } catch (OperationCanceledException) {
                // The operator pressed cancel: leave nothing half-written for
                // the privileged unit to find on the next run.
                TryClearStage();
                State = State with { State = "cancelled", Error = null };
                _logger.LogInformation("Solver database install cancelled: {Id}", id);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Solver database install failed: {Id}", id);
                State = State with { State = "failed", Error = ex.Message };
            } finally {
                _cts?.Dispose();
                _cts = null;
                _gate.Release();
            }
        });
        return true;
    }

    /// <summary>Stop the download in flight. Only the download is
    /// interruptible: once the privileged unit has started unpacking, stopping
    /// it halfway would leave a partial database that ASTAP would happily load
    /// and then fail to solve with, which is worse than finishing.</summary>
    public bool Cancel() {
        var cts = _cts;
        if (cts == null || State.State != "downloading") return false;
        try { cts.Cancel(); } catch { }
        return true;
    }

    private static void TryClearStage() {
        try { if (Directory.Exists(StageDir)) Directory.Delete(StageDir, true); } catch { }
    }

    private async Task RunInstallAsync(string id, string target, IReadOnlyList<string> urls,
                                       CancellationToken ct) {
        if (urls == null || urls.Count == 0) throw new InvalidOperationException("Nothing to download.");

        // A previous failed attempt may have left a partial payload; the
        // installer refuses to guess which files belong together, so start clean.
        TryClearStage();
        Directory.CreateDirectory(StageDir);

        var client = _http.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;    // large files; the token governs

        EnsureRoomFor(State.TotalBytes);
        await PreflightAsync(client, urls, ct);

        long received = 0;
        foreach (var url in urls) {
            ct.ThrowIfCancellationRequested();
            var name = FileNameFor(url, target);
            var dest = Path.Combine(StageDir, name);
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) {
                throw new InvalidOperationException(
                    $"{name}: the mirror answered {(int)resp.StatusCode} {resp.ReasonPhrase}. ({url})");
            }
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using (var dst = File.Create(dest)) {
                var buf = new byte[1 << 20];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0) {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    received += n;
                    State = State with { ReceivedBytes = received };
                }
            }
            // SourceForge and the astrometry mirrors answer a bad request with a
            // small HTML page and a 200. A star database is never 50 kB, and
            // handing that to unzip would fail deep inside the privileged unit
            // where the operator cannot see why.
            var got = new FileInfo(dest).Length;
            if (got < 50_000) {
                throw new InvalidOperationException(
                    $"{name} came back as {got:N0} bytes, which is a mirror error page rather than a database.");
            }
        }

        await File.WriteAllTextAsync(Path.Combine(StageDir, "target"), target, ct);

        State = State with { State = "installing" };
        var rc = await StartUnitAsync(ct);
        var log = TryReadLog();
        if (rc != 0) {
            throw new InvalidOperationException(
                $"The install unit exited {rc}." + (log is null ? "" : $" Last log line: {log}"));
        }
        _logger.LogInformation("Installed solver database {Id} ({Target}). {Log}", id, target, log);
    }

    /// <summary>Refuse a job that cannot fit rather than filling the disk.
    ///
    /// <para>An index band can be 17 GB, and the staging directory sits on the
    /// system disk on every SBC image, so running it out is not a download
    /// failure but a broken host. A third over the payload covers the copy the
    /// privileged unit makes into place.</para></summary>
    private static void EnsureRoomFor(long bytes) {
        if (bytes <= 0) return;
        try {
            var free = new DriveInfo(Path.GetPathRoot(StageDir) ?? "/").AvailableFreeSpace;
            var needed = bytes + bytes / 3;
            if (free < needed) {
                throw new InvalidOperationException(
                    $"That selection needs about {needed / 1_000_000_000.0:F1} GB free and this host has "
                    + $"{free / 1_000_000_000.0:F1} GB. Pick fewer bands, or free some space first.");
            }
        } catch (Exception ex) when (ex is not InvalidOperationException) {
            // An unreadable drive is not a reason to refuse the download.
        }
    }

    /// <summary>Ask for every file's headers before pulling any of it.
    ///
    /// <para>The download used to die on the first bad URL, which for a band
    /// list meant discovering a wrong filename only after the bands ahead of it
    /// had already come down: a stale mirror path cost a full band of transfer
    /// before it said 404. A few hundred HEADs cost seconds and name the file
    /// that is missing.</para></summary>
    private async Task PreflightAsync(HttpClient client, IReadOnlyList<string> urls,
                                      CancellationToken ct) {
        // Nothing ahead of a single file to waste, and the GET now names it in
        // its own error. Skipping this also keeps SourceForge's redirecting
        // download links, which are the whole ASTAP path, out of a HEAD.
        if (urls.Count < 2) return;

        var missing = new List<string>();
        using var gate = new SemaphoreSlim(8);
        var checks = urls.Select(async url => {
            await gate.WaitAsync(ct);
            try {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                // A mirror that will not answer HEAD at all says nothing about
                // whether the file is there, so it must not fail the job.
                if (resp.StatusCode is System.Net.HttpStatusCode.MethodNotAllowed
                                    or System.Net.HttpStatusCode.NotImplemented) return;
                if (!resp.IsSuccessStatusCode) {
                    lock (missing) missing.Add($"{(int)resp.StatusCode} {url}");
                }
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                lock (missing) missing.Add($"{ex.GetType().Name} {url}");
            } finally {
                gate.Release();
            }
        });
        await Task.WhenAll(checks);
        if (missing.Count == 0) return;

        missing.Sort(StringComparer.Ordinal);
        var head = string.Join("; ", missing.Take(3));
        throw new InvalidOperationException(
            missing.Count == 1
                ? $"The mirror does not have {head}."
                : $"{missing.Count} of {urls.Count} files are not on the mirror, starting with {head}.");
    }

    private static string FileNameFor(string url, string target) {
        // The SourceForge download URL ends in "/download", so the name has to
        // come from the segment before it; the astrometry files are named
        // straight in the path.
        var segments = new Uri(url).Segments
            .Select(s => s.Trim('/'))
            .Where(s => s.Length > 0 && !s.Equals("download", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var last = segments.Count > 0 ? segments[^1] : "payload";
        if (Path.GetExtension(last).Length is 0)
            last += target == "astap" ? ".zip" : ".fits";
        return last;
    }

    private async Task<int> StartUnitAsync(CancellationToken ct) {
        var psi = new ProcessStartInfo {
            FileName = "systemctl",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("start");
        psi.ArgumentList.Add(InstallUnit);
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not run systemctl.");
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0) {
            var err = (await proc.StandardError.ReadToEndAsync(ct)).Trim();
            if (!string.IsNullOrEmpty(err))
                _logger.LogWarning("systemctl start {Unit}: {Err}", InstallUnit, err);
        }
        return proc.ExitCode;
    }

    private static string? TryReadLog() {
        try {
            if (!File.Exists(InstallLog)) return null;
            var lines = File.ReadAllLines(InstallLog);
            return lines.Length > 0 ? lines[^1] : null;
        } catch { return null; }
    }
}
