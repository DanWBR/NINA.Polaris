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

using NINA.Polaris.Services.PostProcess;
using NINA.Polaris.Services.Studio;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// Classical (non-AI) post-processing filters ported from Siril 1.4.4,
/// exposed as plain path-in/path-out FITS ops the same way CropEndpoints /
/// DeconEndpoints work: POST a body naming one or more source files + the
/// filter params, get back `{ results:[{sourcePath, outputPath, ...}],
/// failures }`, and the frame library is rescanned so the sibling FITS show
/// up in STUDIO without a manual refresh.
///
/// These are the server-side halves of the Auto Workflow "Tools" steps
/// (scnr, ...) and can also be driven directly from the Files toolbar.
/// Synchronous on the wire — each is a single buffer transform + FITS write.
/// </summary>
public static class PostProcessEndpoints {
    public static void MapPostProcessEndpoints(this IEndpointRouteBuilder app) {
        var g = app.MapGroup("/api/post");

        // SCNR — green-cast removal on RGB. Mono is a passthrough no-op.
        g.MapPost("/scnr", async (
                ScnrService svc,
                FrameLibraryService library,
                ScnrRequest req) => {
            if (req.Paths == null || req.Paths.Length == 0)
                return Results.BadRequest(new { error = "paths is required" });

            var results = new List<object>();
            var failures = new List<object>();
            foreach (var path in req.Paths) {
                try {
                    var r = svc.RunFits(path, req.Mode ?? "average-neutral",
                        req.Amount ?? 1.0, req.PreserveLightness ?? false);
                    results.Add(new {
                        sourcePath = path,
                        outputPath = r.OutputPath,
                        width = r.Width,
                        height = r.Height,
                        channels = r.Channels,
                        pixelsChanged = r.PixelsChanged
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

        // GHS / asinh stretch (linear -> stretched, linked across channels).
        g.MapPost("/stretch", async (
                StretchService svc,
                FrameLibraryService library,
                StretchRequest req) => {
            if (req.Paths == null || req.Paths.Length == 0)
                return Results.BadRequest(new { error = "paths is required" });

            var results = new List<object>();
            var failures = new List<object>();
            foreach (var path in req.Paths) {
                try {
                    var r = svc.RunFits(path, req.Mode ?? "ghs",
                        req.D ?? 1.0, req.B ?? 0.0,
                        req.Lp ?? 0.0, req.Sp ?? 0.0, req.Hp ?? 1.0, req.Bp ?? 0.0,
                        req.Auto ?? false, req.TargetBackground ?? 0.25);
                    results.Add(new {
                        sourcePath = path,
                        outputPath = r.OutputPath,
                        width = r.Width,
                        height = r.Height,
                        channels = r.Channels,
                        appliedD = r.AppliedD
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

        // Cosmetic correction (hot / cold pixel removal).
        g.MapPost("/cosmetic", async (
                CosmeticService svc,
                FrameLibraryService library,
                CosmeticRequest req) => {
            if (req.Paths == null || req.Paths.Length == 0)
                return Results.BadRequest(new { error = "paths is required" });

            var results = new List<object>();
            var failures = new List<object>();
            foreach (var path in req.Paths) {
                try {
                    var r = svc.RunFits(path, req.SigmaCold ?? 5.0, req.SigmaHot ?? 3.0,
                        req.Amount ?? 1.0, req.Cfa ?? false);
                    results.Add(new {
                        sourcePath = path,
                        outputPath = r.OutputPath,
                        width = r.Width, height = r.Height, channels = r.Channels,
                        cold = r.Cold, hot = r.Hot
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

    // mode: average-neutral | maximum-neutral | maximum-mask | additive-mask
    // amount: 0..1 blend strength (masked modes); preserveLightness keeps
    // the pixel's Rec.709 luminance so only hue shifts.
    public record ScnrRequest(
        string[] Paths, string? Mode = null,
        double? Amount = null, bool? PreserveLightness = null);

    // mode: ghs | asinh. D = stretch amount, B = intensity/character (ghs),
    // SP/LP/HP = symmetry / shadow-protect / highlight-protect points,
    // BP = black point. Auto estimates D from the median toward TargetBackground.
    public record StretchRequest(
        string[] Paths, string? Mode = null,
        double? D = null, double? B = null,
        double? Lp = null, double? Sp = null, double? Hp = null, double? Bp = null,
        bool? Auto = null, double? TargetBackground = null);

    // sigmaCold / sigmaHot in units of the channel average deviation
    // (-1 disables that side). cfa samples same-Bayer neighbours for
    // undebayered OSC frames.
    public record CosmeticRequest(
        string[] Paths, double? SigmaCold = null, double? SigmaHot = null,
        double? Amount = null, bool? Cfa = null);
}
