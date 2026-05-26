using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using NINA.Polaris.Services;

namespace NINA.Polaris.Services.External;

/// <summary>
/// Driver for the Siril CLI (siril-cli), the user's preferred
/// preprocessing + stacking engine. Polaris invokes Siril by writing
/// the user's frames into a temporary work directory laid out the way
/// Siril expects (lights/, darks/, flats/, biases/), then runs the
/// chosen .ssf script with `-s script -d workdir`.
///
/// Bundled scripts ship in src/NINA.Polaris/Resources/SirilScripts/
/// and are extracted from the assembly on first use. User-installed
/// scripts (in %APPDATA%/siril/scripts or ~/.siril/scripts) are also
/// enumerated so the UI can offer them in the same dropdown.
///
/// Long-running jobs report progress via mutable <see cref="SirilJob"/>
/// records keyed by short jobId, same pattern as BatchStackingService.
/// </summary>
public class SirilService {
    private readonly IConfiguration _config;
    private readonly ProfileService _profile;
    private readonly ILogger<SirilService> _logger;

    // Active + recently-completed jobs, keyed by jobId. We keep the
    // last ~50 completed so the UI can show the final outcome after
    // the user navigates away and back.
    private readonly ConcurrentDictionary<string, SirilJob> _jobs = new();
    private readonly object _versionLock = new();
    private string? _cachedVersion;
    private bool _versionChecked;

    // Lazy-extracted bundled scripts. We unpack them to AppData on
    // first access so they have stable paths that the user can also
    // copy / edit / share.
    private readonly Lazy<string> _bundledScriptsDir;

    public SirilService(IConfiguration config, ProfileService profile,
                         ILogger<SirilService> logger) {
        _config = config;
        _profile = profile;
        _logger = logger;
        _bundledScriptsDir = new Lazy<string>(ExtractBundledScripts);
    }

    public string? BinaryPath => Locate();

    public bool IsAvailable => !string.IsNullOrEmpty(BinaryPath);

    /// <summary>
    /// Cached version string parsed from `siril-cli --version`. Empty
    /// when Siril isn't installed. First call may block ~1s.
    /// </summary>
    public string Version {
        get {
            lock (_versionLock) {
                if (_versionChecked) return _cachedVersion ?? "";
                _versionChecked = true;
                _cachedVersion = ProbeVersion();
                return _cachedVersion ?? "";
            }
        }
    }

    /// <summary>
    /// Force a fresh version probe. Called when the user updates the
    /// configured path or clicks "Re-detect" in Settings.
    /// </summary>
    public void InvalidateVersionCache() {
        lock (_versionLock) {
            _versionChecked = false;
            _cachedVersion = null;
        }
    }

