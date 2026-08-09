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
                    ColorMode: req.ColorMode,
                    // PLAN8: anything but an explicit 8 means 16, so a client
                    // that does not send the field keeps today's behaviour.
                    BitDepth: req.BitDepth == 8 ? 8 : 16));
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

        // ----- Recorded files (process picker) -----

        // List every *.ser recording, newest first. Authoritative + recursive:
        // the generic /api/files/list is non-recursive and the recordings live
        // a couple of levels down, so a client-side walk finds nothing.
        //
        // PLANPATH: two kinds of root. New clips go to
        // {ImageOutputDir}/{rig}/planetary; before that they all went to
        // {ImageOutputDir}/planetary, flat across every rig. The legacy root is
        // still scanned, so a season of existing recordings does not vanish
        // from the picker the moment the host updates. Nothing is moved --
        // that is the operator's data, and tens of gigabytes of it.
        group.MapGet("/recordings", (ProfileService profiles) => {
            var outDir = profiles.Active.ImageOutputDir;
            if (string.IsNullOrWhiteSpace(outDir) || !Directory.Exists(outDir))
                return Results.Ok(new { recordings = Array.Empty<object>() });

            var roots = new List<string>();
            try {
                // Every rig, not only the active one: switching rigs must not
                // hide the clips recorded last night.
                foreach (var rigDir in Directory.EnumerateDirectories(outDir)) {
                    var p = Path.Combine(rigDir, "planetary");
                    if (Directory.Exists(p)) roots.Add(p);
                }
            } catch { /* an unreadable capture root simply yields the legacy one */ }
            var legacy = Path.Combine(outDir, "planetary");
            if (Directory.Exists(legacy)) roots.Add(legacy);
            if (roots.Count == 0)
                return Results.Ok(new { recordings = Array.Empty<object>() });

            try {
                var recordings = roots
                    .SelectMany(r => Directory.EnumerateFiles(r, "*.ser", SearchOption.AllDirectories))
                    // A rig folder literally named "planetary" would otherwise
                    // be walked twice, as a rig root and as the legacy one.
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(fi => fi.LastWriteTimeUtc)
                    .Select(fi => new {
                        path = fi.FullName,
                        name = fi.Name,
                        target = fi.Directory?.Name ?? "",
                        // Which rig a clip belongs to, so the picker can tell
                        // two identically named targets apart. Empty for the
                        // legacy tree, which predates the distinction.
                        rig = RigOfRecording(outDir, fi.FullName),
                        sizeBytes = fi.Length,
                        modifiedUtc = fi.LastWriteTimeUtc
                    })
                    .ToList();
                return Results.Ok(new { recordings });
            } catch (Exception ex) {
                return Results.Ok(new { recordings = Array.Empty<object>(), error = ex.Message });
            }
        });

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

    /// <summary>The rig a recording sits under, read back from its path.
    ///
    /// <para>Three shapes are on disk at once and all of them have to read
    /// back, because nothing rewrites clips that are already written:</para>
    /// <list type="bullet">
    ///   <item>current: <c>{rig}/{target}/planetary/x.ser</c></item>
    ///   <item>no target: <c>{rig}/planetary/x.ser</c>, and the same shape as
    ///         the previous layout's <c>{rig}/planetary/{target}/x.ser</c></item>
    ///   <item>legacy, pre per-rig folders: <c>planetary/{target}/x.ser</c>,
    ///         which genuinely does not know which rig shot it</item>
    /// </list>
    ///
    /// <para>So: look for the "planetary" segment anywhere FROM INDEX 1. Found
    /// means segment 0 is the rig. Not found leaves only the legacy tree, where
    /// segment 0 is the literal folder. Starting the search at 1 rather than 0
    /// is what keeps a rig actually named "planetary" from reading as a legacy
    /// clip.</para></summary>
    private static string RigOfRecording(string outDir, string filePath) {
        try {
            var rel = Path.GetRelativePath(outDir, filePath);
            var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (var i = 1; i < parts.Length - 1; i++) {
                if (parts[i].Equals("planetary", StringComparison.OrdinalIgnoreCase))
                    return parts[0];
            }
        } catch { /* a path outside the capture root has no rig to report */ }
        return "";
    }

    public record RecordStartRequest(
        string? TargetName = null,
        int? MaxFrames = null,
        double? MaxDurationSeconds = null,
        SerColorMode? ColorMode = null,
        /// <summary>PLAN8: 8 or 16 bits per sample on disk. Omitted = 16.</summary>
        int? BitDepth = null);

    public record StackStartRequest(
        string SerPath,
        string? OutputDir = null,
        double? KeepPercent = null,
        string? OutputName = null);
}