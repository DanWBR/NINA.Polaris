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
            return Directory.EnumerateFiles(dir)
                .Select(f => Path.GetFileName(f))
                .Where(n => n.Length >= 3 && n.Contains('_'))
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
                    var name = Path.GetFileNameWithoutExtension(f);          // index-4209 / index-5200-03
                    var digits = name.Split('-').Skip(1).FirstOrDefault();
                    if (digits is { Length: 4 } && int.TryParse(digits[^2..], out var s)) scales.Add(s);
                }
            } catch { /* unreadable directory is simply "nothing installed" */ }
        }
        return scales.OrderBy(x => x).ToList();
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

        long received = 0;
        foreach (var url in urls) {
            ct.ThrowIfCancellationRequested();
            var name = FileNameFor(url, target);
            var dest = Path.Combine(StageDir, name);
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
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
