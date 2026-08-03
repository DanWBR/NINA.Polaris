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

using NINA.Polaris.Services;

namespace NINA.Polaris.WebSocket.Status;

/// <summary>
/// Jobs and long-running analyses the operator watches finish.
///
/// Blocks owned: sirilJobs, graXpertJobs, decon, plateSolve, polarAlignment, sensorAnalysis, benchmark.
/// </summary>
public sealed class ProcessingStatusContributor : IStatusContributor {
    private readonly BenchmarkService _benchmark;
    private readonly DeconProgressService _deconProgress;
    private readonly NINA.Polaris.Services.External.GraXpertService _graxpert;
    private readonly NINA.Polaris.Services.PlateSolving.PlateSolveProgressService _plateSolveProgress;
    private readonly PolarAlignmentService _polarAlign;
    private readonly SensorAnalysisService _sensorAnalysis;
    private readonly NINA.Polaris.Services.External.SirilService _siril;

    public ProcessingStatusContributor(BenchmarkService benchmark, DeconProgressService deconProgress, NINA.Polaris.Services.External.GraXpertService graxpert, NINA.Polaris.Services.PlateSolving.PlateSolveProgressService plateSolveProgress, PolarAlignmentService polarAlign, SensorAnalysisService sensorAnalysis, NINA.Polaris.Services.External.SirilService siril) {
        _benchmark = benchmark;
        _deconProgress = deconProgress;
        _graxpert = graxpert;
        _plateSolveProgress = plateSolveProgress;
        _polarAlign = polarAlign;
        _sensorAnalysis = sensorAnalysis;
        _siril = siril;
    }

    public IReadOnlyCollection<string> Keys { get; } = new[] { "sirilJobs", "graXpertJobs", "decon", "plateSolve", "polarAlignment", "sensorAnalysis", "benchmark" };

    public void Contribute(StatusTick tick) {
        var benchmark = _benchmark;
        var deconProgress = _deconProgress;
        var graxpert = _graxpert;
        var plateSolveProgress = _plateSolveProgress;
        var polarAlign = _polarAlign;
        var sensorAnalysis = _sensorAnalysis;
        var siril = _siril;

        // Compact summaries for the activity bar. Full job
        // detail (lights paths, results, etc) lives on the
        // per-tool endpoints, only the surface needed for
        // chips makes it into the broadcast.
        var sirilJobsPayload = siril.ActiveJobs.Select(j => new {
            j.JobId, j.ScriptName, j.TargetName, j.Stage, j.PercentDone
        }).ToList();

        var graXpertJobsPayload = graxpert.ActiveJobs.Select(j => new {
            j.JobId,
            operation = j.Operation.ToString(),
            j.Done, j.Total, j.Failed
        }).ToList();

            tick.Blocks["sirilJobs"] = sirilJobsPayload;

            tick.Blocks["graXpertJobs"] = graXpertJobsPayload;

            // CLOCK-1: serverUtcNow lets the client compute
            // wall-clock skew against its own Date.now()
            // every tick. When |skew| > 30s the activity
            // bar shows a "Clock N off" chip and the
            // Settings card surfaces a Sync button.
            tick.Blocks["decon"] = BuildDeconPayload(deconProgress);

            // Opt-in server-owned LIVE loop state. running=true means
            // the server is driving the LIVE session (the client only
            // offloads stacking); the LIVE shutter binds to this so a
            // reconnecting browser sees the session is still going.
            tick.Blocks["plateSolve"] = BuildPlateSolvePayload(plateSolveProgress);

            // Server-authoritative current-exposure progress so
            // every capture button's "Xs of Ys" countdown survives
            // a reconnect. startedUtc + the server block's utcNow
            // let the client compute elapsed without trusting its
            // own (possibly skewed / freshly reloaded) clock.
            tick.Blocks["polarAlignment"] = polarAlign.CurrentJob == null ? null : new {
                jobId = polarAlign.CurrentJob.Id,
                phase = polarAlign.CurrentJob.Phase.ToString(),
                mode = polarAlign.CurrentJob.Mode,
                isActive = polarAlign.CurrentJob.IsActive,
                points = polarAlign.CurrentJob.Points,
                azErrorArcsec = polarAlign.CurrentJob.AzErrorArcsec,
                altErrorArcsec = polarAlign.CurrentJob.AltErrorArcsec,
                totalErrorArcsec = polarAlign.CurrentJob.TotalErrorArcsec,
                lastError = polarAlign.CurrentJob.LastError,
                startedAt = polarAlign.CurrentJob.StartedAt,
                completedAt = polarAlign.CurrentJob.CompletedAt,
                // True only while the CONTINUOUS refine loop runs
                // (not during a single-shot manual Refresh) — the
                // POLAR tab's Auto toggle mirrors this.
                refineLoop = polarAlign.RefineLoopActive,
                // RDPA-2: rudimentary-mode fields. Null in TPPA
                // mode (the frontend gates on mode==='rudimentary'
                // before reading these). Includes target +
                // last solved + iteration sparkline data.
                targetRaHours = polarAlign.CurrentJob.TargetRaHours,
                targetDecDeg = polarAlign.CurrentJob.TargetDecDeg,
                targetName = polarAlign.CurrentJob.TargetName,
                solvedRaHours = polarAlign.CurrentJob.SolvedRaHours,
                solvedDecDeg = polarAlign.CurrentJob.SolvedDecDeg,
                iterationCount = polarAlign.CurrentJob.History.Count,
                history = polarAlign.CurrentJob.History
            };

            // DBGLOG-5: ship new log entries since last tick
            // (max 50 per tick). truncated=true if the
            // cursor fell behind the ring-buffer head so the
            // client knows it missed entries and should
            // refetch via GET /api/logs.
            tick.Blocks["sensorAnalysis"] = new {
                state    = sensorAnalysis.State,
                progress = sensorAnalysis.Progress,
                phase    = sensorAnalysis.Phase
            };

            // PA-4: TPPA orchestrator state. CurrentJob is
            // null until the user clicks Start; serialise a
            // null-shaped object so the front-end can bind
            // without null checks.
            tick.Blocks["benchmark"] = new {
                state    = benchmark.State,
                progress = benchmark.Progress,
                phase    = benchmark.Phase
            };

            // Sensor analysis (e/ADU, read noise, full well vs
            // gain). Compact progress here; full result via REST.
    }

    private static object BuildDeconPayload(DeconProgressService svc) {
        try {
            var s = svc.Snapshot();
            return new {
                runId = s.RunId,
                active = s.Active,
                phase = s.Phase,
                fraction = s.Fraction,
                elapsedSeconds = s.ElapsedSeconds,
                etaSeconds = s.EtaSeconds
            };
        } catch {
            return new { runId = 0L, active = false, phase = (string?)null,
                         fraction = 0.0, elapsedSeconds = 0.0, etaSeconds = (double?)null };
        }
    }
    private static object BuildPlateSolvePayload(
        NINA.Polaris.Services.PlateSolving.PlateSolveProgressService svc) {
        try {
            var s = svc.Snapshot();
            return new {
                runId = s.RunId,
                active = s.Active,
                source = s.Source,
                seq = s.Seq,
                truncated = s.Truncated,
                lines = s.Lines
            };
        } catch {
            return new { runId = 0L, active = false, source = (string?)null,
                         seq = 0L, truncated = false, lines = System.Array.Empty<string>() };
        }
    }
}
