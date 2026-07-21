// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. See <https://www.gnu.org/licenses/>.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NINA.Polaris.Services;

/// <summary>Runs Polaris Python scripts (the polarispy scripting surface) as
/// child processes. Phase 1 is headless: a script drives the processing engine
/// over the loopback API and reports log + progress back through
/// <c>/api/script/{jobId}/log|progress</c>. Scripts live in the bundled
/// <c>Scripts/examples</c> folder and a per-user scripts folder; only files
/// under those roots may be run.</summary>
public sealed class ScriptRunnerService {
    public record ScriptInfo(string Name, string Path, string Description, bool BuiltIn);

    public sealed class ScriptJob {
        public string Id { get; init; } = Guid.NewGuid().ToString("n");
        public string Name { get; init; } = "";
        public string State { get; set; } = "running";   // running | succeeded | failed | cancelled
        public double Progress { get; set; }
        public string ProgressMessage { get; set; } = "";
        public int? ExitCode { get; set; }
        public string? Error { get; set; }
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public readonly List<string> Log = new();
        internal Process? Proc;

        // A declarative dialog the script is blocking on (Phase 2 UI). Seq bumps
        // per dialog so the client can tell a new one from the current one.
        public int DialogSeq;
        public string DialogState = "none";   // none | pending | submitted | cancelled
        public JsonElement? DialogSpec;
        public JsonElement? DialogValues;
    }

    private readonly ILogger<ScriptRunnerService> _log;
    private readonly string _loopbackUrl;
    private readonly string _scriptsRoot;      // {BaseDir}/Scripts (holds polarispy + examples)
    private readonly string _examplesDir;
    private readonly string _userDir;
    private readonly ConcurrentDictionary<string, ScriptJob> _jobs = new();

    private const int MaxLogLines = 2000;

    public ScriptRunnerService(IConfiguration cfg, ILogger<ScriptRunnerService> log) {
        _log = log;
        var port = cfg.GetValue("Server:Http:Port", 5080);
        _loopbackUrl = $"http://127.0.0.1:{port}";
        _scriptsRoot = Path.Combine(AppContext.BaseDirectory, "Scripts");
        _examplesDir = Path.Combine(_scriptsRoot, "examples");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _userDir = string.IsNullOrWhiteSpace(home)
            ? Path.Combine(_scriptsRoot, "user")
            : Path.Combine(home, ".config", "NINA.Polaris", "scripts");
        try { Directory.CreateDirectory(_userDir); } catch { /* best effort */ }
    }

    private static string PythonExe => OperatingSystem.IsWindows() ? "python" : "python3";

