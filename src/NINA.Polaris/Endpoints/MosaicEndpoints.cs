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
using NINA.Polaris.Services.Mosaic;
using NINA.Polaris.Services.Sequencer;

namespace NINA.Polaris.Endpoints;

public static class MosaicEndpoints {
    public static void MapMosaicEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/mosaic");

        // Plan only, returns panel coords + time estimate; the UI overlays this on Aladin
        g.MapPost("/plan", (MosaicRequest req, MosaicPlannerService planner) => {
            try {
                var plan = planner.Plan(req);
                return Results.Ok(plan);
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Plan + lower into an Advanced Sequencer document. Caller can either
        // load it into the engine immediately (loadIntoEngine=true) or get the
        // SequenceDocument back for hand-tuning before loading.
        g.MapPost("/to-sequence", (MosaicToSequenceRequest req,
            MosaicPlannerService planner, AdvancedSequenceEngine engine) => {
            try {
                var plan = planner.Plan(req.Mosaic);
                var doc = planner.ToSequenceDocument(plan,
                    req.ExposureSeconds, req.ExposureCount,
                    req.FilterName, req.Gain, req.Binning);
                if (req.LoadIntoEngine) engine.Load(doc);
                return Results.Ok(new {
                    plan,
                    document = doc,
                    loadedIntoEngine = req.LoadIntoEngine,
                    validation = req.LoadIntoEngine ? engine.Validate() : null
                });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // MOSAIC mode. The panel size of the connected camera and rig, so the
        // tab can show how many panels a field needs before anything moves.
        g.MapGet("/fov", (MosaicRunService runner) => {
            var fov = runner.EstimatePanelFov();
            return fov == null
                ? Results.Ok(new { known = false })
                : Results.Ok(new {
                    known = true,
                    widthDeg = fov.Value.widthDeg,
                    heightDeg = fov.Value.heightDeg,
                    scaleArcsec = fov.Value.scaleArcsec
                });
        });

        g.MapGet("/run", (MosaicRunService runner) => Results.Ok(runner.Status()));

        g.MapPost("/run", (MosaicRunRequest req, MosaicRunService runner) => {
            var refusal = runner.Start(req);
            return refusal == null
                ? Results.Ok(runner.Status())
                : Results.BadRequest(new { error = refusal });
        });

        g.MapPost("/run/stop", async (MosaicRunService runner) => {
            await runner.StopAsync();
            return Results.Ok(runner.Status());
        });

        // The composited view. The status carries a version; the tab asks
        // for this again only when it changes.
        g.MapGet("/run/canvas.jpg", (MosaicRunService runner) => {
            var jpeg = runner.CanvasJpeg();
            return jpeg == null ? Results.NotFound() : Results.File(jpeg, "image/jpeg");
        });
    }

    public record MosaicToSequenceRequest(
        MosaicRequest Mosaic,
        double ExposureSeconds,
        int ExposureCount,
        string? FilterName,
        int? Gain,
        int Binning,
        bool LoadIntoEngine);
}