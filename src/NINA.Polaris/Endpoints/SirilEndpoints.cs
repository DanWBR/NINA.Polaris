using Microsoft.AspNetCore.Http;
using NINA.Polaris.Services;
using NINA.Polaris.Services.External;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// HTTP surface for SirilService. Mirrors the StudioEndpoints style:
/// long-running jobs return 202 + jobId immediately; the client polls
/// /jobs/{id} for progress (also broadcast via WS for hot updates).
/// </summary>
public static class SirilEndpoints {
    public static void MapSirilEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/siril");

        // Lightweight status for the Settings panel detection row.
        // Cheap, Version is cached after the first probe.
        g.MapGet("/status", (SirilService siril) => Results.Ok(new {
            available = siril.IsAvailable,
            binaryPath = siril.BinaryPath,
            version = siril.Version,
            scriptsCount = siril.EnumerateScripts().Count
        }));

        // Diagnostic list of every place we looked for siril-cli +
        // every scripts dir we searched. Powers the "we tried here..."
        // hint shown when Siril isn't detected.
        g.MapGet("/diagnostic", (SirilService siril) => Results.Ok(new {
            binaryCandidates = siril.EnumerateBinaryCandidates(),
            userScriptDirs = siril.UserScriptDirs().Select(d => new {
                path = d,
                exists = Directory.Exists(d)
            })
        }));

        g.MapGet("/scripts", (SirilService siril)
            => Results.Ok(siril.EnumerateScripts()));

        g.MapPost("/run", (SirilService siril, SirilRunRequest req) => {
            if (!siril.IsAvailable)
                return Results.Json(new { error = "Siril is not installed on this host" },
                    statusCode: StatusCodes.Status409Conflict);
            if (req.LightPaths == null || req.LightPaths.Count == 0)
                return Results.BadRequest(new { error = "lightPaths is required" });
            try {
                var job = siril.StartJob(new SirilJobRequest(
                    ScriptName: req.ScriptName,
                    TargetName: req.TargetName,
                    LightPaths: req.LightPaths,
                    DarkPaths: req.DarkPaths,
                    FlatPaths: req.FlatPaths,
                    BiasPaths: req.BiasPaths,
                    WorkDirOverride: req.WorkDirOverride));
                return Results.Accepted(value: new { jobId = job.JobId, stage = job.Stage });
            } catch (Exception ex) {
                return Results.Problem(ex.Message);
            }
        });

        g.MapGet("/jobs/{jobId}", (SirilService siril, string jobId,
                                    [Microsoft.AspNetCore.Mvc.FromQuery] int? sinceLine) => {
            var job = siril.GetJob(jobId);
            if (job == null) return Results.NotFound();
            // Lock-snapshotted copy so the JSON serializer doesn't race
            // against the stdout reader writing new lines. sinceLine
            // lets the UI's polling fetch only the tail since the last
            // poll — keeps payload tiny for jobs with 500-line buffers.
            var snap = job.SnapshotLog();
            var since = Math.Max(0, sinceLine ?? 0);
            // totalLines is the absolute end-of-stream index (post-
            // truncation it equals snap.Count; the client uses it to
            // compute the next sinceLine). When the buffer wraps past
            // 500 we lose the head but that's fine — the UI keeps its
            // own already-rendered lines and only appends what's new.
            var totalLines = snap.Count;
            var tail = since >= totalLines
                ? Array.Empty<string>()
                : snap.GetRange(since, totalLines - since).ToArray();
            return Results.Ok(new {
                job.JobId, job.ScriptName, job.ScriptPath, job.TargetName,
                job.Stage, job.PercentDone, job.WorkDir, job.ResultPath,
                job.LastError, job.StartedAt, job.CompletedAt,
                job.CancelRequested,
                logLines = tail,
                totalLines
            });
        });

        g.MapGet("/jobs", (SirilService siril) => Results.Ok(siril.ActiveJobs));

        g.MapPost("/jobs/{jobId}/cancel", (SirilService siril, string jobId) => {
            var ok = siril.CancelJob(jobId);
            return ok ? Results.Ok(new { ok = true })
                      : Results.NotFound(new { error = "Job not found or already finished" });
        });

        // Re-probe, used by the Settings "Re-detect" button after
        // the user installs Siril or fixes the configured path.
        g.MapPost("/redetect", (SirilService siril) => {
            siril.InvalidateVersionCache();
            return Results.Ok(new {
                available = siril.IsAvailable,
                binaryPath = siril.BinaryPath,
                version = siril.Version
            });
        });
    }

    public record SirilRunRequest(
        string ScriptName,
        string? TargetName,
        List<string> LightPaths,
        List<string>? DarkPaths,
        List<string>? FlatPaths,
        List<string>? BiasPaths,
        string? WorkDirOverride);
}
