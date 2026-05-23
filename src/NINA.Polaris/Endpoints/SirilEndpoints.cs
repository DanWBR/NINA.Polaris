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
        // Cheap — Version is cached after the first probe.
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

        g.MapGet("/jobs/{jobId}", (SirilService siril, string jobId) => {
            var job = siril.GetJob(jobId);
            return job == null ? Results.NotFound() : Results.Ok(job);
        });

        g.MapGet("/jobs", (SirilService siril) => Results.Ok(siril.ActiveJobs));

        g.MapPost("/jobs/{jobId}/cancel", (SirilService siril, string jobId) => {
            var ok = siril.CancelJob(jobId);
            return ok ? Results.Ok(new { ok = true })
                      : Results.NotFound(new { error = "Job not found or already finished" });
        });

        // Re-probe — used by the Settings "Re-detect" button after
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
