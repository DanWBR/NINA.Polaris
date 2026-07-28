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
    public record ScriptInfo(string Name, string Path, string Description, bool BuiltIn,
                             string DisplayName, string Icon, string Scope);

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

        // Output files the script wrote (reported via set_pixeldata / output()),
        // so the browser can open the result after a frame script finishes.
        public readonly List<string> Outputs = new();

        // A declarative dialog the script is blocking on (Phase 2 UI). Seq bumps
        // per dialog so the client can tell a new one from the current one.
        public int DialogSeq;
        public string DialogState = "none";   // none | pending | submitted | cancelled
        public JsonElement? DialogSpec;
        public JsonElement? DialogValues;

        // Live preview: the browser bumps PreviewReqSeq with the current field
        // values; the script renders and posts back a PNG for PreviewResSeq.
        public int PreviewReqSeq;
        public JsonElement? PreviewValues;
        public int PreviewResSeq;
        public string? PreviewPng;
        public string? PreviewError;
    }

    private readonly ILogger<ScriptRunnerService> _log;
    private readonly ScriptRuntimeService _runtime;
    private readonly string _loopbackUrl;
    private readonly string _scriptsRoot;      // {BaseDir}/Scripts (holds polarispy + examples)
    private readonly string _examplesDir;
    private readonly string _userDir;
    private readonly ConcurrentDictionary<string, ScriptJob> _jobs = new();

    private const int MaxLogLines = 2000;

    public ScriptRunnerService(IConfiguration cfg, ILogger<ScriptRunnerService> log, ScriptRuntimeService runtime) {
        _log = log;
        _runtime = runtime;
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


    /// <summary>Bundled + user scripts (*.py), excluding the polarispy package.</summary>
    public IReadOnlyList<ScriptInfo> ListScripts() {
        var list = new List<ScriptInfo>();
        void Scan(string dir, bool builtIn) {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*.py")) {
                if (Path.GetFileName(f).StartsWith("_")) continue;
                var name = Path.GetFileNameWithoutExtension(f);
                var (display, icon, scope) = ParseMeta(f, name);
                list.Add(new ScriptInfo(name, f, FirstDocline(f), builtIn, display, icon, scope));
            }
        }
        Scan(_examplesDir, builtIn: true);
        Scan(_userDir, builtIn: false);
        return list.OrderBy(s => !s.BuiltIn).ThenBy(s => s.Name).ToList();
    }

    // Parse the "# polaris: name=...; icon=...; scope=frame|folder|any" metadata
    // line (scanned in the first lines). Defaults: prettified filename, 🐍, any.
    private static (string display, string icon, string scope) ParseMeta(string path, string fileName) {
        string display = Prettify(fileName), icon = "🐍", scope = "any";
        try {
            foreach (var raw in File.ReadLines(path).Take(30)) {
                var idx = raw.IndexOf("polaris:", StringComparison.OrdinalIgnoreCase);
                if (idx < 0 || !raw.TrimStart().StartsWith("#")) continue;
                foreach (var part in raw[(idx + "polaris:".Length)..].Split(';')) {
                    var eq = part.IndexOf('=');
                    if (eq < 0) continue;
                    var k = part[..eq].Trim().ToLowerInvariant();
                    var v = part[(eq + 1)..].Trim();
                    if (v.Length == 0) continue;
                    if (k == "name") display = v;
                    else if (k == "icon") icon = v;
                    else if (k == "scope") scope = v.ToLowerInvariant();
                }
                break;
            }
        } catch { /* ignore */ }
        return (display, icon, scope);
    }

    private static string Prettify(string fileName) =>
        string.Join(' ', fileName.Split('_', '-')
            .Where(w => w.Length > 0)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    // First non-empty line of the module docstring, for the UI subtitle.
    //
    // Skips the machinery every script carries above its docstring: the
    // shebang, the encoding cookie and the "# polaris:" metadata directive.
    // Those are not descriptions, and the directive in particular used to be
    // handed to the UI verbatim, so a button's tooltip read
    // "polaris: name=Narrowband to RGB; icon=...; scope=any".
    private static string FirstDocline(string path) {
        try {
            foreach (var raw in File.ReadLines(path).Take(20)) {
                var trimmed = raw.Trim();
                if (trimmed.StartsWith("#!")) continue;
                if (trimmed.StartsWith('#')
                    && (trimmed.Contains("polaris:", StringComparison.OrdinalIgnoreCase)
                        || trimmed.Contains("coding:", StringComparison.OrdinalIgnoreCase))) {
                    continue;
                }
                var line = trimmed.Trim('"', '\'', '#', ' ');
                if (line.Length > 0) return line.Length > 140 ? line[..140] : line;
            }
        } catch { /* ignore */ }
        return "";
    }

    public ScriptJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    /// <summary>Launch a script by path. The path must resolve under an allowed
    /// scripts root. Returns the created job.</summary>
    public ScriptJob Run(string scriptPath, string? activeFrame = null, string? cwd = null) {
        var full = Path.GetFullPath(scriptPath);
        if (!IsAllowed(full)) throw new UnauthorizedAccessException("Script is outside the allowed folders.");
        if (!File.Exists(full)) throw new FileNotFoundException("Script not found.", full);

        var job = new ScriptJob { Name = Path.GetFileNameWithoutExtension(full) };

        var psi = new ProcessStartInfo {
            FileName = _runtime.PythonForScripts(),   // runtime venv (numpy/astropy) or system Python
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
        // STUDIO context: the frame the user had open and the folder they were
        // browsing, so a script can act on the open frame or the home folder.
        if (!string.IsNullOrEmpty(activeFrame)) psi.EnvironmentVariables["POLARIS_ACTIVE_FRAME"] = activeFrame;
        if (!string.IsNullOrEmpty(cwd)) psi.EnvironmentVariables["POLARIS_CWD"] = cwd;

        Process proc;
        try {
            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) AppendLog(job, e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) AppendLog(job, e.Data); };
            proc.Exited += (_, _) => {
                job.ExitCode = SafeExitCode(proc);
                if (job.State == "running")
                    job.State = job.ExitCode == 0 ? "succeeded" : "failed";
                if (job.ExitCode is int ec && ec != 0 && job.Error == null) {
                    // Surface the real cause: the last non-empty log line is
                    // usually the Python exception (e.g. ModuleNotFoundError).
                    string last;
                    lock (job.Log) last = job.Log.LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
                    if (last.Length > 300) last = last[^300..];
                    job.Error = last.Length > 0 ? $"exited with code {ec}: {last}" : $"exited with code {ec}.";
                }
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

    // A script reporting an output file it wrote (deduped, keeps order).
    public void AddOutput(string id, string path) {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!_jobs.TryGetValue(id, out var job)) return;
        lock (job.Outputs) {
            if (!job.Outputs.Contains(path)) job.Outputs.Add(path);
        }
    }

    // Live preview bridge. Browser -> SetPreviewRequest (values, bumps seq);
    // script polls PreviewRequest, renders, and posts SetPreviewResult (png).
    public int SetPreviewRequest(string id, JsonElement values) {
        if (!_jobs.TryGetValue(id, out var job)) return 0;
        lock (job) { job.PreviewReqSeq++; job.PreviewValues = values.Clone(); return job.PreviewReqSeq; }
    }

    public object PreviewRequest(string id) {
        if (!_jobs.TryGetValue(id, out var job)) return new { seq = 0 };
        lock (job) return new { seq = job.PreviewReqSeq, values = (object?)job.PreviewValues };
    }

    public void SetPreviewResult(string id, int seq, string? png, string? error) {
        if (!_jobs.TryGetValue(id, out var job)) return;
        lock (job) { job.PreviewResSeq = seq; job.PreviewPng = png; job.PreviewError = error; }
    }

    public object PreviewResult(string id) {
        if (!_jobs.TryGetValue(id, out var job)) return new { seq = 0 };
        lock (job) return new { seq = job.PreviewResSeq, png = job.PreviewPng, error = job.PreviewError };
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
