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
using NINA.Polaris.Services.Planetary;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// Planetary video workflow, record the live camera stream to a SER
/// file (capture) and stack one into a single image (process).
/// </summary>
public static class VideoEndpoints {
    public static void MapVideoEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/video");

        // ----- Recording (capture) -----

        group.MapPost("/record/start", (VideoRecordingService rec, RecordStartRequest req) => {
            try {
                rec.Start(new RecordingConfig(
                    TargetName: req.TargetName ?? "planet",
                    MaxFrames: req.MaxFrames,
                    MaxDuration: req.MaxDurationSeconds is double s && s > 0
                        ? TimeSpan.FromSeconds(s) : null,
                    ColorMode: req.ColorMode));
                return Results.Ok(new {
                    recording = true,
                    path = rec.OutputPath
                });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/record/stop", async (VideoRecordingService rec) => {
            await rec.StopAsync();
            return Results.Ok(new { recording = false });
        });

        group.MapGet("/record/status", (VideoRecordingService rec) => Results.Ok(new {
            recording = rec.IsRecording,
            path = rec.OutputPath,
            frames = rec.FrameCount,
            bytes = rec.BytesWritten,
            durationSec = rec.Duration.TotalSeconds,
            droppedFrames = rec.DroppedFrames,
            lastError = rec.LastError
        }));

        // ----- Stacking (process) -----

        group.MapPost("/stack/start", (PlanetaryStackerService stacker,
                                       ProfileService profiles,
                                       StackStartRequest req) => {
            if (string.IsNullOrWhiteSpace(req.SerPath))
                return Results.BadRequest(new { error = "serPath required" });
            var outDir = !string.IsNullOrWhiteSpace(req.OutputDir)
                ? req.OutputDir!
                : Path.Combine(Path.GetDirectoryName(req.SerPath) ?? ".", "stacked");
            var job = stacker.StartJob(new StackConfig(
                SerPath: req.SerPath,
                OutputDir: outDir,
                KeepPercent: req.KeepPercent ?? 50,
                OutputName: req.OutputName ?? "stack"));
            return Results.Accepted($"/api/video/stack/{job.Id}", new { jobId = job.Id });
        });

        group.MapGet("/stack/{jobId}", (string jobId, PlanetaryStackerService stacker) => {
            var job = stacker.GetJob(jobId);
            if (job == null) return Results.NotFound(new { error = "Job not found" });
            return Results.Ok(new {
                id = job.Id,
                phase = job.Phase.ToString(),
                totalFrames = job.TotalFrames,
                framesAnalyzed = job.FramesAnalyzed,
                framesPicked = job.FramesPicked,
                framesAligned = job.FramesAligned,
                framesStacked = job.FramesStacked,
                outputPath = job.OutputPath,
                error = job.Error,
                startedAt = job.StartedAt,
                completedAt = job.CompletedAt,
                done = job.Phase == StackPhase.Ok || job.Phase == StackPhase.Fail,
                // QualityScores deliberately omitted from the routine status
                // response (can be 10000+ doubles). Use /stack/{id}/qualities.
            });
        });

        group.MapGet("/stack/{jobId}/qualities", (string jobId, PlanetaryStackerService stacker) => {
            var job = stacker.GetJob(jobId);
            if (job == null) return Results.NotFound(new { error = "Job not found" });
            return Results.Ok(new { qualities = job.QualityScores ?? Array.Empty<double>() });
        });

        group.MapPost("/stack/{jobId}/abort", (string jobId, PlanetaryStackerService stacker) => {
            stacker.Abort(jobId);
            return Results.Ok(new { aborted = true });
        });
    }

    public record RecordStartRequest(
        string? TargetName = null,
        int? MaxFrames = null,
        double? MaxDurationSeconds = null,
        SerColorMode? ColorMode = null);

    public record StackStartRequest(
        string SerPath,
        string? OutputDir = null,
        double? KeepPercent = null,
        string? OutputName = null);
}