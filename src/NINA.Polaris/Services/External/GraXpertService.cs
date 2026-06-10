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
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using NINA.Polaris.Services;

namespace NINA.Polaris.Services.External;

/// <summary>
/// Driver for the GraXpert CLI. Three operations are unified under
/// one service: background extraction (all versions), deconvolution
/// (v3.0+), and denoising (v3.0+). Each frame is processed by a
/// single subprocess call, GraXpert has no batch mode of its own,
/// so batches are sequential (or with bounded concurrency on beefy
/// hardware) at this layer.
///
/// Output naming convention so multiple operations on the same file
/// don't collide: input.fits → input_bge.fits / input_decon.fits /
/// input_denoise.fits. Encoded by <see cref="OutputSuffix"/>.
/// </summary>
public class GraXpertService {
    private readonly IConfiguration _config;
    private readonly ProfileService _profile;
    private readonly ILogger<GraXpertService> _logger;
    private readonly Onnx.OnnxModelRegistry? _models;
    private readonly Rknn.RknnInferenceService? _rknn;

    private readonly ConcurrentDictionary<string, GraXpertBatchJob> _jobs = new();
    private readonly object _versionLock = new();
    private string? _cachedVersion;
    private bool _versionChecked;

    public GraXpertService(IConfiguration config, ProfileService profile,
                            ILogger<GraXpertService> logger,
                            Onnx.OnnxModelRegistry? models = null,
                            Rknn.RknnInferenceService? rknn = null) {
        _config = config;
        _profile = profile;
        _logger = logger;
        _models = models;
        _rknn = rknn;
    }

    /// <summary>
    /// True when a Rockchip NPU is available to accelerate BGE/Denoise on the
    /// host (RK3588). Surfaced in the GraXpert status so the UI can show an
    /// "NPU" chip. Note: the NPU path works even when the GraXpert CLI is not
    /// installed.
    /// </summary>
    public bool NpuAvailable => _rknn?.IsAvailable == true;

    /// <summary>One-line NPU probe description (for status/diagnostics).</summary>
    public string NpuDiagnostics => _rknn?.Diagnostics ?? "NPU support not built";

    public string? BinaryPath => Locate();
    public bool IsAvailable => !string.IsNullOrEmpty(BinaryPath);