    /// <summary>Bundled + user scripts (*.py), excluding the polarispy package.</summary>
    public IReadOnlyList<ScriptInfo> ListScripts() {
        var list = new List<ScriptInfo>();
        void Scan(string dir, bool builtIn) {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*.py")) {
                if (Path.GetFileName(f).StartsWith("_")) continue;
                list.Add(new ScriptInfo(Path.GetFileNameWithoutExtension(f), f, FirstDocline(f), builtIn));
            }
        }
        Scan(_examplesDir, builtIn: true);
        Scan(_userDir, builtIn: false);
        return list.OrderBy(s => !s.BuiltIn).ThenBy(s => s.Name).ToList();
    }

    // First non-empty line of the module docstring, for the UI subtitle.
    private static string FirstDocline(string path) {
        try {
            foreach (var raw in File.ReadLines(path).Take(20)) {
                var line = raw.Trim().Trim('"', '\'', '#', ' ');
                if (line.Length > 0) return line.Length > 140 ? line[..140] : line;
            }
        } catch { /* ignore */ }
        return "";
    }

    public ScriptJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    /// <summary>Launch a script by path. The path must resolve under an allowed
    /// scripts root. Returns the created job.</summary>
    public ScriptJob Run(string scriptPath) {
        var full = Path.GetFullPath(scriptPath);
        if (!IsAllowed(full)) throw new UnauthorizedAccessException("Script is outside the allowed folders.");
        if (!File.Exists(full)) throw new FileNotFoundException("Script not found.", full);

        var job = new ScriptJob { Name = Path.GetFileNameWithoutExtension(full) };

        var psi = new ProcessStartInfo {
            FileName = PythonExe,
            WorkingDirectory = Path.GetDirectoryName(full)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(full);
        // polarispy is importable via PYTHONPATH = the Scripts root; the compat
        // subfolder makes `import sirilpy` resolve to the polarispy-backed shim.
        var compatDir = Path.Combine(_scriptsRoot, "compat");
        var existingPyPath = Environment.GetEnvironmentVariable("PYTHONPATH");
        var pyPath = _scriptsRoot + Path.PathSeparator + compatDir;
        psi.EnvironmentVariables["PYTHONPATH"] = string.IsNullOrEmpty(existingPyPath)
            ? pyPath : pyPath + Path.PathSeparator + existingPyPath;
        psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
        psi.EnvironmentVariables["POLARIS_API_URL"] = _loopbackUrl;   // loopback = auth-exempt
        psi.EnvironmentVariables["POLARIS_SCRIPT_JOB"] = job.Id;

        Process proc;
        try {
            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) AppendLog(job, e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) AppendLog(job, e.Data); };
            proc.Exited += (_, _) => {
                job.ExitCode = SafeExitCode(proc);
                if (job.State == "running")
                    job.State = job.ExitCode == 0 ? "succeeded" : "failed";
                if (job.ExitCode is int ec && ec != 0 && job.Error == null)
                    job.Error = $"Script exited with code {ec}.";
            };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        } catch (Exception ex) {
            _log.LogWarning(ex, "Failed to launch script {Path} (is python installed?)", full);
            job.State = "failed";
            job.Error = $"Could not start Python: {ex.Message}. Is python installed on this host?";
            _jobs[job.Id] = job;
            return job;
        }

        job.Proc = proc;
        _jobs[job.Id] = job;
        _log.LogInformation("Script started: {Name} (job {Id})", job.Name, job.Id);
        return job;
    }

    public void Cancel(string id) {
        if (_jobs.TryGetValue(id, out var job) && job.Proc is { HasExited: false } p) {
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            job.State = "cancelled";
            job.Error ??= "Cancelled by the user.";
        }
    }

    public void AddLog(string id, string message) {
        if (_jobs.TryGetValue(id, out var job)) AppendLog(job, message);
    }

    public void SetProgress(string id, string? message, double? fraction) {
        if (!_jobs.TryGetValue(id, out var job)) return;
        if (fraction is double f) job.Progress = Math.Clamp(f, 0, 1);
        if (message != null) job.ProgressMessage = message;
    }

    // ---- Phase 2: declarative dialog bridge --------------------------------
    // The script (polarispy) POSTs a form spec and long-polls the result; the
    // browser reads the pending spec from /status and POSTs the submitted values.
    public void SetDialog(string id, JsonElement spec) {
        if (!_jobs.TryGetValue(id, out var job)) return;
        lock (job) {
            job.DialogSeq++;
            job.DialogSpec = spec.Clone();   // detach from the request's JsonDocument
            job.DialogValues = null;
            job.DialogState = "pending";
        }
    }

    // For the script's poll: pending, or the terminal outcome.
    public object DialogResult(string id) {
        if (!_jobs.TryGetValue(id, out var job)) return new { error = "unknown job" };
        lock (job) {
            return job.DialogState switch {
                "submitted" => new { submitted = true, values = (object?)job.DialogValues },
                "cancelled" => new { cancelled = true },
                _ => (object)new { pending = true },
            };
        }
    }

    public void SubmitDialog(string id, JsonElement values) {
        if (!_jobs.TryGetValue(id, out var job)) return;
        lock (job) { job.DialogValues = values.Clone(); job.DialogState = "submitted"; }
    }

    public void CancelDialog(string id) {
        if (_jobs.TryGetValue(id, out var job)) lock (job) { job.DialogState = "cancelled"; }
    }

    // For /status: the spec the browser should render, or null when none is pending.
    public object? PendingDialog(ScriptJob job) {
        lock (job) {
            return job.DialogState == "pending" && job.DialogSpec is { } spec
                ? new { seq = job.DialogSeq, spec = (object?)spec }
                : null;
        }
    }

    private static void AppendLog(ScriptJob job, string line) {
        lock (job.Log) {
            job.Log.Add(line);
            if (job.Log.Count > MaxLogLines) job.Log.RemoveRange(0, job.Log.Count - MaxLogLines);
        }
    }

    private static int? SafeExitCode(Process p) {
        try { return p.ExitCode; } catch { return null; }
    }

    private bool IsAllowed(string full) {
        bool Under(string root) {
            if (string.IsNullOrEmpty(root)) return false;
            var r = Path.GetFullPath(root);
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return full.StartsWith(r + Path.DirectorySeparatorChar, cmp) || full.Equals(r, cmp);
        }
        return Under(_examplesDir) || Under(_userDir);
    }
}
