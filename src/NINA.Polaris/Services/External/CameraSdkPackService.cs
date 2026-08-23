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
using System.Runtime.InteropServices;

namespace NINA.Polaris.Services.External;

/// <summary>
/// On-demand downloader for the native camera-SDK libraries (ZWO/SVBony/Player
/// One/ToupTek/Altair). Windows-x64 bundles the DLLs next to the exe already, so
/// this pack targets Linux, where the .deb only Recommends the indi-3rdparty libs
/// and an SBC image may lack them. The vendor .so files are ~100 MB, so they are
/// shipped as a separate <c>data-pack</c> release asset rather than baked into
/// every package, installed on demand from the Software Updates card.
///
/// The pack extracts the arch-matching .so files (flat) into a writable dir the
/// host exports via <c>POLARIS_NATIVE_SDK_DIR</c> (see Program.cs), because on a
/// .deb install the app base dir (/opt/polaris) is root-owned. The per-vendor
/// resolvers (SvbonyRegistry etc.) probe that dir, so once the pack is present a
/// re-GET of /api/camera/drivers flips the drivers to available with no restart.
/// Mirror of <see cref="NcnnModelPackService"/> otherwise.
/// </summary>
public sealed class CameraSdkPackService {
    private const string UrlTemplate =
        "https://github.com/DanWBR/NINA.Polaris/releases/download/data-pack/polaris-camera-sdk-linux-{0}.zip";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(20) };

    private readonly string _packDir;
    private readonly string _url;
    private readonly ILogger<CameraSdkPackService> _logger;

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private volatile CameraSdkPackStatus _status = new();

    public CameraSdkPackService(ProfileService profiles, IConfiguration config,
                                ILogger<CameraSdkPackService> logger) {
        _logger = logger;
        _packDir = PackDir(profiles.DataDir);
        _url = config.GetValue<string>("CameraSdkPack:Url") ?? string.Format(UrlTemplate, Arch);
    }

    /// <summary>The writable directory the pack extracts into (flat .so files),
    /// exported to the vendor resolvers via <c>POLARIS_NATIVE_SDK_DIR</c>.</summary>
    public static string PackDir(string dataDir) => Path.Combine(dataDir, "native-sdks");

    /// <summary>dpkg/.so-style arch for the pack asset name: "x64" | "arm64".</summary>
    private static string Arch => RuntimeInformation.ProcessArchitecture switch {
        Architecture.Arm64 => "arm64",
        Architecture.X64 => "x64",
        _ => "x64"
    };

    /// <summary>The pack only applies on Linux x64/arm64 — Windows x64 bundles the
    /// DLLs already, and no native libs exist for win-arm64.</summary>
    public bool Supported =>
        OperatingSystem.IsLinux() &&
        RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;

    public bool IsInstalled() => File.Exists(Path.Combine(_packDir, ".pack-complete"));

    public int InstalledLibCount() {
        try {
            return Directory.Exists(_packDir)
                ? Directory.EnumerateFiles(_packDir, "*.so", SearchOption.TopDirectoryOnly).Count()
                : 0;
        } catch { return 0; }
    }

    public CameraSdkPackStatus GetStatus() {
        var s = _status;
        return s with {
            Supported = Supported,
            Installed = IsInstalled(),
            InstalledLibCount = InstalledLibCount()
        };
    }

    public bool Start() {
        lock (_lock) {
            if (_status.Running) return false;
            _cts = new CancellationTokenSource();
            _status = new CameraSdkPackStatus { Running = true, Phase = "starting", StartedAt = DateTime.UtcNow };
        }
        _ = Task.Run(() => RunAsync(_cts!.Token));
        return true;
    }

    public void Cancel() { lock (_lock) { _cts?.Cancel(); } }

    private async Task RunAsync(CancellationToken ct) {
        var root = _packDir;
        var tmpZip = Path.Combine(root, ".camera-sdk-download.zip.part");
        try {
            if (!Supported) { Finish("The native camera SDK pack is only available on Linux (x64 / arm64)."); return; }
            try {
                Directory.CreateDirectory(root);
                var probe = Path.Combine(root, ".write-probe");
                await File.WriteAllTextAsync(probe, "ok", ct);
                File.Delete(probe);
            } catch (Exception ex) {
                Finish($"Target folder is not writable ({ex.GetType().Name}: {ex.Message}). " +
                       $"Polaris could not write to '{root}'.");
                return;
            }

            SetPhase("downloading");
            using (var resp = await Http.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, ct)) {
                if (!resp.IsSuccessStatusCode) {
                    Finish($"Download failed: HTTP {(int)resp.StatusCode} from {_url}. " +
                           "The camera SDK pack may not be published yet for this build.");
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
            _logger.LogInformation("Native camera SDK pack installed: {Count} libs in {Dir}",
                InstalledLibCount(), root);
            Finish(null);
        } catch (OperationCanceledException) {
            try { File.Delete(tmpZip); } catch { }
            Finish("cancelled");
        } catch (Exception ex) {
            try { File.Delete(tmpZip); } catch { }
            _logger.LogWarning(ex, "Native camera SDK pack download failed");
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

/// <summary>Observable state for the native camera SDK pack download.</summary>
public sealed record CameraSdkPackStatus {
    public bool Running { get; init; }
    public string Phase { get; init; } = "idle";
    public long BytesDownloaded { get; init; }
    public long BytesTotal { get; init; }
    public int EntriesExtracted { get; init; }
    public int EntriesTotal { get; init; }
    public string? Error { get; init; }
    public bool Supported { get; init; }
    public bool Installed { get; init; }
    public int InstalledLibCount { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
}
