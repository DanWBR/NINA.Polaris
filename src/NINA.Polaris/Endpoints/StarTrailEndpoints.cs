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

using NINA.Polaris.Services.StarTrail;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// Star-trails capture + composite (VIDEO → Star trails). Mirrors the planetary
/// stack / time-lapse job endpoints: start returns a jobId, progress rides the
/// WS <c>starTrail</c> block, stop finalizes the master, abort cancels (and
/// still finalizes what was captured).
/// </summary>
public static class StarTrailEndpoints {
    public static void MapStarTrailEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/startrail");

        group.MapPost("/start", (StarTrailService svc, StarTrailStartRequest req) => {
            if (req.ExposureSeconds is not > 0)
                return Results.BadRequest(new { error = "exposureSeconds must be greater than 0" });
            var active = svc.CurrentJob;
            if (active != null && active.Phase is StarTrailPhase.Preparing
                    or StarTrailPhase.Capturing or StarTrailPhase.Finalizing)
                return Results.BadRequest(new { error = "A star-trail run is already in progress" });

            var job = svc.StartJob(new StarTrailConfig(
                ExposureSeconds: req.ExposureSeconds!.Value,
                Gain: req.Gain ?? 0,
                Binning: Math.Max(1, req.Binning ?? 1),
                IntervalSeconds: Math.Max(0, req.IntervalSeconds ?? 0),
                MaxFrames: req.MaxFrames is > 0 ? req.MaxFrames : null,
                TurnTrackingOff: req.TurnTrackingOff ?? true,
                CosmeticCorrection: req.CosmeticCorrection ?? true,
                SaveSubs: req.SaveSubs ?? false,
                AlsoTimelapse: req.AlsoTimelapse ?? false,
                OutputName: string.IsNullOrWhiteSpace(req.OutputName) ? "startrail" : req.OutputName!));
            return Results.Accepted($"/api/startrail/{job.Id}", new { jobId = job.Id });
        });

        group.MapGet("/{jobId}", (string jobId, StarTrailService svc) => {
            var job = svc.GetJob(jobId);
            if (job == null) return Results.NotFound(new { error = "Job not found" });
            return Results.Ok(new {
                id = job.Id,
                phase = job.Phase.ToString(),
                framesCaptured = job.FramesCaptured,
                exposureSec = job.ExposureSeconds,
                trackingOff = job.TrackingOff,
                outputPathFits = job.OutputPathFits,
                outputPathJpg = job.OutputPathJpg,
                error = job.Error,
                startedAt = job.StartedAt,
                completedAt = job.CompletedAt,
                done = job.Phase is StarTrailPhase.Ok or StarTrailPhase.Fail
            });
        });

        // Graceful stop: break the loop and write the master.
        group.MapPost("/{jobId}/stop", (string jobId, StarTrailService svc) => {
            svc.Stop(jobId);
            return Results.Ok(new { stopping = true });
        });

        // Cancel. The partial composite is still finalized.
        group.MapPost("/{jobId}/abort", (string jobId, StarTrailService svc) => {
            svc.Abort(jobId);
            return Results.Ok(new { aborted = true });
        });
    }

    public record StarTrailStartRequest(
        double? ExposureSeconds,
        int? Gain,
        int? Binning,
        int? IntervalSeconds,
        int? MaxFrames,
        bool? TurnTrackingOff,
        bool? CosmeticCorrection,
        bool? SaveSubs,
        bool? AlsoTimelapse,
        string? OutputName);
}
