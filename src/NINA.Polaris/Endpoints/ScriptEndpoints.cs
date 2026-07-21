// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. See <https://www.gnu.org/licenses/>.

using System.Text.Json;
using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

/// <summary>HTTP surface for the polarispy script runner. The web UI lists and
/// runs scripts and polls their status; the running script itself reports log +
/// progress back through /log and /progress (over the loopback API, which is
/// auth-exempt).</summary>
public static class ScriptEndpoints {
    public record RunRequest(string Path, string? ActiveFrame, string? Cwd);
    public record LogRequest(string Message);
    public record ProgressRequest(string? Message, double? Fraction);

    public static void MapScriptEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/script");

        g.MapGet("/list", (ScriptRunnerService svc) =>
            Results.Ok(svc.ListScripts().Select(s => new {
                name = s.Name, path = s.Path, description = s.Description, builtIn = s.BuiltIn
            })));

        g.MapPost("/run", (RunRequest req, ScriptRunnerService svc) => {
            if (string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new { error = "path is required" });
            try {
                var job = svc.Run(req.Path, req.ActiveFrame, req.Cwd);
                return Results.Ok(new { jobId = job.Id, state = job.State, error = job.Error });
            } catch (UnauthorizedAccessException uae) {
                return Results.Json(new { error = uae.Message }, statusCode: StatusCodes.Status403Forbidden);
            } catch (FileNotFoundException) {
                return Results.NotFound(new { error = "Script not found." });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        g.MapGet("/{jobId}/status", (string jobId, ScriptRunnerService svc) => {
            var job = svc.Get(jobId);
            if (job == null) return Results.NotFound(new { error = "Unknown job." });
            string[] logCopy;
            lock (job.Log) logCopy = job.Log.ToArray();
            return Results.Ok(new {
                id = job.Id, name = job.Name, state = job.State,
                progress = job.Progress, progressMessage = job.ProgressMessage,
                exitCode = job.ExitCode, error = job.Error, log = logCopy,
                dialog = svc.PendingDialog(job)
            });
        });

        g.MapPost("/{jobId}/cancel", (string jobId, ScriptRunnerService svc) => {
            svc.Cancel(jobId);
            return Results.Ok(new { ok = true });
        });

        // Back-channel from polarispy (loopback, auth-exempt).
        g.MapPost("/{jobId}/log", (string jobId, LogRequest req, ScriptRunnerService svc) => {
            svc.AddLog(jobId, req.Message ?? "");
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/{jobId}/progress", (string jobId, ProgressRequest req, ScriptRunnerService svc) => {
            svc.SetProgress(jobId, req.Message, req.Fraction);
            return Results.Ok(new { ok = true });
        });

        // ----- Phase 2: declarative dialog bridge -----
        // The script posts a form spec (arbitrary JSON) and long-polls the result.
        g.MapPost("/{jobId}/dialog", (string jobId, JsonElement spec, ScriptRunnerService svc) => {
            svc.SetDialog(jobId, spec);
            return Results.Ok(new { ok = true });
        });

        g.MapGet("/{jobId}/dialog/result", (string jobId, ScriptRunnerService svc) =>
            Results.Ok(svc.DialogResult(jobId)));

        // The browser submits the entered values ({ values: {...} }) or cancels.
        g.MapPost("/{jobId}/dialog/submit", (string jobId, JsonElement body, ScriptRunnerService svc) => {
            var values = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("values", out var v)
                ? v : body;
            svc.SubmitDialog(jobId, values);
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/{jobId}/dialog/cancel", (string jobId, ScriptRunnerService svc) => {
            svc.CancelDialog(jobId);
            return Results.Ok(new { ok = true });
        });
    }
}
