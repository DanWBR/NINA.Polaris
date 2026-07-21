// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. See <https://www.gnu.org/licenses/>.

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace NINA.Polaris.Services;

/// <summary>Provides the Python that runs polarispy scripts, and installs the
/// pixel-processing runtime (numpy + astropy) offline. Because those are C
/// extensions tied to the host's Python version, we cannot bundle a single
/// wheel; instead a per-arch, per-Python-version wheel pack is downloaded once
/// (while online) from the GitHub release and pip-installed with --no-index into
/// a dedicated venv, after which scripts run fully offline.</summary>
public sealed class ScriptRuntimeService {
    public sealed class InstallState {
        public bool Running { get; set; }
        public string Phase { get; set; } = "";
        public double Percent { get; set; }
        public string? Error { get; set; }
        public bool Done { get; set; }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private const string ReleaseBase = "https://github.com/DanWBR/NINA.Polaris/releases/download/script-runtime";

    private readonly ILogger<ScriptRuntimeService> _log;
    private readonly string _venvDir;
    private readonly InstallState _install = new();

    public ScriptRuntimeService(ILogger<ScriptRuntimeService> log) {
        _log = log;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = string.IsNullOrWhiteSpace(home)
            ? Path.Combine(AppContext.BaseDirectory, ".scripts")
            : Path.Combine(home, ".config", "NINA.Polaris");
        _venvDir = Path.Combine(root, "scripts-venv");
    }

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static string SystemPython => IsWindows ? "python" : "python3";

    private string VenvPython =>
        IsWindows ? Path.Combine(_venvDir, "Scripts", "python.exe")
                  : Path.Combine(_venvDir, "bin", "python");

    /// <summary>The interpreter scripts run under: the runtime venv when present,
    /// otherwise the system Python.</summary>
    public string PythonForScripts() => File.Exists(VenvPython) ? VenvPython : SystemPython;

    /// <summary>True when numpy + astropy import under the resolved interpreter.</summary>
    public bool PixelRuntimeReady() => ImportsOk(PythonForScripts());

    public object Status() => new {
        ready = PixelRuntimeReady(),
        viaVenv = File.Exists(VenvPython),
        rid = Rid(),
        install = new {
            running = _install.Running, phase = _install.Phase, percent = _install.Percent,
            error = _install.Error, done = _install.Done
        }
    };

    private bool ImportsOk(string python) {
        try {
            var (code, _) = Run(python, new[] { "-c", "import numpy, astropy" }, 20_000);
            return code == 0;
        } catch { return false; }
    }

    // "linux-arm64" / "linux-x64" / "win-x64".
    private static string Rid() {
        var os = IsWindows ? "win" : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            var a => a.ToString().ToLowerInvariant(),
        };
        return $"{os}-{arch}";
    }

    // "311", "312", ... from the system Python.
    private string PythonMinor() {
        try {
            var (code, outp) = Run(SystemPython,
                new[] { "-c", "import sys;print(f'{sys.version_info.major}{sys.version_info.minor}')" }, 20_000);
            var v = outp.Trim();
            return code == 0 && v.Length is 2 or 3 ? v : "";
        } catch { return ""; }
    }

    /// <summary>Download the wheel pack for this host and pip-install numpy +
    /// astropy offline into the venv. Idempotent; no-op while already running.</summary>
    public void StartInstall() {
        lock (_install) {
            if (_install.Running) return;
            _install.Running = true; _install.Done = false; _install.Error = null;
            _install.Percent = 0; _install.Phase = "starting";
        }
        _ = Task.Run(InstallAsync);
    }

    private async Task InstallAsync() {
        var tmpZip = Path.Combine(Path.GetTempPath(), $"polaris-script-rt-{Guid.NewGuid():n}.zip");
        var wheels = Path.Combine(Path.GetTempPath(), $"polaris-script-wheels-{Guid.NewGuid():n}");
        try {
            var py = PythonMinor();
            if (string.IsNullOrEmpty(py)) throw new InvalidOperationException(
                $"Could not determine the system Python version ({SystemPython} not found?).");
            var url = $"{ReleaseBase}/script-runtime-{Rid()}-py{py}.zip";

            SetPhase("downloading", 5);
            await DownloadAsync(url, tmpZip);

            SetPhase("extracting", 40);
            Directory.CreateDirectory(wheels);
            ZipFile.ExtractToDirectory(tmpZip, wheels, overwriteFiles: true);

            SetPhase("creating venv", 55);
            if (!File.Exists(VenvPython)) {
                Directory.CreateDirectory(Path.GetDirectoryName(_venvDir)!);
                var (vc, vout) = Run(SystemPython, new[] { "-m", "venv", _venvDir }, 120_000);
                if (vc != 0) throw new InvalidOperationException("venv creation failed: " + vout);
            }

            SetPhase("installing (offline)", 70);
            var (ic, iout) = Run(VenvPython, new[] {
                "-m", "pip", "install", "--no-index", "--find-links", wheels, "numpy", "astropy"
            }, 600_000);
            if (ic != 0) throw new InvalidOperationException("pip install failed: " + Tail(iout));

            if (!ImportsOk(VenvPython)) throw new InvalidOperationException("numpy/astropy still not importable after install.");

            lock (_install) { _install.Percent = 100; _install.Phase = "done"; _install.Done = true; }
            _log.LogInformation("Script runtime installed into {Venv}", _venvDir);
        } catch (Exception ex) {
            _log.LogWarning(ex, "Script runtime install failed");
            lock (_install) { _install.Error = ex.Message; _install.Phase = "error"; }
        } finally {
            lock (_install) _install.Running = false;
            try { File.Delete(tmpZip); } catch { }
            try { Directory.Delete(wheels, true); } catch { }
        }
    }

    private void SetPhase(string phase, double pct) {
        lock (_install) { _install.Phase = phase; _install.Percent = pct; }
    }

    private static async Task DownloadAsync(string url, string dest) {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(dest);
        await resp.Content.CopyToAsync(fs);
    }

    // Run a process, capture merged stdout+stderr, return (exitCode, output).
    private static (int, string) Run(string exe, string[] args, int timeoutMs) {
        var psi = new ProcessStartInfo {
            FileName = exe, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, outp); }
        return (p.ExitCode, outp);
    }

    private static string Tail(string s, int n = 600) => s.Length <= n ? s : s[^n..];
}
