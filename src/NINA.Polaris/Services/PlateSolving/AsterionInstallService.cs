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
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace NINA.Polaris.Services.PlateSolving;

/// <summary>
/// Downloads and installs the Asterion plate solver
/// (https://github.com/asterion-solver/asterion, Apache 2.0) and its star
/// catalogs, from the Settings card.
///
/// <para>The solver is one native executable per platform, no Java. Polaris
/// pins a release (<see cref="Version"/>) and checks the archive against the
/// SHA-256 the release publishes: Asterion was three weeks old and putting out
/// a release every couple of days when it went in, so an update is a decision
/// taken after testing, not something that happens on its own. Catalogs are
/// fetched by Asterion itself (<c>--download-catalog</c>, which resumes and
/// reports progress as one JSON object per line) into a folder Polaris owns,
/// handed over through <c>ASTERION_CATALOGS</c>.</para>
/// </summary>
public sealed class AsterionInstallService {
    /// <summary>The Asterion release Polaris installs and has been tested with.</summary>
    public const string Version = "0.3.1";

    private const string ReleaseBase = "https://github.com/asterion-solver/asterion/releases/download/v" + Version + "/";

    /// <summary>The published catalogs, from Asterion's README for the pinned
    /// release: name, narrowest field (shorter side, degrees), download and
    /// installed index size.</summary>
    public static readonly IReadOnlyList<AsterionCatalog> Catalogs = [
        new("tycho2", "Tycho-2, 2.5 million stars", 0.7, 295_000_000, 378_000_000),
        new("gaia-500", "Gaia DR3, 500 stars per square degree", 0.15, 1_500_000_000, 1_900_000_000),
        new("gaia-1000", "Gaia DR3, 1000 stars per square degree", 0.15, 3_100_000_000, 3_700_000_000),
        new("gaia-2000", "Gaia DR3, 2000 stars per square degree", 0.1, 6_100_000_000, 7_400_000_000),
        new("gaia-3000", "Gaia DR3, 3000 stars per square degree", 0.08, 10_700_000_000, 12_900_000_000),
    ];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(20) };

    private readonly string _root;
    private readonly ProfileService _profiles;
    private readonly ILogger<AsterionInstallService> _logger;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private volatile AsterionInstallStatus _status = new();

    public AsterionInstallService(ProfileService profiles, ILogger<AsterionInstallService> logger) {
        _profiles = profiles;
        _logger = logger;
        _root = Path.Combine(profiles.DataDir, "solvers", "asterion");
    }

    public string BinDir => Path.Combine(_root, "bin");
    public string CatalogDir => Path.Combine(_root, "catalogs");
    public string ExecutablePath => Path.Combine(BinDir, OperatingSystem.IsWindows() ? "asterion.exe" : "asterion");

    /// <summary>The archive for this host, or null where Asterion publishes none.</summary>
    public static string? AssetFor(OSPlatform os, Architecture arch) {
        string? plat = null;
        if (os == OSPlatform.Linux && arch == Architecture.Arm64) plat = "linux-aarch_64";
        else if (os == OSPlatform.Linux && arch == Architecture.X64) plat = "linux-x86_64";
        else if (os == OSPlatform.Windows && arch == Architecture.X64) plat = "windows-x86_64";
        else if (os == OSPlatform.OSX && arch == Architecture.Arm64) plat = "osx-aarch_64";
        return plat == null ? null : $"asterion-{Version}-{plat}.zip";
    }

    private static string? HostAsset() {
        var os = OperatingSystem.IsLinux() ? OSPlatform.Linux
            : OperatingSystem.IsWindows() ? OSPlatform.Windows
            : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.FreeBSD;
        return AssetFor(os, RuntimeInformation.ProcessArchitecture);
    }

    public bool Supported => HostAsset() != null;
    public bool SolverInstalled => File.Exists(ExecutablePath);

    /// <summary>Catalogs present in the catalog folder: each index is a
    /// <c>&lt;catalog&gt;.astx</c> file (its wide-field companion,
    /// <c>.astx.wide</c>, is not one).</summary>
    public IReadOnlyList<string> InstalledCatalogs() {
        try {
            return Directory.Exists(CatalogDir)
                ? Directory.EnumerateFiles(CatalogDir, "*.astx", SearchOption.TopDirectoryOnly)
                    .Select(f => Path.GetFileNameWithoutExtension(f)).OrderBy(n => n).ToList()
                : [];
        } catch { return []; }
    }

    /// <summary>The smallest catalog that solves the field, from its shorter
    /// side; tycho2 when the field is not known.</summary>
    public static string Recommend(double shortSideDeg) {
        if (!(shortSideDeg > 0)) return "tycho2";
        foreach (var c in Catalogs.Where(c => c.Name != "gaia-1000"))
            if (shortSideDeg >= c.MinFieldDeg) return c.Name;
        return "gaia-3000";
    }

    public AsterionInstallStatus GetStatus() => _status with {
        Supported = Supported,
        SolverInstalled = SolverInstalled,
        InstalledCatalogs = InstalledCatalogs(),
    };

    /// <summary>Install the solver if it is missing, then the catalog.
    /// False when an install is already running.</summary>
    public bool Start(string catalog) {
        if (!Catalogs.Any(c => c.Name == catalog)) throw new ArgumentException($"Unknown catalog '{catalog}'.");
        lock (_lock) {
            if (_status.Running) return false;
            _cts = new CancellationTokenSource();
            _status = new AsterionInstallStatus { Running = true, Phase = "starting", Catalog = catalog, StartedAt = DateTime.UtcNow };
        }
        _ = Task.Run(() => RunAsync(catalog, _cts!.Token));
        return true;
    }

    public void Cancel() { lock (_lock) { _cts?.Cancel(); } }

    private async Task RunAsync(string catalog, CancellationToken ct) {
        try {
            var asset = HostAsset();
            if (asset == null) { Finish("Asterion publishes no build for this platform."); return; }
            Directory.CreateDirectory(BinDir);
            Directory.CreateDirectory(CatalogDir);

            var need = Catalogs.First(c => c.Name == catalog);
            if (!InstalledCatalogs().Contains(catalog)) {
                var free = FreeBytes(CatalogDir);
                // The download, the index and a temporary file, per the README.
                if (free >= 0 && free < need.DownloadBytes + need.IndexBytes) {
                    Finish($"Not enough free space for {catalog}: it needs about {Gb(need.DownloadBytes + need.IndexBytes)} "
                         + $"and {Gb(free)} is free on the disk holding {CatalogDir}.");
                    return;
                }
            }

            if (!SolverInstalled) await InstallSolverAsync(asset, ct);
            await InstallCatalogAsync(catalog, ct);
            var switched = MakePrimaryIfDefault();
            _logger.LogInformation("Asterion {Version} installed with catalog {Catalog}", Version, catalog);
            lock (_lock) _status = _status with { SwitchedPrimary = switched };
            Finish(null);
        } catch (OperationCanceledException) {
            Finish("cancelled");
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Asterion install failed");
            Finish(ex.Message);
        }
    }

    private async Task InstallSolverAsync(string asset, CancellationToken ct) {
        SetPhase("downloading-solver");
        var expected = ParseChecksums(await Http.GetStringAsync(ReleaseBase + "checksums_sha256.txt", ct))
            .GetValueOrDefault(asset)
            ?? throw new InvalidOperationException($"The Asterion {Version} release lists no checksum for {asset}.");

        var tmp = Path.Combine(_root, asset + ".part");
        try {
            using (var resp = await Http.GetAsync(ReleaseBase + asset, HttpCompletionOption.ResponseHeadersRead, ct)) {
                resp.EnsureSuccessStatusCode();
                lock (_lock) _status = _status with { BytesTotal = resp.Content.Headers.ContentLength ?? 0, BytesDownloaded = 0 };
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[1 << 20];
                long got = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0) {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    got += read;
                    lock (_lock) _status = _status with { BytesDownloaded = got };
                }
            }

            SetPhase("verifying");
            string actual;
            await using (var fs = File.OpenRead(tmp)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The Asterion download does not match its published checksum ({asset}).");

            SetPhase("extracting");
            var exeName = Path.GetFileName(ExecutablePath);
            using (var zip = ZipFile.OpenRead(tmp)) {
                // Only the solver itself is needed; the archive also carries the
                // web interface. Where it sits inside the archive is not relied on.
                var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, exeName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"{exeName} is not in {asset}.");
                var staged = ExecutablePath + ".new";
                entry.ExtractToFile(staged, overwrite: true);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                File.Move(staged, ExecutablePath, overwrite: true);
            }
        } finally {
            try { File.Delete(tmp); } catch { }
        }
    }

    private async Task InstallCatalogAsync(string catalog, CancellationToken ct) {
        SetPhase("catalog");
        lock (_lock) _status = _status with { BytesDownloaded = 0, BytesTotal = 0, Fraction = null, Step = null };
        var psi = new ProcessStartInfo {
            FileName = ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "--download-catalog", catalog, "--json" }) psi.ArgumentList.Add(a);
        psi.Environment["ASTERION_CATALOGS"] = CatalogDir;

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Asterion did not start.");
        var stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        string? lastMessage = null;
        using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
        string? line;
        while ((line = await proc.StandardOutput.ReadLineAsync(CancellationToken.None)) != null) {
            var ev = ParseProgress(line);
            if (ev == null) continue;
            if (ev.Message != null) lastMessage = ev.Message;
            lock (_lock) _status = _status with {
                Step = ev.Step ?? _status.Step,
                Fraction = ev.Fraction ?? _status.Fraction,
                BytesDownloaded = ev.Bytes ?? _status.BytesDownloaded,
            };
        }
        await proc.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (proc.ExitCode != 0) {
            var err = (await stderr).Trim();
            throw new InvalidOperationException($"Asterion could not install {catalog} (exit {proc.ExitCode}): "
                + (err.Length > 0 ? Tail(err, 400) : lastMessage ?? "no message"));
        }
    }

    /// <summary>Set Asterion as the primary solver when the profile still has
    /// the stock ASTAP, so installing it is all it takes to use it. A choice of
    /// anything else is left alone. True when the setting changed.</summary>
    private bool MakePrimaryIfDefault() {
        bool switched = false;
        _profiles.UpdateSettings(p => {
            if (string.IsNullOrWhiteSpace(p.PlateSolvePrimary) || p.PlateSolvePrimary == "astap") {
                p.PlateSolvePrimary = "asterion";
                switched = true;
            }
        });
        return switched;
    }

    /// <summary>"&lt;sha256&gt;  &lt;file&gt;" per line, as sha256sum writes it.</summary>
    internal static Dictionary<string, string> ParseChecksums(string text) {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n')) {
            var parts = raw.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0].Length == 64)
                map[parts[1].TrimStart('*').Trim()] = parts[0];
        }
        return map;
    }

    /// <summary>One line of <c>--download-catalog --json</c>: an object with an
    /// <c>event</c> (step, progress, detail, done, info, installed). Null for
    /// anything that is not such an object.</summary>
    internal static AsterionProgress? ParseProgress(string line) {
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart()[0] != '{') return null;
        try {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            string? Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            double? Num(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            var ev = Str("event");
            if (ev == null) return null;
            var bytes = Num("bytes");
            return new AsterionProgress(ev, Str("step"), Num("fraction"), bytes.HasValue ? (long)bytes.Value : null,
                Str("message") ?? (ev == "installed" ? $"installed {Str("catalog")}" : null));
        } catch (JsonException) { return null; }
    }

    private static long FreeBytes(string dir) {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace; }
        catch { return -1; }
    }

    private static string Gb(long bytes) => (bytes / 1e9).ToString("0.0") + " GB";
    private static string Tail(string s, int n) => s.Length <= n ? s : "…" + s[^n..];

    private void SetPhase(string phase) { lock (_lock) _status = _status with { Phase = phase }; }

    private void Finish(string? error) {
        lock (_lock) {
            _status = _status with {
                Running = false,
                Phase = error == null ? "done" : error == "cancelled" ? "cancelled" : "error",
                Error = error == "cancelled" ? null : error,
                FinishedAt = DateTime.UtcNow,
            };
        }
    }
}

public sealed record AsterionCatalog(string Name, string Description, double MinFieldDeg, long DownloadBytes, long IndexBytes);

public sealed record AsterionProgress(string Event, string? Step, double? Fraction, long? Bytes, string? Message);

public sealed record AsterionInstallStatus {
    public bool Running { get; init; }
    /// <summary>idle, starting, downloading-solver, verifying, extracting, catalog, done, error, cancelled.</summary>
    public string Phase { get; init; } = "idle";
    public string? Catalog { get; init; }
    public string? Step { get; init; }
    public double? Fraction { get; init; }
    public long BytesDownloaded { get; init; }
    public long BytesTotal { get; init; }
    public string? Error { get; init; }
    public bool SwitchedPrimary { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public bool Supported { get; init; }
    public bool SolverInstalled { get; init; }
    public IReadOnlyList<string> InstalledCatalogs { get; init; } = [];
}