    /// <summary>
    /// Enumerate every .ssf script Polaris can offer the user.
    /// Bundled (shipped with Polaris) + user-installed (Siril's
    /// per-OS scripts dir) + any extra dir the user added in Settings.
    /// Duplicates are de-deduplicated by filename, user scripts win
    /// over bundled when names collide so power users can override.
    /// </summary>
    public IReadOnlyList<SirilScriptInfo> EnumerateScripts() {
        var byName = new Dictionary<string, SirilScriptInfo>(StringComparer.OrdinalIgnoreCase);

        // Bundled first, they're the floor that user scripts can
        // override. ExtractBundledScripts may throw on disk-full / RO
        // filesystem; swallow + log so the rest of enumeration works.
        try {
            foreach (var f in EnumerateDir(_bundledScriptsDir.Value, "bundled")) {
                byName[f.Name] = f;
            }
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Could not extract bundled Siril scripts");
        }

        foreach (var dir in UserScriptDirs()) {
            foreach (var f in EnumerateDir(dir, "user")) {
                byName[f.Name] = f;
            }
        }

        return byName.Values
            .OrderBy(s => s.Source == "bundled" ? 0 : 1)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<SirilScriptInfo> EnumerateDir(string dir, string source) {
        if (!Directory.Exists(dir)) yield break;
        foreach (var path in Directory.EnumerateFiles(dir, "*.ssf", SearchOption.TopDirectoryOnly)) {
            yield return new SirilScriptInfo(Path.GetFileName(path), path, source);
        }
    }

    /// <summary>
    /// Per-OS list of where the user's personal Siril scripts live.
    /// Includes any extra dir the user configured in profile settings.
    /// </summary>
    public IEnumerable<string> UserScriptDirs() {
        var configured = (_profile.Active.SirilScriptsDir ?? "").Trim();
        if (!string.IsNullOrEmpty(configured)) yield return configured;

        if (OperatingSystem.IsWindows()) {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            yield return Path.Combine(appData, "siril", "scripts");
        } else if (OperatingSystem.IsMacOS()) {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, "Library", "Application Support", "siril", "scripts");
            yield return Path.Combine(home, ".siril", "scripts");
        } else {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, ".siril", "scripts");
            yield return Path.Combine(home, ".config", "siril", "scripts");
        }
    }

    /// <summary>
    /// Public for the Settings panel diagnostic, show every place we
    /// looked for Siril and whether it existed there.
    /// </summary>
    public IReadOnlyList<BinaryLocator.Candidate> EnumerateBinaryCandidates() =>
        BinaryLocator.Enumerate(_profile.Active.SirilPath,
            WindowsCandidates(), LinuxCandidates(), MacCandidates(), "siril-cli");

    // --- Job execution ----------------------------------------------

    /// <summary>
    /// Kick off a Siril script. Returns immediately with a SirilJob in
    /// "queued" state; the actual work runs in a background task that
    /// updates job state as it goes. Poll <see cref="GetJob"/> for
    /// progress.
    /// </summary>
    public SirilJob StartJob(SirilJobRequest req, CancellationToken ct = default) {
        if (!IsAvailable)
            throw new InvalidOperationException("Siril is not installed");

        var jobId = Guid.NewGuid().ToString("N")[..8];
        var job = new SirilJob {
            JobId = jobId,
            ScriptName = req.ScriptName,
            TargetName = req.TargetName ?? "Unknown",
            Stage = "queued",
            PercentDone = 0,
            StartedAt = DateTime.UtcNow
        };
        _jobs[jobId] = job;

        _ = Task.Run(() => RunJobAsync(job, req, ct), ct);
        return job;
    }

    public SirilJob? GetJob(string jobId) =>
        _jobs.TryGetValue(jobId, out var j) ? j : null;

    public IReadOnlyList<SirilJob> ActiveJobs =>
        _jobs.Values.Where(j => j.Stage != "done" && j.Stage != "failed" && j.Stage != "cancelled")
                    .ToList();

    /// <summary>Cancel a running job. The next progress-poll picks up the cancellation.</summary>
    public bool CancelJob(string jobId) {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        if (job.Stage is "done" or "failed" or "cancelled") return false;
        job.CancelRequested = true;
        return true;
    }

    private async Task RunJobAsync(SirilJob job, SirilJobRequest req, CancellationToken outerCt) {
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        string? workDir = null;
        try {
            // 1. Resolve script: name → bundled or user scripts dir.
            var scriptPath = ResolveScriptPath(req.ScriptName);
            if (scriptPath == null) {
                Fail(job, $"Script not found: {req.ScriptName}");
                return;
            }
            job.ScriptPath = scriptPath;

            // 2. Build the working directory under the rig's output
            //    root so the user can find intermediate files if
            //    needed and so cleanup is bounded to one location.
            workDir = req.WorkDirOverride ?? BuildWorkDir(job.JobId, req.TargetName);
            job.WorkDir = workDir;
            Directory.CreateDirectory(workDir);
            job.Stage = "staging";
            job.PercentDone = 5;
            await StageInputsAsync(workDir, req, jobCts.Token);

            // 3. Run siril-cli. Stdout drives Stage/PercentDone via
            //    light regex matching on Siril's well-known status
            //    messages, pure best-effort, no schema guarantee.
            job.Stage = "running";
            job.PercentDone = 10;
            var exitCode = await RunCliAsync(scriptPath, workDir, job, jobCts.Token);

            if (job.CancelRequested) {
                job.Stage = "cancelled";
                job.PercentDone = 0;
                return;
            }
            if (exitCode != 0) {
                Fail(job, $"siril-cli exited with code {exitCode}: {Truncate(job.LastError, 500)}");
                return;
            }

            // 4. Find and move result.
            job.Stage = "collecting";
            job.PercentDone = 95;
            var outputPath = CollectResult(workDir, req.TargetName ?? "Unknown", job);
            if (outputPath == null) {
                Fail(job, "Siril finished but no result_*.fit was produced in the work directory");
                return;
            }
            job.ResultPath = outputPath;
            job.Stage = "done";
            job.PercentDone = 100;
            job.CompletedAt = DateTime.UtcNow;

            // Best-effort cleanup of work dir (keep on failure so user can debug).
            try { Directory.Delete(workDir, recursive: true); } catch { /* not fatal */ }
        } catch (OperationCanceledException) {
            job.Stage = "cancelled";
            job.PercentDone = 0;
        } catch (Exception ex) {
            _logger.LogError(ex, "Siril job {Id} threw", job.JobId);
            Fail(job, ex.Message);
        }
    }

    /// <summary>
    /// Resolve a script name (bare filename or absolute path) to an
    /// existing .ssf on disk. Bundled wins over user only when name
    /// matches a bundled exactly AND no user script overrides it.
    /// </summary>
    public string? ResolveScriptPath(string scriptName) {
        if (string.IsNullOrWhiteSpace(scriptName)) return null;
        if (Path.IsPathRooted(scriptName) && File.Exists(scriptName)) return scriptName;
        return EnumerateScripts()
            .FirstOrDefault(s => string.Equals(s.Name, scriptName, StringComparison.OrdinalIgnoreCase))
            ?.Path;
    }

    private string BuildWorkDir(string jobId, string? targetName) {
        var outDir = (_profile.Active.ImageOutputDir ?? "").Trim();
        if (string.IsNullOrEmpty(outDir)) {
            outDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA.Polaris");
        }
        return Path.Combine(outDir, ".polaris-tmp", $"siril-{jobId}");
    }

    /// <summary>
    /// Stage input frames into the lights/darks/flats/biases subdirs
    /// Siril expects. Hardlinks (Windows NTFS) or symlinks (Unix)
    /// avoid copying GB of data; falls back to copy when the target
    /// FS doesn't support links.
    /// </summary>
    private async Task StageInputsAsync(string workDir, SirilJobRequest req, CancellationToken ct) {
        await StageAsync(Path.Combine(workDir, "lights"), req.LightPaths, ct);
        await StageAsync(Path.Combine(workDir, "darks"),  req.DarkPaths,  ct);
        await StageAsync(Path.Combine(workDir, "flats"),  req.FlatPaths,  ct);
        await StageAsync(Path.Combine(workDir, "biases"), req.BiasPaths,  ct);
    }

    private static async Task StageAsync(string subDir, List<string>? sources, CancellationToken ct) {
        if (sources == null || sources.Count == 0) return;
        Directory.CreateDirectory(subDir);
        foreach (var src in sources) {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(src)) continue;
            var dst = Path.Combine(subDir, Path.GetFileName(src));
            if (File.Exists(dst)) continue;
            try {
                File.CreateSymbolicLink(dst, src);
            } catch {
                // Fall back to copy on filesystems that don't allow
                // symlinks (FAT32 thumb drives, restricted Windows
                // without dev mode, etc.).
                await CopyFileAsync(src, dst, ct);
            }
        }
    }

    private static async Task CopyFileAsync(string src, string dst, CancellationToken ct) {
        await using var sIn = File.OpenRead(src);
        await using var sOut = File.Create(dst);
        await sIn.CopyToAsync(sOut, ct);
    }

    private async Task<int> RunCliAsync(string scriptPath, string workDir, SirilJob job, CancellationToken ct) {
        var binPath = BinaryPath ?? throw new InvalidOperationException("Siril not found");
        var psi = new ProcessStartInfo {
            FileName = binPath,
            Arguments = $"-s \"{scriptPath}\" -d \"{workDir}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workDir
        };
        using var proc = new Process { StartInfo = psi };

        var stderrBuf = new System.Text.StringBuilder();
        proc.ErrorDataReceived += (_, e) => {
            if (e.Data != null) {
                stderrBuf.AppendLine(e.Data);
                // Capture last 500 chars for the job's LastError field
                job.LastError = stderrBuf.ToString();
            }
        };

        proc.Start();
        proc.BeginErrorReadLine();

        var stdoutTask = Task.Run(async () => {
            // Line-by-line stdout reader so we can update progress as
            // Siril runs. ReadLineAsync returns null at EOF, checking
            // EndOfStream first would do a synchronous Peek under the
            // hood (CA2024), defeating the async loop.
            while (true) {
                ct.ThrowIfCancellationRequested();
                if (job.CancelRequested) {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    return;
                }
                var line = await proc.StandardOutput.ReadLineAsync(ct);
                if (line == null) break;
                ParseProgress(line, job);
            }
        }, ct);

        await proc.WaitForExitAsync(ct);
        try { await stdoutTask; } catch (OperationCanceledException) { /* expected on cancel */ }
        return proc.ExitCode;
    }

    /// <summary>
    /// Lightweight pattern match on Siril's stdout. The CLI emits
    /// human-readable status, there's no machine-readable channel
    /// short of named pipes, which would add OS-specific complexity
    /// for little benefit at this stage.
    /// </summary>
    private static readonly Regex ProgressRe =
        new(@"progress:?\s+(\d{1,3})%", RegexOptions.IgnoreCase);
    private static readonly Regex StageRe =
        new(@"status:?\s+(\w+)", RegexOptions.IgnoreCase);

    private void ParseProgress(string line, SirilJob job) {
        var m = ProgressRe.Match(line);
        if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer,
                                       CultureInfo.InvariantCulture, out var pct)) {
            // Map the script-internal 0..100 to the slice we reserve
            // for the running phase (10..95) so staging + collecting
            // get their own dedicated ranges.
            job.PercentDone = Math.Clamp(10 + (int)(pct * 0.85), 10, 95);
        }
        var s = StageRe.Match(line);
        if (s.Success) {
            job.Stage = $"running: {s.Groups[1].Value.ToLowerInvariant()}";
        }
        _logger.LogDebug("[siril {Id}] {Line}", job.JobId, line);
    }

    /// <summary>
    /// Find the stacked result in the work dir + move it to the rig's
    /// permanent siril output folder. Siril writes result_{name}.fit
    /// or just result.fit depending on the script.
    /// </summary>
    private string? CollectResult(string workDir, string targetName, SirilJob job) {
        // Search top-level + process_* subdirs (modern Siril stacking
        // dumps results in a nested folder).
        var candidates = Directory.EnumerateFiles(workDir, "result*.fit*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(workDir, "stacked*.fit*", SearchOption.AllDirectories))
            .OrderByDescending(p => new FileInfo(p).LastWriteTimeUtc)
            .ToList();

        if (candidates.Count == 0) return null;
        var src = candidates[0];

        // Destination: {rig}/siril/{target}/result_{timestamp}.fit
        var rigName = SafeFolder(_profile.Active.Name ?? "Default");
        var safeTarget = SafeFolder(targetName);
        var outDir = (_profile.Active.ImageOutputDir ?? "").Trim();
        if (string.IsNullOrEmpty(outDir))
            outDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA.Polaris");
        var destDir = Path.Combine(outDir, rigName, "siril", safeTarget);
        Directory.CreateDirectory(destDir);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var ext = Path.GetExtension(src);
        var destPath = Path.Combine(destDir, $"result_{stamp}{ext}");
        File.Move(src, destPath);
        _logger.LogInformation("FileOp SirilResult {Src} -> {Dst}", src, destPath);
        return destPath;
    }

    private static string SafeFolder(string name) {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
        return string.IsNullOrEmpty(clean) ? "Unknown" : clean;
    }

    private static string Truncate(string? s, int max) {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max];
    }

    private void Fail(SirilJob job, string message) {
        job.Stage = "failed";
        job.LastError = message;
        job.CompletedAt = DateTime.UtcNow;
        _logger.LogWarning("Siril job {Id} failed: {Msg}", job.JobId, message);
    }

    // --- Bundled scripts --------------------------------------------

    /// <summary>
    /// Bundled .ssf files are embedded as resources at compile time
    /// (csproj <c>&lt;EmbeddedResource&gt;</c>). On first access we
    /// unpack them to a stable AppData path so they have real file
    /// paths siril-cli can consume and so the user can also browse /
    /// edit / share them. Idempotent: re-extracts only if missing or
    /// stale (compared to the embedded copy via filename, we don't
    /// version individual scripts).
    /// </summary>
    private string ExtractBundledScripts() {
        var targetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NINA.Polaris", "siril", "scripts-bundled");
        Directory.CreateDirectory(targetDir);

        var asm = Assembly.GetExecutingAssembly();
        // Resources are named NINA.Polaris.Resources.SirilScripts.<filename>.ssf
        const string prefix = "NINA.Polaris.Resources.SirilScripts.";
        foreach (var resName in asm.GetManifestResourceNames()) {
            if (!resName.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (!resName.EndsWith(".ssf", StringComparison.Ordinal)) continue;
            var fileName = resName[prefix.Length..];
            var target = Path.Combine(targetDir, fileName);
            if (File.Exists(target)) continue;
            try {
                using var stream = asm.GetManifestResourceStream(resName);
                if (stream == null) continue;
                using var fs = File.Create(target);
                stream.CopyTo(fs);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Could not extract bundled script {Name}", fileName);
            }
        }
        return targetDir;
    }

    // --- Binary lookup ----------------------------------------------

    private string? Locate() =>
        BinaryLocator.Find(_profile.Active.SirilPath,
            WindowsCandidates(), LinuxCandidates(), MacCandidates(),
            pathLookupName: "siril-cli");

    private static string[] WindowsCandidates() {
        var p64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var p86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return [
            Path.Combine(p64, "Siril", "bin", "siril-cli.exe"),
            Path.Combine(p64, "SiriL", "bin", "siril-cli.exe"),
            Path.Combine(p86, "Siril", "bin", "siril-cli.exe"),
            Path.Combine(p86, "SiriL", "bin", "siril-cli.exe")
        ];
    }

    private static string[] LinuxCandidates() => [
        "/usr/bin/siril-cli",
        "/usr/local/bin/siril-cli",
        "/opt/siril/bin/siril-cli",
        "/snap/bin/siril-cli",
        "/var/lib/flatpak/exports/bin/org.siril.Siril"
    ];

    private static string[] MacCandidates() => [
        "/Applications/Siril.app/Contents/MacOS/siril-cli",
        "/Applications/SiriL.app/Contents/MacOS/siril-cli",
        "/opt/homebrew/bin/siril-cli",
        "/usr/local/bin/siril-cli"
    ];

    /// <summary>Run `siril-cli --version` and parse the first version-looking token.</summary>
    private string? ProbeVersion() {
        var bin = BinaryPath;
        if (string.IsNullOrEmpty(bin)) return null;
        try {
            using var proc = Process.Start(new ProcessStartInfo {
                FileName = bin,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (proc == null) return null;
            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(5000)) {
                try { proc.Kill(true); } catch { }
                return null;
            }
            var m = Regex.Match(stdout, @"(\d+\.\d+(?:\.\d+)?)");
            return m.Success ? m.Groups[1].Value : null;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Siril version probe failed");
            return null;
        }
    }
}

// --- DTOs / records -------------------------------------------------

public sealed record SirilScriptInfo(string Name, string Path, string Source);
                                                              // Source = "bundled" | "user"

public sealed record SirilJobRequest(
    string ScriptName,
    string? TargetName,
    List<string> LightPaths,
    List<string>? DarkPaths = null,
    List<string>? FlatPaths = null,
    List<string>? BiasPaths = null,
    string? WorkDirOverride = null);

/// <summary>
/// Mutable job state, the background task updates fields in place,
/// the HTTP endpoint serialises a snapshot. Not thread-safe to write
/// from outside the owning task; safe to read from anywhere.
/// </summary>
public class SirilJob {
    public string JobId { get; set; } = "";
    public string ScriptName { get; set; } = "";
    public string? ScriptPath { get; set; }
    public string TargetName { get; set; } = "";
    public string Stage { get; set; } = "queued";   // queued | staging | running[: subverb] | collecting | done | failed | cancelled
    public int PercentDone { get; set; }
    public string? WorkDir { get; set; }
    public string? ResultPath { get; set; }
    public string? LastError { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool CancelRequested { get; set; }
}