    /// <summary>
    /// True when the resolved BinaryPath is a Python interpreter
    /// (e.g. a venv's bin/python or bin/python3). In that case
    /// GraXpert lives as the `graxpert` PyPI package inside that venv
    /// and is invoked via `python -m graxpert.main ARGS` instead of
    /// `graxpert ARGS`. Adds `-m graxpert.main` as the args prefix
    /// transparently so callers (BuildArgs, ProbeVersion) don't need
    /// to know which install style the user has.
    /// </summary>
    private bool IsPythonInvocation {
        get {
            var bin = BinaryPath;
            if (string.IsNullOrEmpty(bin)) return false;
            var name = Path.GetFileName(bin);
            return name.StartsWith("python", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Args prefix prepended before the GraXpert CLI args. Empty for
    /// standalone binary installs, "-m graxpert.main " for venv-based
    /// pip installs.
    /// </summary>
    private string ArgsPrefix => IsPythonInvocation ? "-m graxpert.main " : "";

    /// <summary>Cached version probed via `graxpert --version`. Empty when missing.</summary>
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
    /// Decon + Denoise landed in GraXpert 3.0. Older builds only
    /// expose background extraction. The UI uses these flags to grey
    /// out the operations the user can't actually run.
    /// </summary>
    public bool SupportsDeconvolution => IsVersionAtLeast(3, 0);
    public bool SupportsDenoising     => IsVersionAtLeast(3, 0);

    public void InvalidateVersionCache() {
        lock (_versionLock) {
            _versionChecked = false;
            _cachedVersion = null;
        }
    }

    public IReadOnlyList<BinaryLocator.Candidate> EnumerateBinaryCandidates() =>
        BinaryLocator.Enumerate(_profile.Active.GraXpertPath,
            WindowsCandidates(), LinuxCandidates(), MacCandidates(), "graxpert");

    // --- Single-frame processing ------------------------------------

    public async Task<GraXpertResult> ProcessFrameAsync(string inputPath,
                                                         GraXpertOptions opts,
                                                         CancellationToken ct,
                                                         Action<string>? onLog = null) {
        if (!File.Exists(inputPath))
            return new GraXpertResult("", null, opts.Operation, 0,
                $"Input file not found: {inputPath}");

        // NPU fast path (RK3588): BGE/Denoise on the Rockchip NPU, ~5x faster
        // than the CPU and it frees the cores for stacking. Works even when the
        // GraXpert CLI isn't installed. FITS input only; any failure falls
        // through to the GraXpert CLI path below.
        if (_rknn != null && _rknn.IsAvailable && opts.UseNpu && IsFitsPath(inputPath)) {
            var npu = TryRunRknn(inputPath, opts, ct, onLog);
            if (npu != null) return npu;
        }

        if (!IsAvailable)
            return new GraXpertResult("", null, opts.Operation, 0, "GraXpert not installed");

        // Block decon/denoise on old GraXpert installs, friendlier
        // than letting the subprocess fail with an obscure error.
        if (opts.Operation == GraXpertOperation.Deconvolution && !SupportsDeconvolution)
            return new GraXpertResult("", null, opts.Operation, 0,
                "Deconvolution requires GraXpert v3.0+");
        if (opts.Operation == GraXpertOperation.Denoising && !SupportsDenoising)
            return new GraXpertResult("", null, opts.Operation, 0,
                "Denoising requires GraXpert v3.0+");

        // Make the host CLI use the SAME model Polaris has for the
        // browser/native path instead of letting GraXpert pick the latest
        // and download it. When the caller didn't pin a version, fall back
        // to the newest model Polaris already has locally for this
        // operation, so the host run stays offline and consistent with the
        // browser. Then normalise the version (GraXpert doesn't know
        // Polaris's -fp16/-int8 variants) and stage the model.onnx into
        // GraXpert's own store + pass -ai_version so it doesn't re-resolve.
        if (string.IsNullOrEmpty(opts.AiVersion)) {
            var local = ResolveLocalAiVersion(opts);
            if (!string.IsNullOrEmpty(local)) opts = opts with { AiVersion = local };
        }
        if (!string.IsNullOrEmpty(opts.AiVersion)) {
            var clean = opts.AiVersion;
            var dash = clean.IndexOf('-');
            if (dash > 0) clean = clean[..dash];
            if (clean != opts.AiVersion) opts = opts with { AiVersion = clean };
            StageVendoredModel(opts, onLog);
        }

        // GX-12i: use the variant-aware overload so decon stars/objects
        // land in separate sibling files instead of clobbering each other.
        var outputPath = DefaultOutputPath(inputPath, opts);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var args = ArgsPrefix + BuildArgs(inputPath, outputPath, opts);
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("FileOp GraXpert {Op} {In} -> {Out}",
            opts.Operation, inputPath, outputPath);

        try {
            var psi = new ProcessStartInfo {
                FileName = BinaryPath!,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(inputPath) ?? Path.GetTempPath()
            };
            // Force unbuffered output so GraXpert's progress (Python /
            // tqdm) reaches the UI live instead of arriving in one lump
            // when the process exits. Harmless for the standalone binary.
            psi.Environment["PYTHONUNBUFFERED"] = "1";
            using var proc = new Process { StartInfo = psi };

            // Stream stdout + stderr line-by-line so the UI can show the
            // GraXpert console live (model load, progress, errors) instead
            // of a silent spinner. We still accumulate the full text for
            // the error message on a non-zero exit. Event-based reading
            // also avoids pipe-buffer deadlocks on long runs.
            var stdoutSb = new System.Text.StringBuilder();
            var stderrSb = new System.Text.StringBuilder();
            proc.OutputDataReceived += (_, e) => {
                if (e.Data == null) return;
                lock (stdoutSb) stdoutSb.AppendLine(e.Data);
                try { onLog?.Invoke(e.Data); } catch { /* logging is best-effort */ }
            };
            proc.ErrorDataReceived += (_, e) => {
                if (e.Data == null) return;
                lock (stderrSb) stderrSb.AppendLine(e.Data);
                try { onLog?.Invoke(e.Data); } catch { /* logging is best-effort */ }
            };

            // Surface the exact command so the user (or a bug report) can
            // see precisely what ran on the host.
            try { onLog?.Invoke($"$ {Path.GetFileName(BinaryPath)} {args}"); } catch { }

            if (!proc.Start())
                return new GraXpertResult("", null, opts.Operation, 0, "Failed to start GraXpert");
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // Kill the subprocess when the job is cancelled (Abort button)
            // so a long denoise on the host actually stops instead of
            // running to completion after the user gave up.
            using var killReg = ct.Register(() => {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                catch { /* race: already exited */ }
            });

            await proc.WaitForExitAsync(ct);
            // Final synchronous wait flushes the async output handlers so
            // stdout/stderr are complete before we read them.
            proc.WaitForExit();
            string stdout, stderr;
            lock (stdoutSb) stdout = stdoutSb.ToString();
            lock (stderrSb) stderr = stderrSb.ToString();
            sw.Stop();

            if (proc.ExitCode != 0) {
                var err = Truncate(stderr.Trim().Length > 0 ? stderr : stdout, 500);
                return new GraXpertResult("", null, opts.Operation,
                    sw.Elapsed.TotalSeconds, $"exit {proc.ExitCode}: {err}");
            }
            // GraXpert decides the output container itself: a ".fit" input
            // commonly comes back as ".fits" (astropy's canonical
            // extension), and on some hosts the file lands a fraction of a
            // second after the process exits. Both made the strict
            // File.Exists(outputPath) check fail even though GraXpert
            // succeeded (a STUDIO refresh then showed the file). Resolve the
            // real written file by polling briefly and accepting a
            // sibling-extension match.
            var written = await ResolveWrittenOutputAsync(outputPath, ct);
            if (written == null) {
                return new GraXpertResult("", null, opts.Operation,
                    sw.Elapsed.TotalSeconds, "GraXpert reported success but no output file appeared");
            }

            string? bgPath = null;
            if (opts.Operation == GraXpertOperation.BackgroundExtraction && opts.SaveBackground) {
                var stem = Path.ChangeExtension(written, null) + "_bg";
                bgPath = FindSiblingExtension(stem);
            }

            return new GraXpertResult(written, bgPath, opts.Operation,
                sw.Elapsed.TotalSeconds, null);
        } catch (OperationCanceledException) {
            return new GraXpertResult("", null, opts.Operation,
                sw.Elapsed.TotalSeconds, "Cancelled");
        } catch (Exception ex) {
            _logger.LogError(ex, "GraXpert {Op} threw on {Path}", opts.Operation, inputPath);
            return new GraXpertResult("", null, opts.Operation,
                sw.Elapsed.TotalSeconds, ex.Message);
        }
    }

    private static bool IsFitsPath(string path) {
        var ext = Path.GetExtension(path);
        return ext.Equals(".fits", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".fit", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".fts", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Run BGE/Denoise on the Rockchip NPU instead of the GraXpert CLI. Reads
    /// the FITS input, runs the converted .rknn model on the NPU, and writes a
    /// FITS output with the same naming convention the CLI path uses. Returns
    /// the result on success, or null (with a logged warning) so the caller
    /// transparently falls back to the CLI. The NPU only serves operations and
    /// model versions for which a model.rknn exists (see RknnInferenceService);
    /// anything else returns null here.
    /// </summary>
    private GraXpertResult? TryRunRknn(string inputPath, GraXpertOptions opts,
                                       CancellationToken ct, Action<string>? onLog) {
        try {
            if (!_rknn!.CanHandle(opts.Operation, opts.AiVersion, out _, out var ver))
                return null;

            onLog?.Invoke($"[NPU] running {opts.Operation} on the Rockchip NPU (rknn {ver})");
            var sw = Stopwatch.StartNew();

            BaseImageData img;
            using (var fs = File.OpenRead(inputPath)) img = FITSReader.Read(fs);
            ct.ThrowIfCancellationRequested();

            var res = _rknn.Run(img, opts);

            var outputPath = DefaultOutputPath(inputPath, opts);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            FITSWriter.Write(res.Image, outputPath);

            string? bgPath = null;
            if (opts.Operation == GraXpertOperation.BackgroundExtraction
                && opts.SaveBackground && res.Background != null) {
                bgPath = Path.ChangeExtension(outputPath, null) + "_bg" + Path.GetExtension(outputPath);
                FITSWriter.Write(res.Background, bgPath);
            }

            sw.Stop();
            onLog?.Invoke(FormattableString.Invariant(
                $"[NPU] done in {sw.Elapsed.TotalSeconds:0.0}s ({res.Tiles} tiles)"));
            _logger.LogInformation("FileOp RKNN {Op} {In} -> {Out} ({Ms} ms)",
                opts.Operation, inputPath, outputPath, (long)res.ElapsedMs);
            return new GraXpertResult(outputPath, bgPath, opts.Operation,
                sw.Elapsed.TotalSeconds, null);
        } catch (OperationCanceledException) {
            // Honour cancellation rather than masking it as a fallback.
            return new GraXpertResult("", null, opts.Operation, 0, "Cancelled");
        } catch (Exception ex) {
            _logger.LogWarning(ex, "RKNN NPU path failed for {Op}; falling back to GraXpert CLI",
                opts.Operation);
            onLog?.Invoke($"[NPU] failed ({ex.Message}); falling back to GraXpert CLI");
            return null;
        }
    }

    /// <summary>
    /// Stage Polaris's GraXpert model into GraXpert's own model store so a
    /// host (CLI) run uses the exact file the browser/native path uses,
    /// instead of GraXpert downloading (or defaulting to) its own.
    ///
    /// The source is resolved through <see cref="Onnx.OnnxModelRegistry"/>,
    /// which already knows the real on-disk location of every model.onnx
    /// (the profile's <c>OnnxModelsPath</c>, <c>/home/polaris/models</c>,
    /// and the bundled <c>wwwroot/graxpert/models</c>, in priority order).
    /// The previous implementation only ever looked under
    /// <c>wwwroot/graxpert/models</c>, which is gitignored / empty on a
    /// normal install, so staging was silently skipped and GraXpert
    /// re-downloaded the model every run. Using the registry means a model
    /// the user actually has (anywhere the registry scans) is honoured.
    ///
    /// GraXpert's store layout is
    ///   ~/.local/share/GraXpert/{family-ai-models}/{version}/model.onnx
    /// (LocalApplicationData/GraXpert/... on Windows), which is the SAME
    /// family/version layout the registry parses, so we just symlink (or
    /// copy, if symlinks are unavailable) the resolved file into place when
    /// it isn't there already. Best-effort: any failure just falls back to
    /// GraXpert's normal model resolution.
    /// </summary>
    /// <summary>
    /// Known image extensions GraXpert may emit, in the order we prefer to
    /// accept them when the exact requested extension isn't what landed.
    /// </summary>
    private static readonly string[] OutputExtCandidates =
        { ".fits", ".fit", ".fts", ".tiff", ".tif", ".xisf", ".png" };

    /// <summary>
    /// Resolve the file GraXpert actually wrote. GraXpert chooses the
    /// container itself (a ".fit" input often comes back ".fits"), and on
    /// some hosts the file is still being flushed when the process exits.
    /// So we poll briefly and accept either the exact path or a
    /// same-stem sibling with a different known image extension. Returns
    /// the real path, or null if nothing non-empty appeared in time.
    /// </summary>
    private static async Task<string?> ResolveWrittenOutputAsync(string outputPath,
                                                                 CancellationToken ct) {
        // ~3s budget: most hosts write instantly; slow disks / network
        // shares occasionally lag a beat behind process exit.
        for (int attempt = 0; attempt < 12; attempt++) {
            if (NonEmpty(outputPath)) return outputPath;
            var sibling = FindSiblingExtension(Path.ChangeExtension(outputPath, null));
            if (sibling != null) return sibling;
            if (attempt < 11) {
                try { await Task.Delay(250, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        return null;
    }

    /// <summary>
    /// Given a path stem (no extension), return the first existing,
    /// non-empty file with one of the known image extensions, or null.
    /// </summary>
    private static string? FindSiblingExtension(string stem) {
        foreach (var ext in OutputExtCandidates) {
            var cand = stem + ext;
            if (NonEmpty(cand)) return cand;
        }
        return null;
    }

    private static bool NonEmpty(string path) {
        try { return File.Exists(path) && new FileInfo(path).Length > 0; }
        catch { return false; }
    }

    /// <summary>
    /// GraXpert's on-disk model dir name for an operation, plus the
    /// canonical family id the <see cref="Onnx.OnnxModelRegistry"/> indexes
    /// it under. Returns (null, null) for operations with no model family.
    /// </summary>
    private static (string? Dir, string? Id) FamilyFor(GraXpertOptions opts) =>
        opts.Operation switch {
            GraXpertOperation.BackgroundExtraction => ("bge-ai-models", "bge"),
            GraXpertOperation.Denoising            => ("denoise-ai-models", "denoise"),
            GraXpertOperation.Deconvolution =>
                string.Equals(opts.DeconTarget, "objects", StringComparison.OrdinalIgnoreCase)
                    ? ("deconvolution-object-ai-models", "decon-objects")
                    : ("deconvolution-stars-ai-models", "decon-stars"),
            _ => (null, null)
        };

    /// <summary>
    /// Newest model version Polaris already has locally for this operation,
    /// per the OnnxModelRegistry. Quantized variants (-fp16 / -int8) are
    /// ignored because the host GraXpert CLI wants its own (non-quantized)
    /// model format; the browser/native path uses the quantized ones.
    /// Returns null when the registry isn't wired or has nothing for the
    /// family, in which case GraXpert keeps its normal model resolution.
    /// </summary>
    private string? ResolveLocalAiVersion(GraXpertOptions opts) {
        var (_, familyId) = FamilyFor(opts);
        if (_models == null || familyId == null) return null;
        try {
            return _models.All()
                .Where(m => string.Equals(m.Family, familyId, StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Version)
                .Where(v => v.IndexOf('-') < 0) // skip -fp16 / -int8 quantized
                .OrderByDescending(v => v, new VersionishComparer())
                .FirstOrDefault();
        } catch (Exception ex) {
            _logger.LogDebug(ex, "GraXpert local AI-version resolution skipped");
            return null;
        }
    }

    /// <summary>
    /// Compares "major.minor.patch"-ish version strings numerically so
    /// "2.0.0" sorts above "10" only when it should; non-numeric parts fall
    /// back to ordinal compare. Good enough for the handful of GraXpert
    /// model versions, without pulling in a SemVer dependency.
    /// </summary>
    private sealed class VersionishComparer : IComparer<string> {
        public int Compare(string? a, string? b) {
            var pa = (a ?? "").Split('.');
            var pb = (b ?? "").Split('.');
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++) {
                var sa = i < pa.Length ? pa[i] : "0";
                var sb = i < pb.Length ? pb[i] : "0";
                if (int.TryParse(sa, out var na) && int.TryParse(sb, out var nb)) {
                    if (na != nb) return na.CompareTo(nb);
                } else {
                    var c = string.CompareOrdinal(sa, sb);
                    if (c != 0) return c;
                }
            }
            return 0;
        }
    }

    private void StageVendoredModel(GraXpertOptions opts, Action<string>? onLog) {
        try {
            var (familyDir, familyId) = FamilyFor(opts);
            if (familyDir == null || string.IsNullOrEmpty(opts.AiVersion)) return;

            // Resolve the real model.onnx Polaris has for this family/version.
            // Prefer the registry (covers the profile models dir,
            // /home/polaris/models AND the bundled wwwroot copy); fall back
            // to the bundled path directly if the registry isn't wired.
            var src = _models?.Find(familyId!, opts.AiVersion)?.Path;
            if (string.IsNullOrEmpty(src) || !File.Exists(src)) {
                var bundled = Path.Combine(AppContext.BaseDirectory, "wwwroot", "graxpert",
                    "models", familyDir, opts.AiVersion, "model.onnx");
                src = File.Exists(bundled) ? bundled : null;
            }
            if (string.IsNullOrEmpty(src)) return; // nothing available for this version

            var destDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GraXpert", familyDir, opts.AiVersion);
            var dest = Path.Combine(destDir, "model.onnx");
            if (File.Exists(dest)) return; // already downloaded or staged

            Directory.CreateDirectory(destDir);
            try {
                File.CreateSymbolicLink(dest, src);
                onLog?.Invoke($"using Polaris model: {familyDir}/{opts.AiVersion} (linked)");
            } catch {
                File.Copy(src, dest, overwrite: false);
                onLog?.Invoke($"using Polaris model: {familyDir}/{opts.AiVersion} (copied)");
            }
        } catch (Exception ex) {
            _logger.LogDebug(ex, "GraXpert vendored-model staging skipped");
        }
    }

    // --- Batch processing (sequential or bounded concurrency) -------

    public GraXpertBatchJob StartBatch(GraXpertBatchRequest req, CancellationToken outerCt = default) {
        var jobId = Guid.NewGuid().ToString("N")[..8];
        var job = new GraXpertBatchJob {
            JobId = jobId,
            Operation = req.Options.Operation,
            Total = req.InputPaths.Count,
            Done = 0,
            Failed = 0,
            CurrentlyProcessing = new List<string>(),
            Results = new List<GraXpertResult>(),
            StartedAt = DateTime.UtcNow
        };
        _jobs[jobId] = job;

        // Per-job cancellation, linked to any outer token. CancelJob
        // cancels this so the in-flight subprocess is actually killed
        // (CancelRequested alone only stops launching *new* files).
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        job.Cts = cts;
        _ = Task.Run(() => RunBatchAsync(job, req, cts.Token), cts.Token);
        return job;
    }

    public GraXpertBatchJob? GetJob(string jobId) =>
        _jobs.TryGetValue(jobId, out var j) ? j : null;

    public IReadOnlyList<GraXpertBatchJob> ActiveJobs =>
        _jobs.Values.Where(j => j.CompletedAt == null && !j.CancelRequested).ToList();

    public bool CancelJob(string jobId) {
        if (!_jobs.TryGetValue(jobId, out var j)) return false;
        if (j.CompletedAt != null) return false;
        j.CancelRequested = true;
        // Actually stop the running subprocess, not just the queue.
        try { j.Cts?.Cancel(); } catch { /* already disposed/cancelled */ }
        return true;
    }

    private async Task RunBatchAsync(GraXpertBatchJob job, GraXpertBatchRequest req,
                                      CancellationToken outerCt) {
        // GraXpert models are RAM-heavy (3-8 GB depending on op);
        // default Concurrency=1 keeps the RPi alive. Power users on
        // Windows mini PCs can crank it up.
        var concurrency = Math.Max(1, req.Concurrency);
        using var sem = new SemaphoreSlim(concurrency, concurrency);

        // Append a console line to the job, capped so a chatty model can't
        // grow the log (and the polled JSON payload) without bound.
        void AppendLog(string line) {
            lock (job) {
                job.Log.Add(line);
                const int cap = 1000;
                if (job.Log.Count > cap)
                    job.Log.RemoveRange(0, job.Log.Count - cap);
            }
        }

        AppendLog($"GraXpert {Version} -- {req.Options.Operation}, "
            + $"{req.InputPaths.Count} file(s), concurrency {concurrency}");

        var tasks = new List<Task>();
        foreach (var input in req.InputPaths) {
            if (job.CancelRequested) break;
            await sem.WaitAsync(outerCt);
            tasks.Add(Task.Run(async () => {
                var baseName = Path.GetFileName(input);
                try {
                    if (job.CancelRequested) return;
                    lock (job) job.CurrentlyProcessing.Add(input);
                    AppendLog($"▶ {baseName}");
                    // When several files run concurrently, prefix each
                    // console line with the file so interleaved output is
                    // still readable; single-file runs stay clean.
                    var res = await ProcessFrameAsync(input, req.Options, outerCt,
                        req.InputPaths.Count > 1
                            ? line => AppendLog($"[{baseName}] {line}")
                            : AppendLog);
                    AppendLog(string.IsNullOrEmpty(res.Error)
                        ? $"✓ {baseName} done in {res.ElapsedSeconds:0.0}s "
                            + $"→ {Path.GetFileName(res.OutputPath)}"
                        : $"✗ {baseName} FAILED: {res.Error}");
                    lock (job) {
                        job.CurrentlyProcessing.Remove(input);
                        job.Results.Add(res);
                        if (string.IsNullOrEmpty(res.Error)) job.Done++;
                        else                                  job.Failed++;
                    }
                } finally {
                    sem.Release();
                }
            }, outerCt));
        }
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { /* batch cancel, partial Results survive */ }
        job.CompletedAt = DateTime.UtcNow;
    }

    // --- Arg building -----------------------------------------------

    /// <summary>Public so tests can pin the CLI string per operation.</summary>
    public string BuildArgs(string inputPath, string outputPath, GraXpertOptions opts) {
        // The -cli flag MUST come before subcommand flags; without
        // it GraXpert launches the GUI.
        var sb = new System.Text.StringBuilder();
        sb.Append($"\"{inputPath}\" -cli -cmd ");
        switch (opts.Operation) {
            case GraXpertOperation.BackgroundExtraction:
                sb.Append("background-extraction");
                sb.Append($" -output \"{StripExt(outputPath)}\"");
                sb.Append($" -correction {opts.Correction}");
                sb.Append(FormattableString.Invariant($" -smoothing {opts.Smoothing:0.##}"));
                if (opts.SaveBackground) sb.Append(" -bg");
                break;
            case GraXpertOperation.Deconvolution:
                // GX-12i: GraXpert CLI splits decon into deconv-stellar /
                // deconv-obj. The previous "-cmd deconvolution" was an
                // invalid choice (only background-extraction / denoising
                // / deconv-obj / deconv-stellar are accepted) and would
                // be rejected by GraXpert before any work happened.
                sb.Append(string.Equals(opts.DeconTarget, "objects",
                    StringComparison.OrdinalIgnoreCase)
                    ? "deconv-obj" : "deconv-stellar");
                sb.Append($" -output \"{StripExt(outputPath)}\"");
                sb.Append(FormattableString.Invariant($" -strength {opts.DeconStrength:0.##}"));
                sb.Append(FormattableString.Invariant($" -psfsize {opts.DeconPsfSize:0.##}"));
                break;
            case GraXpertOperation.Denoising:
                sb.Append("denoising");
                sb.Append($" -output \"{StripExt(outputPath)}\"");
                sb.Append(FormattableString.Invariant($" -strength {opts.DenoiseStrength:0.##}"));
                break;
        }
        if (!string.IsNullOrEmpty(opts.AiVersion))
            sb.Append($" -ai_version {opts.AiVersion}");
        return sb.ToString();
    }

    /// <summary>GraXpert appends its own extension; we strip ours so the resulting filename is what we want.</summary>
    private static string StripExt(string p) {
        var ext = Path.GetExtension(p);
        return string.IsNullOrEmpty(ext) ? p : p[..^ext.Length];
    }

    public static string OutputSuffix(GraXpertOperation op) => op switch {
        GraXpertOperation.BackgroundExtraction => "_bge",
        GraXpertOperation.Deconvolution        => "_decon",
        GraXpertOperation.Denoising            => "_denoise",
        _                                      => "_gx"
    };

    /// <summary>
    /// GX-12i: variant-aware suffix. For decon, the target picks
    /// "_decon_stars" or "_decon_objects" so the two model outputs
    /// don't collide on disk. Other ops are unchanged.
    /// </summary>
    public static string OutputSuffix(GraXpertOptions opts) {
        if (opts.Operation == GraXpertOperation.Deconvolution) {
            return string.Equals(opts.DeconTarget, "objects",
                StringComparison.OrdinalIgnoreCase)
                ? "_decon_objects" : "_decon_stars";
        }
        return OutputSuffix(opts.Operation);
    }

    /// <summary>
    /// Default output path: same dir as input + suffix. The endpoints
    /// override this when the batch wants a dedicated dir (e.g.
    /// {rig}/bge/{target}/).
    /// </summary>
    public static string DefaultOutputPath(string inputPath, GraXpertOperation op) {
        var dir = Path.GetDirectoryName(inputPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var ext = Path.GetExtension(inputPath);
        // FITS is GraXpert's canonical output even when input was XISF/TIFF.
        if (string.IsNullOrEmpty(ext) || !IsImageExt(ext)) ext = ".fits";
        return Path.Combine(dir, stem + OutputSuffix(op) + ext);
    }

    /// <summary>GX-12i: variant-aware overload, picks the suffix from full opts.</summary>
    public static string DefaultOutputPath(string inputPath, GraXpertOptions opts) {
        var dir = Path.GetDirectoryName(inputPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var ext = Path.GetExtension(inputPath);
        if (string.IsNullOrEmpty(ext) || !IsImageExt(ext)) ext = ".fits";
        return Path.Combine(dir, stem + OutputSuffix(opts) + ext);
    }

    private static bool IsImageExt(string ext) =>
        ext.Equals(".fits", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".fit",  StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".fts",  StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".xisf", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".tif",  StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".png",  StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);

    // --- Binary lookup ----------------------------------------------

    private string? Locate() =>
        BinaryLocator.Find(_profile.Active.GraXpertPath,
            WindowsCandidates(), LinuxCandidates(), MacCandidates(), "graxpert");

    private static string[] WindowsCandidates() {
        var p64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var p86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return [
            // Standard installer location
            Path.Combine(p64, "GraXpert", "GraXpert.exe"),
            Path.Combine(p64, "GraXpert", "GraXpert-win64.exe"),
            Path.Combine(p86, "GraXpert", "GraXpert.exe"),
            // Some users portable-extract under LocalAppData
            Path.Combine(localApp, "Programs", "GraXpert", "GraXpert.exe"),
            Path.Combine(localApp, "GraXpert", "GraXpert.exe")
        ];
    }

    private static string[] LinuxCandidates() {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return [
            // Standalone binary layouts (.deb / tarball / portable)
            "/usr/bin/graxpert",
            "/usr/local/bin/graxpert",
            "/opt/graxpert/graxpert",
            "/opt/GraXpert/GraXpert",
            Path.Combine(home, "graxpert", "graxpert"),
            Path.Combine(home, "graxpert", "GraXpert"),
            Path.Combine(home, "GraXpert", "GraXpert"),
            Path.Combine(home, ".local", "bin", "graxpert"),

            // Pip / pipenv install: GraXpert is a Python package
            // (`pip install graxpert`), invoked via `python -m
            // graxpert.main`. The "binary" we resolve is the venv's
            // python; ProcessFrameAsync / ProbeVersion detect that
            // and prepend `-m graxpert.main` to all subprocess args.
            //
            // Covers the common pipenv / venv layouts:
            //   ~/GraXpert/graxpert/bin/python   (project dir GraXpert, venv graxpert)
            //   ~/GraXpert/.venv/bin/python      (python -m venv .venv)
            //   ~/GraXpert/venv/bin/python       (older convention)
            //   ~/graxpert/.venv/bin/python      (all-lowercase)
            Path.Combine(home, "GraXpert", "graxpert", "bin", "python"),
            Path.Combine(home, "GraXpert", "graxpert", "bin", "python3"),
            Path.Combine(home, "GraXpert", ".venv", "bin", "python"),
            Path.Combine(home, "GraXpert", ".venv", "bin", "python3"),
            Path.Combine(home, "GraXpert", "venv", "bin", "python"),
            Path.Combine(home, "GraXpert", "venv", "bin", "python3"),
            // graxpert-env is the venv name used by the official
            // GraXpert "manual install on Linux" docs, so it's the
            // most common name on a Pi setup.
            Path.Combine(home, "GraXpert", "graxpert-env", "bin", "python"),
            Path.Combine(home, "GraXpert", "graxpert-env", "bin", "python3"),
            Path.Combine(home, "graxpert", ".venv", "bin", "python"),
            Path.Combine(home, "graxpert", ".venv", "bin", "python3"),
            Path.Combine(home, "graxpert", "venv", "bin", "python"),
            Path.Combine(home, "graxpert", "venv", "bin", "python3"),
            Path.Combine(home, "graxpert", "graxpert-env", "bin", "python"),
            Path.Combine(home, "graxpert", "graxpert-env", "bin", "python3")
        ];
    }

    private static string[] MacCandidates() => [
        "/Applications/GraXpert.app/Contents/MacOS/GraXpert",
        "/opt/homebrew/bin/graxpert",
        "/usr/local/bin/graxpert"
    ];

    private string? ProbeVersion() {
        var bin = BinaryPath;
        if (string.IsNullOrEmpty(bin)) return null;
        try {
            using var proc = Process.Start(new ProcessStartInfo {
                FileName = bin,
                Arguments = ArgsPrefix + "--version",
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
            // GraXpert prints "GraXpert 3.0.2" or "v3.0.2" depending
            // on the build; match anything that looks like x.y(.z).
            var m = Regex.Match(stdout, @"(\d+\.\d+(?:\.\d+)?)");
            return m.Success ? m.Groups[1].Value : null;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "GraXpert version probe failed");
            return null;
        }
    }

    private bool IsVersionAtLeast(int major, int minor) {
        var v = Version;
        if (string.IsNullOrEmpty(v)) return false;
        var parts = v.Split('.');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mj))
            return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mn))
            return false;
        return mj > major || (mj == major && mn >= minor);
    }
}

// --- DTOs / records -------------------------------------------------

public enum GraXpertOperation {
    BackgroundExtraction,
    Deconvolution,
    Denoising
}

public sealed record GraXpertOptions(
    GraXpertOperation Operation = GraXpertOperation.BackgroundExtraction,
    string Correction = "Subtraction",
    double Smoothing = 1.0,
    bool SaveBackground = false,
    double DeconStrength = 0.5,
    double DeconPsfSize = 4.0,
    double DenoiseStrength = 0.5,
    // GX-12i: "stars" → -cmd deconv-stellar, "objects" → -cmd deconv-obj.
    // GraXpert CLI splits decon into two distinct subcommands; the
    // previous "-cmd deconvolution" was rejected by GraXpert at runtime.
    string DeconTarget = "stars",
    string? AiVersion = null,
    // RKNN: use the Rockchip NPU for BGE/Denoise when available. False forces
    // the GraXpert CLI (CPU) even on an RK3588 host.
    bool UseNpu = true);

public sealed record GraXpertResult(string OutputPath, string? BackgroundPath,
                                     GraXpertOperation Operation,
                                     double ElapsedSeconds, string? Error);

public sealed record GraXpertBatchRequest(List<string> InputPaths,
                                           GraXpertOptions Options,
                                           int Concurrency = 1);

public class GraXpertBatchJob {
    public string JobId { get; set; } = "";
    public GraXpertOperation Operation { get; set; }
    public int Total { get; set; }
    public int Done { get; set; }
    public int Failed { get; set; }
    public List<string> CurrentlyProcessing { get; set; } = new();
    public List<GraXpertResult> Results { get; set; } = new();
    /// <summary>Live console output (stdout + stderr) from the GraXpert
    /// subprocess(es), capped to the last ~1000 lines. Polled by the UI
    /// so the user can watch the host-side run instead of a blind spinner.</summary>
    public List<string> Log { get; set; } = new();
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool CancelRequested { get; set; }
    /// <summary>Per-job cancellation source. CancelJob cancels it to kill
    /// the in-flight subprocess. Not serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CancellationTokenSource? Cts { get; set; }
}