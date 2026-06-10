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

using Microsoft.AspNetCore.Http;
using NINA.Polaris.Services;
using NINA.Polaris.Services.External;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// HTTP surface for GraXpertService. Same shape as SirilEndpoints:
/// status + diagnostic for the Settings panel, run/jobs/cancel for
/// the actual processing. Single endpoint accepts any of the three
/// operations (BGE, Deconvolution, Denoising) via the request body.
/// </summary>
public static class GraXpertEndpoints {
    public static void MapGraXpertEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/graxpert");

        g.MapGet("/status", (GraXpertService gx) => Results.Ok(new {
            available = gx.IsAvailable,
            binaryPath = gx.BinaryPath,
            version = gx.Version,
            supportsDeconvolution = gx.SupportsDeconvolution,
            supportsDenoising = gx.SupportsDenoising,
            // RKNN: NPU acceleration for BGE/Denoise on RK3588. When true the
            // host can run those even without the GraXpert CLI installed.
            npuAvailable = gx.NpuAvailable,
            npuDiagnostics = gx.NpuDiagnostics
        }));

        g.MapGet("/diagnostic", (GraXpertService gx) => Results.Ok(new {
            binaryCandidates = gx.EnumerateBinaryCandidates()
        }));

        g.MapPost("/run", (GraXpertService gx, GraXpertRunRequest req) => {
            if (req.Paths == null || req.Paths.Count == 0)
                return Results.BadRequest(new { error = "paths is required" });

            var op = ParseOperation(req.Operation);
            if (op == null)
                return Results.BadRequest(new { error = $"Unknown operation: {req.Operation}" });

            // The NPU path can serve BGE/Denoise even without the GraXpert CLI.
            // Decon always needs the CLI. The user can force the CLI by sending
            // UseNpu=false (e.g. to compare, or when NPU quality isn't wanted).
            bool useNpu = req.UseNpu ?? true;
            bool canNpu = gx.NpuAvailable && useNpu &&
                (op == GraXpertOperation.BackgroundExtraction || op == GraXpertOperation.Denoising);
            if (!gx.IsAvailable && !canNpu)
                return Results.Json(new { error = "GraXpert is not installed on this host" },
                    statusCode: StatusCodes.Status409Conflict);
            if (op == GraXpertOperation.Deconvolution && !gx.SupportsDeconvolution)
                return Results.Json(new { error = "Deconvolution requires GraXpert v3.0+" },
                    statusCode: StatusCodes.Status409Conflict);
            if (op == GraXpertOperation.Denoising && !canNpu && !gx.SupportsDenoising)
                return Results.Json(new { error = "Denoising requires GraXpert v3.0+" },
                    statusCode: StatusCodes.Status409Conflict);

            var opts = new GraXpertOptions(
                Operation: op.Value,
                Correction: req.Correction ?? "Subtraction",
                Smoothing: req.Smoothing ?? 1.0,
                SaveBackground: req.SaveBackground ?? false,
                DeconStrength: req.DeconStrength ?? 0.5,
                DeconPsfSize: req.DeconPsfSize ?? 4.0,
                DenoiseStrength: req.DenoiseStrength ?? 0.5,
                DeconTarget: req.DeconTarget ?? "stars",
                // Pass the requested model version through so the host CLI
                // run pins to (and stages) the exact model Polaris has,
                // instead of letting GraXpert pick the latest and download
                // it. Null is fine: the service falls back to the newest
                // version it can find locally for this operation.
                AiVersion: req.AiVersion,
                UseNpu: useNpu);
            var job = gx.StartBatch(new GraXpertBatchRequest(
                req.Paths, opts, req.Concurrency ?? 1));
            return Results.Accepted(value: new { jobId = job.JobId });
        });

        g.MapGet("/jobs/{jobId}", (GraXpertService gx, string jobId) => {
            var job = gx.GetJob(jobId);
            return job == null ? Results.NotFound() : Results.Ok(SnapshotJob(job));
        });

        g.MapGet("/jobs", (GraXpertService gx) =>
            Results.Ok(gx.ActiveJobs.Select(SnapshotJob).ToList()));

        g.MapPost("/jobs/{jobId}/cancel", (GraXpertService gx, string jobId) => {
            var ok = gx.CancelJob(jobId);
            return ok ? Results.Ok(new { ok = true })
                      : Results.NotFound(new { error = "Job not found or already finished" });
        });

        g.MapPost("/redetect", (GraXpertService gx) => {
            gx.InvalidateVersionCache();
            return Results.Ok(new {
                available = gx.IsAvailable,
                binaryPath = gx.BinaryPath,
                version = gx.Version,
                supportsDeconvolution = gx.SupportsDeconvolution,
                supportsDenoising = gx.SupportsDenoising
            });
        });
    }

    // Serialize a consistent snapshot of the job. The service mutates
    // Log / Results / CurrentlyProcessing live under lock(job); copying
    // the lists under the same lock prevents a "Collection was modified"
    // exception when System.Text.Json enumerates them mid-run.
    private static object SnapshotJob(GraXpertBatchJob job) {
        lock (job) {
            return new {
                jobId = job.JobId,
                operation = job.Operation.ToString(),
                total = job.Total,
                done = job.Done,
                failed = job.Failed,
                currentlyProcessing = job.CurrentlyProcessing.ToList(),
                results = job.Results.ToList(),
                log = job.Log.ToList(),
                startedAt = job.StartedAt,
                completedAt = job.CompletedAt,
                cancelRequested = job.CancelRequested
            };
        }
    }

    private static GraXpertOperation? ParseOperation(string? s) {
        return (s ?? "").ToLowerInvariant() switch {
            "background-extraction" or "bge" or "" => GraXpertOperation.BackgroundExtraction,
            "deconvolution" or "decon"             => GraXpertOperation.Deconvolution,
            "denoising" or "denoise"               => GraXpertOperation.Denoising,
            _ => null
        };
    }

    public record GraXpertRunRequest(
        List<string> Paths,
        string? Operation,
        // Common
        int? Concurrency,
        string? AiVersion,
        // BGE
        string? Correction,
        double? Smoothing,
        bool? SaveBackground,
        // Decon
        double? DeconStrength,
        double? DeconPsfSize,
        // GX-12i: "stars" → deconv-stellar, "objects" → deconv-obj.
        // Drives both the GraXpert CLI subcommand and the output suffix
        // (_decon_stars vs _decon_objects) so the two runs don't collide.
        string? DeconTarget,
        // Denoise
        double? DenoiseStrength,
        // RKNN: when the host has an NPU (RK3588), use it for BGE/Denoise.
        // Null/true = use the NPU when available; false = force the GraXpert
        // CLI (CPU) instead. Only meaningful when npuAvailable.
        bool? UseNpu);
}