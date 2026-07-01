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
            bool protectStars = req.ProtectStars ?? true;

            var results = new List<object>();
            var failures = new List<object>();
            foreach (var path in req.Paths) {
                try {
                    // RL is CPU-heavy; keep the request thread free.
                    var r = await Task.Run(() =>
                        svc.RichardsonLucy(path, strength, tv, mask, field, grid, noiseAdaptive, protectStars));
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

        // POST /api/decon/rl-prepare — measure PSF + noise model + star list for
        // a single frame so the browser can run the RL iteration loop locally
        // (keeping the SBC server CPU free during the heavy iteration phase).
        // Only global-PSF mode is supported (field mode stays server-side).
        g.MapPost("/rl-prepare", async (
                DeconvolutionService svc,
                RlPrepareRequest req) => {
            if (string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new { error = "path is required" });
            double strength = req.Strength is >= 0 and <= 1 ? req.Strength!.Value : 0.5;
            bool protectStars = req.ProtectStars ?? true;
            try {
                var r = await Task.Run(() =>
                    svc.PrepareForBrowserRl(req.Path, strength, protectStars));
                return Results.Ok(new {
                    width = r.Width, height = r.Height, channels = r.Channels,
                    fwhmPx = r.FwhmPx, eccentricity = r.Eccentricity,
                    starsUsed = r.StarsUsed, iterations = r.Iterations,
                    kernelSize = r.KernelSize, kernelBase64 = r.KernelBase64,
                    noiseA = r.NoiseA, noiseB = r.NoiseB, background = r.Background,
                    dampT = 2.5,
                    protectStars,
                    stars = r.Stars.Select(s => new { x = s.X, y = s.Y, r = s.R })
                });
            } catch (InvalidOperationException ex) {
                return Results.UnprocessableEntity(new { error = ex.Message });
            } catch (FileNotFoundException) {
                return Results.NotFound(new { error = "Source FITS not found" });
            }
        });

        // POST /api/decon/measure-fwhm — measure the median star FWHM (px) of a
        // frame so the decon / detail modal can auto-fill the "Image FWHM" field.
        g.MapPost("/measure-fwhm", async (DeconvolutionService svc, MeasureFwhmRequest req) => {
            if (string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new { error = "path is required" });
            try {
                var r = await Task.Run(() => svc.MeasureFwhm(req.Path));
                return Results.Ok(new {
                    width = r.Width, height = r.Height, channels = r.Channels,
                    fwhmPx = r.FwhmPx, eccentricity = r.Eccentricity, starsUsed = r.StarsUsed
                });
            } catch (InvalidOperationException ex) {
                return Results.UnprocessableEntity(new { error = ex.Message });
            } catch (FileNotFoundException) {
                return Results.NotFound(new { error = "Source FITS not found" });
            }
        });
    }

    public record MeasureFwhmRequest(string Path);

    public record RlPrepareRequest(string Path, double? Strength = null, bool? ProtectStars = null);

    public record DeconRequest(
        string[] Paths, double? Strength = null,
        double? TvLambda = null, bool? SupportMask = null,
        bool? Field = null, int? Grid = null, bool? NoiseAdaptive = null,
        bool? ProtectStars = null);
}
