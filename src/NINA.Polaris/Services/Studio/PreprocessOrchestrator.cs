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
using NINA.Image.FileFormat.FITS;

namespace NINA.Polaris.Services.Studio;

/// <summary>
/// One-click WBPP-style preprocessing: build the master calibration frames,
/// calibrate the lights, optionally grade + drop the weak subs, then register
/// and integrate into a master light. It orchestrates the existing single-stage
/// services (<see cref="MasterFrameService"/>, <see cref="CalibrationService"/>,
/// <see cref="FrameGradingService"/>, <see cref="BatchStackingService"/>) rather
/// than re-implementing any of the math: each stage is fired as its own job and
/// awaited by polling, its output paths threaded into the next stage.
///
/// A calibration slot holding a single frame whose FITS IMAGETYP is already a
/// master is used as-is (no rebuild). When a working folder is set, every output
/// (masters, calibrated lights, master light) lands under it via each service's
/// OutputDir override. Abort cancels between stages (the sub-jobs themselves are
/// not cancellable, so an in-flight stage runs to completion first).
/// </summary>
public class PreprocessOrchestrator {
    private readonly MasterFrameService _masters;
    private readonly CalibrationService _calibrate;
    private readonly FrameGradingService _grade;
    private readonly BatchStackingService _integrate;
    private readonly ILogger<PreprocessOrchestrator> _logger;
    private readonly ConcurrentDictionary<string, PreprocessProgress> _jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cts = new();

    public PreprocessOrchestrator(MasterFrameService masters, CalibrationService calibrate,
        FrameGradingService grade, BatchStackingService integrate,
        ILogger<PreprocessOrchestrator> logger) {
        _masters = masters;
        _calibrate = calibrate;
        _grade = grade;
        _integrate = integrate;
        _logger = logger;
    }

    public string StartJob(PreprocessRequest req) {
        var jobId = Guid.NewGuid().ToString("N")[..8];
        _jobs[jobId] = new PreprocessProgress {
            JobId = jobId, InProgress = true, Phase = "Preparing",
            Stage = "queued", Total = req.Lights.Count
        };
        var cts = new CancellationTokenSource();
        _cts[jobId] = cts;
        _ = Task.Run(() => RunAsync(jobId, req, cts.Token));
        return jobId;
    }

    public PreprocessProgress? GetStatus(string jobId)
        => _jobs.TryGetValue(jobId, out var p) ? p : null;

    public void Abort(string jobId) {
        if (_cts.TryGetValue(jobId, out var cts)) { try { cts.Cancel(); } catch { } }
    }

