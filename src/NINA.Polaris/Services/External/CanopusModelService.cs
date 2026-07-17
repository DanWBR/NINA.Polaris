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

using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace NINA.Polaris.Services.External;

/// <summary>
/// CANOPUS: on-demand downloader for the local-LLM assets the "On this server
/// (SBC)" Canopus backend needs — the Qwen3-4B Q4_0 GGUF (~2.4 GB) and an
/// arch-matched llama.cpp <c>llama-server</c> binary. Both are EXCLUDED from the
/// distribution package (they would balloon the .deb, same reasoning as the DSO
/// thumbnail pack / ncnn models), and pulled here into the writable data dir on
/// request, verified by SHA-256 when the index supplies one.
///
/// Mirrors <see cref="DsoThumbPackService"/> (single status slot the UI polls,
/// download-to-.part, completion marker). It ALSO resolves the on-disk paths that
/// <see cref="CanopusServerService"/> launches: <see cref="ModelPath"/> and
/// <see cref="LlamaServerPath"/>, each overridable via config so an operator who
/// already has the files on the box (e.g. a rig test) can point Polaris at them
/// without any download.
/// </summary>
public sealed class CanopusModelService {
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(60) };

    private readonly IConfiguration _config;
    private readonly ILogger<CanopusModelService> _logger;
    private readonly string _modelsDir;    // DataDir/canopus/models
    private readonly string _runtimeDir;   // DataDir/canopus/runtime/<rid>
    private readonly string _indexPath;    // bundled models-index.json

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private volatile CanopusModelStatus _status = new();

    public CanopusModelService(IWebHostEnvironment env, ProfileService profiles,
                               IConfiguration config, ILogger<CanopusModelService> logger) {
        _config = config;
        _logger = logger;
        var root = Path.Combine(profiles.DataDir, "canopus");
        _modelsDir = Path.Combine(root, "models");
        _runtimeDir = Path.Combine(root, "runtime", Rid);
        var webRoot = env.WebRootPath ?? Directory.GetCurrentDirectory();
        _indexPath = Path.Combine(webRoot, "canopus", "models-index.json");
    }

    /// <summary>Runtime identifier used to pick the llama.cpp binary, e.g.
    /// "linux-arm64", "linux-x64", "win-x64", "osx-arm64".</summary>
    public static string Rid {
        get {
            var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
                   : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "linux";
            var arch = RuntimeInformation.ProcessArchitecture switch {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
            };
            return $"{os}-{arch}";
        }
    }

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private string ExeName => IsWindows ? "llama-server.exe" : "llama-server";

    /// <summary>Path to the GGUF model Polaris should feed llama-server. A
    /// <c>Canopus:ModelPath</c> override wins (files already on the box); else the
    /// downloaded file under the data dir. Empty string when neither exists.</summary>
    public string ModelPath {
        get {
            var overridePath = _config.GetValue<string?>("Canopus:ModelPath", null);
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
                return overridePath;
            var file = LoadIndex()?.Model?.File;
            if (!string.IsNullOrWhiteSpace(file)) {
                var p = Path.Combine(_modelsDir, file);
                if (File.Exists(p)) return p;
            }
            // Any .gguf that made it into the models dir (manual copy) is usable too.
            try {
                var any = Directory.Exists(_modelsDir)
                    ? Directory.EnumerateFiles(_modelsDir, "*.gguf").FirstOrDefault() : null;
                if (any != null) return any;
            } catch { /* ignore */ }
            return "";
        }
    }

    /// <summary>Path to the llama.cpp <c>llama-server</c> executable. A
    /// <c>Canopus:LlamaServerPath</c> override wins; else the downloaded binary;
    /// else the bare name so a PATH lookup can still find a system install.</summary>
    public string LlamaServerPath {
        get {
            var overridePath = _config.GetValue<string?>("Canopus:LlamaServerPath", null);
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
                return overridePath;
            var downloaded = Path.Combine(_runtimeDir, ExeName);
            if (File.Exists(downloaded)) return downloaded;
            return ExeName; // let the OS resolve it on PATH
        }
    }

    public bool ModelPresent => !string.IsNullOrEmpty(ModelPath);
    public bool RuntimePresent {
        get {
            if (!string.IsNullOrWhiteSpace(_config.GetValue<string?>("Canopus:LlamaServerPath", null)))
                return File.Exists(_config.GetValue<string>("Canopus:LlamaServerPath"));
            return File.Exists(Path.Combine(_runtimeDir, ExeName));
        }
    }

    /// <summary>True when the bundled index lists a runtime for this arch (so a
    /// download can succeed) — used to gate the "download" offer in the UI.</summary>
    public bool RuntimeAvailableForArch => LoadIndex()?.Runtimes?.ContainsKey(Rid) == true;

    public CanopusModelStatus GetStatus() {
        var s = _status;
        return s with {
            ModelInstalled = ModelPresent,
            RuntimeInstalled = RuntimePresent,
            Rid = Rid,
            RuntimeAvailableForArch = RuntimeAvailableForArch
        };
    }

    public bool Start() {
        lock (_lock) {
            if (_status.Running) return false;
            _cts = new CancellationTokenSource();
            _status = new CanopusModelStatus { Running = true, Phase = "starting", StartedAt = DateTime.UtcNow };
        }
        _ = Task.Run(() => RunAsync(_cts!.Token));
        return true;
    }

    public void Cancel() { lock (_lock) { _cts?.Cancel(); } }

    private async Task RunAsync(CancellationToken ct) {
        try {
            var index = LoadIndex();
            if (index == null) { Finish("No models-index.json bundled; cannot download."); return; }

            Directory.CreateDirectory(_modelsDir);
            Directory.CreateDirectory(_runtimeDir);

            // 1) Model GGUF (skip if an override or an already-downloaded file exists).
            if (!ModelPresent && index.Model is { } m && !string.IsNullOrWhiteSpace(m.Url)) {
                SetPhase("downloading-model");
                var dest = Path.Combine(_modelsDir, m.File ?? "model.gguf");
                await DownloadAsync(m.Url!, dest, m.Sha256, ct);
            }

            // 2) llama.cpp runtime for this arch (archive → extract).
            if (!RuntimePresent) {
                if (index.Runtimes == null || !index.Runtimes.TryGetValue(Rid, out var rt)
                        || string.IsNullOrWhiteSpace(rt.Url)) {
                    Finish($"No llama.cpp runtime published for '{Rid}'. " +
                           "Set Canopus:LlamaServerPath to a llama-server you built for this board.");
                    return;
                }
                SetPhase("downloading-runtime");
                var archive = Path.Combine(_runtimeDir, ".runtime-download" +
                    (rt.Url!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ".zip" : ".tar.gz"));
                await DownloadAsync(rt.Url!, archive, rt.Sha256, ct);
                SetPhase("extracting-runtime");
                ExtractRuntime(archive, _runtimeDir, ct);
                try { File.Delete(archive); } catch { /* leftover is harmless */ }
                // llama.cpp ships the executable non-executable inside a tar on some
                // repacks; make sure it can run on POSIX.
                MakeExecutable(Path.Combine(_runtimeDir, ExeName));
            }

            Finish(null);
            _logger.LogInformation("Canopus assets ready: model={Model}, runtime={Runtime}",
                ModelPath, LlamaServerPath);
        } catch (OperationCanceledException) {
            Finish("cancelled");
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Canopus model download failed");
            Finish($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task DownloadAsync(string url, string dest, string? sha256, CancellationToken ct) {
        var part = dest + ".part";
        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)) {
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"HTTP {(int)resp.StatusCode} from {url} (the data pack may not be published yet).");
            var total = resp.Content.Headers.ContentLength ?? 0;
            lock (_lock) _status = _status with { BytesTotal = total, BytesDownloaded = 0 };
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[1 << 20];
            long got = 0; int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0) {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                got += read;
                lock (_lock) _status = _status with { BytesDownloaded = got };
            }
        }
        if (!string.IsNullOrWhiteSpace(sha256)) {
            SetPhase("verifying");
            var actual = await Sha256Async(part, ct);
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase)) {
                try { File.Delete(part); } catch { }
                throw new InvalidOperationException($"SHA-256 mismatch for {Path.GetFileName(dest)}.");
            }
        }
        File.Move(part, dest, overwrite: true);
    }

    private static void ExtractRuntime(string archive, string destDir, CancellationToken ct) {
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) {
            ZipFile.ExtractToDirectory(archive, destDir, overwriteFiles: true);
            return;
        }
        // .tar.gz — TarFile preserves the versioned-lib symlinks llama.cpp ships
        // (a plain copy would drop the *.so.0 links the loader resolves).
        using var fs = File.OpenRead(archive);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gz, destDir, overwriteFiles: true);
        ct.ThrowIfCancellationRequested();
    }

    private static void MakeExecutable(string path) {
        if (OperatingSystem.IsWindows() || !File.Exists(path)) return;
        try {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        } catch { /* best effort */ }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct) {
        await using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private CanopusIndex? LoadIndex() {
        try {
            var overrideUrl = _config.GetValue<string?>("Canopus:IndexUrl", null);
            string json;
            if (!string.IsNullOrWhiteSpace(overrideUrl)) {
                json = Http.GetStringAsync(overrideUrl).GetAwaiter().GetResult();
            } else if (File.Exists(_indexPath)) {
                json = File.ReadAllText(_indexPath);
            } else {
                return null;
            }
            return JsonSerializer.Deserialize<CanopusIndex>(json, new JsonSerializerOptions {
                PropertyNameCaseInsensitive = true
            });
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Canopus index load failed");
            return null;
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

    // ---- bundled index shape ----
    private sealed class CanopusIndex {
        public CanopusModelEntry? Model { get; set; }
        public Dictionary<string, CanopusRuntimeEntry>? Runtimes { get; set; }
    }
    private sealed class CanopusModelEntry {
        public string? File { get; set; }
        public string? Url { get; set; }
        public string? Sha256 { get; set; }
        public long Bytes { get; set; }
    }
    private sealed class CanopusRuntimeEntry {
        public string? Url { get; set; }
        public string? Sha256 { get; set; }
    }
}

/// <summary>Observable state for the Canopus asset download.</summary>
public sealed record CanopusModelStatus {
    public bool Running { get; init; }
    public string Phase { get; init; } = "idle";  // idle|starting|downloading-model|downloading-runtime|extracting-runtime|verifying|done|error|cancelled
    public long BytesDownloaded { get; init; }
    public long BytesTotal { get; init; }
    public string? Error { get; init; }
    public bool ModelInstalled { get; init; }
    public bool RuntimeInstalled { get; init; }
    public bool RuntimeAvailableForArch { get; init; }
    public string Rid { get; init; } = "";
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
}
