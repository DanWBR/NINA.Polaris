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
using NINA.Polaris.Services.Studio;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// POST /api/decon/rl — classical, measured-PSF Richardson-Lucy deconvolution.
/// Server-side (the math is C#), unlike the in-browser AI decon. Writes a
/// `{stem}_rl.fits` sibling per input and returns the measured PSF stats so the
/// UI can show what shape was reversed. The frame library is rescanned so the
/// outputs appear in FILES/STUDIO without a manual refresh.
///
/// Runs synchronously on the wire: RL on a full master is seconds-to-tens of
/// seconds on a desktop (GPU acceleration is a later phase), so the client
/// awaits with a spinner like the plate-solve / crop calls.
/// </summary>
public static class DeconEndpoints {
    public static void MapDeconEndpoints(this IEndpointRouteBuilder app) {
        var g = app.MapGroup("/api/decon");

        g.MapPost("/rl", async (
                DeconvolutionService svc,
                FrameLibraryService library,
                DeconRequest req) => {
            if (req.Paths == null || req.Paths.Length == 0)
                return Results.BadRequest(new { error = "paths is required" });

            double strength = req.Strength is >= 0 and <= 1 ? req.Strength!.Value : 0.5;
            double tv = req.TvLambda ?? 0.002;
            bool mask = req.SupportMask ?? true;
            bool field = req.Field ?? false;
            int grid = req.Grid is >= 2 and <= 8 ? req.Grid!.Value : 3;
            bool noiseAdaptive = req.NoiseAdaptive ?? false;

            var results = new List<object>();
            var failures = new List<object>();
            foreach (var path in req.Paths) {
                try {
                    // RL is CPU-heavy; keep the request thread free.
                    var r = await Task.Run(() =>
                        svc.RichardsonLucy(path, strength, tv, mask, field, grid, noiseAdaptive));
                    results.Add(new {
                        sourcePath = path,
                        outputPath = r.OutputPath,
                        width = r.Width,
                        height = r.Height,
                        channels = r.Channels,
                        fwhmPx = r.FwhmPx,
                        eccentricity = r.Eccentricity,
                        starsUsed = r.StarsUsed,
                        iterations = r.Iterations,
                        field = r.Field,
                        gridCells = r.GridCells,
                        measuredCells = r.MeasuredCells
                    });
                } catch (Exception ex) {
                    failures.Add(new { sourcePath = path, error = ex.Message });
                }
            }

            if (results.Count > 0) {
                try { await library.RescanAsync(); } catch { /* best-effort */ }
            }
            return Results.Ok(new { results, failures });
        });
    }

    public record DeconRequest(
        string[] Paths, double? Strength = null,
        double? TvLambda = null, bool? SupportMask = null,
        bool? Field = null, int? Grid = null, bool? NoiseAdaptive = null);
}