    private async Task RunAsync(string jobId, PreprocessRequest req, CancellationToken ct) {
        try {
            if (req.Lights == null || req.Lights.Count < 2)
                throw new InvalidOperationException("Add at least two light frames.");

            var method = Enum.TryParse<IntegrationMethod>(req.Method, true, out var im)
                ? im : IntegrationMethod.SigmaClippedMean;
            var outputDir = string.IsNullOrWhiteSpace(req.OutputDir) ? null : req.OutputDir;

            // ---- Stage 1: master calibration frames ----------------------
            var masterBias = await BuildOrUseMaster(jobId, req.Biases, MasterType.Bias, "BuildingBias", method, outputDir, ct);
            var masterDark = await BuildOrUseMaster(jobId, req.Darks, MasterType.Dark, "BuildingDark", method, outputDir, ct);
            var masterFlat = await BuildOrUseMaster(jobId, req.Flats, MasterType.Flat, "BuildingFlat", method, outputDir, ct);

            // ---- Stage 2: calibrate the lights ---------------------------
            var lights = req.Lights;
            if (masterBias != null || masterDark != null || masterFlat != null) {
                ct.ThrowIfCancellationRequested();
                var calJob = _calibrate.StartJob(new CalibrationService.CalibrationRequest(
                    lights, masterDark, masterFlat, masterBias, outputDir));
                var calProg = await Await(jobId, "Calibrating",
                    () => { var s = _calibrate.GetStatus(calJob); return (s?.InProgress ?? false, s?.Done ?? 0, s?.Total ?? 0, s?.Error, s?.Stage); }, ct);
                if (calProg.error != null) throw new InvalidOperationException("Calibration failed: " + calProg.error);
                var calibrated = _calibrate.GetStatus(calJob)?.CalibratedPaths;
                if (calibrated == null || calibrated.Count == 0)
                    throw new InvalidOperationException("Calibration produced no frames.");
                lights = calibrated;
            }

            // ---- Stage 3: grade + drop weak subs (optional) --------------
            if (req.Grade && lights.Count > 2) {
                ct.ThrowIfCancellationRequested();
                double pct = Math.Clamp(req.GradeKeepPercent ?? 80.0, 10.0, 100.0);
                int keepBest = Math.Max(2, (int)Math.Ceiling(lights.Count * pct / 100.0));
                var gradeJob = _grade.StartJob(new FrameGradingService.GradeRequest(
                    lights, null, null, null, null, null, null, keepBest, null));
                await Await(jobId, "Grading",
                    () => { var s = _grade.GetStatus(gradeJob); return (s?.InProgress ?? false, s?.Done ?? 0, s?.Total ?? 0, s?.Error, s?.Stage); }, ct);
                var selected = _grade.GetStatus(gradeJob)?.Selected;
                if (selected != null && selected.Count >= 2) lights = selected;
            }

            // ---- Stage 4: register + integrate ---------------------------
            ct.ThrowIfCancellationRequested();
            var intJob = _integrate.StartJob(new BatchStackingService.IntegrationRequest(
                lights, req.Method, req.DrizzleScale, req.DrizzlePixfrac, outputDir));
            var intProg = await Await(jobId, "Integrating",
                () => { var s = _integrate.GetStatus(intJob); return (s?.InProgress ?? false, s?.Done ?? 0, s?.Total ?? 0, s?.Error, s?.Stage); }, ct);
            if (intProg.error != null) throw new InvalidOperationException("Integration failed: " + intProg.error);
            var final = _integrate.GetStatus(intJob);

            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false, Phase = "Done", Stage = "done",
                OutputPath = final?.OutputPath, Combined = final?.Combined ?? 0, Dropped = final?.Dropped ?? 0
            };
            _logger.LogInformation("Preprocess {Job} done -> {Path}", jobId, final?.OutputPath);
        } catch (OperationCanceledException) {
            _jobs[jobId] = _jobs[jobId] with { InProgress = false, Phase = "Cancelled", Stage = "cancelled", Error = "Aborted" };
        } catch (Exception ex) {
            _logger.LogError(ex, "Preprocess {Job} failed", jobId);
            _jobs[jobId] = _jobs[jobId] with { InProgress = false, Phase = "Failed", Stage = "error", Error = ex.Message };
        } finally {
            _cts.TryRemove(jobId, out _);
        }
    }

    // Build a master from raw frames, OR pass through a single already-master
    // file, OR skip when the slot is empty. Returns the master path (or null).
    private async Task<string?> BuildOrUseMaster(string jobId, List<string>? frames, MasterType type,
            string phase, IntegrationMethod method, string? outputDir, CancellationToken ct) {
        if (frames == null || frames.Count == 0) return null;
        ct.ThrowIfCancellationRequested();
        if (frames.Count == 1 && IsMasterFits(frames[0])) {
            _logger.LogInformation("Preprocess {Job}: using supplied master {Type} {Path}", jobId, type, frames[0]);
            return frames[0];
        }
        var job = _masters.StartJob(frames, type, method, outputDir);
        var prog = await Await(jobId, phase,
            () => { var s = _masters.GetStatus(job); return (s?.InProgress ?? false, s?.Done ?? 0, s?.Total ?? 0, s?.Error, s?.Stage); }, ct);
        if (prog.error != null) throw new InvalidOperationException($"Master {type} failed: {prog.error}");
        return _masters.GetStatus(job)?.OutputPath;
    }

    // Poll a sub-job to completion, mirroring its progress onto this job's block.
    private async Task<(string? error, int done, int total)> Await(string jobId, string phase,
            Func<(bool inProgress, int done, int total, string? error, string? stage)> probe, CancellationToken ct) {
        while (true) {
            ct.ThrowIfCancellationRequested();
            var (inProgress, done, total, error, stage) = probe();
            _jobs[jobId] = _jobs[jobId] with { Phase = phase, Stage = stage ?? phase, Done = done, Total = total };
            if (!inProgress) return (error, done, total);
            await Task.Delay(300, ct);
        }
    }

    private static bool IsMasterFits(string path) {
        try {
            using var fs = File.OpenRead(path);
            var headers = FITSReader.ReadHeadersOnly(fs);
            if (headers.TryGetValue("IMAGETYP", out var card)) {
                var v = (card.Value ?? "").Trim().Trim('\'').Trim().ToUpperInvariant();
                return v.StartsWith("MASTER");
            }
        } catch { /* unreadable header -> treat as raw */ }
        return false;
    }
}

public record PreprocessRequest(
    List<string> Lights,
    List<string> Biases,
    List<string> Darks,
    List<string> Flats,
    string Method,
    int DrizzleScale,
    double DrizzlePixfrac,
    bool Grade,
    double? GradeKeepPercent,
    string? OutputDir);

public record PreprocessProgress {
    public string JobId { get; init; } = "";
    public bool InProgress { get; init; }
    public string Phase { get; init; } = "";     // BuildingBias/Dark/Flat, Calibrating, Grading, Integrating, Done, Failed, Cancelled
    public string Stage { get; init; } = "";     // detailed sub-stage of the current phase
    public int Done { get; init; }
    public int Total { get; init; }
    public int Combined { get; init; }
    public int Dropped { get; init; }
    public string? OutputPath { get; init; }
    public string? Error { get; init; }
}
