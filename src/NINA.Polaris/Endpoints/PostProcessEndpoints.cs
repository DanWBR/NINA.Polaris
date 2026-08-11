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

using NINA.Image.ImageAnalysis;
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

        // Wavelet sharpen / denoise (à-trous multiscale, luminance).
        g.MapPost("/wavelet-sharpen", async (
                WaveletService svc,
                FrameLibraryService library,
                WaveletSharpenRequest req) => {
            if (req.Paths == null || req.Paths.Length == 0)
                return Results.BadRequest(new { error = "paths is required" });
            var results = new List<object>();
            var failures = new List<object>();
            foreach (var path in req.Paths) {
                try {
                    var r = svc.Sharpen(path, req.Detail ?? 0.5, req.Denoise ?? 0.0, req.Scales ?? 5);
                    results.Add(new { sourcePath = path, outputPath = r.OutputPath,
                        width = r.Width, height = r.Height, channels = r.Channels });
                } catch (Exception ex) { failures.Add(new { sourcePath = path, error = ex.Message }); }
            }
            if (results.Count > 0) { try { await library.RescanAsync(); } catch { } }
            return Results.Ok(new { results, failures });
        });

        // WAVE-2: per-layer wavelets, the way RegiStax and AstroSurface present
        // them. One gain per scale beats one Detail knob for planetary work,
        // where the layer that carries the festoons is not the layer that
        // carries the seeing noise.
        g.MapPost("/wavelet-layers", async (
                WaveletService svc,
                FrameLibraryService library,
                WaveletLayersRequest req) => {
            if (req.Paths == null || req.Paths.Length == 0)
                return Results.BadRequest(new { error = "paths is required" });
            if (req.Gains == null || req.Gains.Length == 0)
                return Results.BadRequest(new { error = "gains is required (one value per scale)" });
            var results = new List<object>();
            var failures = new List<object>();
            foreach (var path in req.Paths) {
                try {
                    var r = svc.SharpenLayers(path, req.Gains, req.Denoise);
                    results.Add(new { sourcePath = path, outputPath = r.OutputPath,
                        width = r.Width, height = r.Height, channels = r.Channels });
                } catch (Exception ex) { failures.Add(new { sourcePath = path, error = ex.Message }); }
            }
            if (results.Count > 0) { try { await library.RescanAsync(); } catch { } }
            return Results.Ok(new { results, failures });
        });

        // WAVE-3: preview for the slider panel. Returns a JPEG, writes nothing.
        // Capped at 1600 px because the point is a fast answer, not a master.
        g.MapPost("/wavelet-preview", (WaveletService svc, WaveletPreviewRequest req) => {
            if (string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new { error = "path is required" });
            try {
                var jpeg = svc.PreviewLayers(req.Path, req.Gains ?? Array.Empty<double>(),
                    req.Denoise, Math.Clamp(req.MaxDim ?? 900, 128, 1600),
                    Math.Clamp(req.Quality ?? 85, 40, 95));
                return Results.File(jpeg, "image/jpeg");
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // DUST-1: dust-mote removal preview. Detects the soft circular shadows a
        // dust speck casts and divides them out with a local synthetic flat,
        // then returns before/after JPEGs (same stretch) plus the mote geometry
        // so the modal can circle what it found. Writes nothing.
        g.MapPost("/dust-preview", (DustRemovalService svc, DustPreviewRequest req) => {
            if (string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new { error = "path is required" });
            try {
                var p = new DustMoteRemoval.Params(
                    req.Sensitivity ?? 0.6, req.MinSize ?? 2.0,
                    req.Feather ?? 2.5, req.Strength ?? 100.0);
                var pv = svc.Preview(req.Path, p,
                    Math.Clamp(req.MaxDim ?? 1024, 256, 1600),
                    Math.Clamp(req.Quality ?? 85, 40, 95));
                var motes = new object[pv.Motes.Count];
                for (int i = 0; i < pv.Motes.Count; i++) {
                    var m = pv.Motes[i];
                    motes[i] = new { x = m.X, y = m.Y, r = m.R };
                }
                return Results.Ok(new {
                    jpeg = pv.Jpeg, original = pv.Original,
                    pw = pv.Width, ph = pv.Height, count = pv.Count, motes
                });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // DUST-2: apply at full resolution, write `{stem}_dustfix.fits`.
        g.MapPost("/dust-remove", async (
                DustRemovalService svc,
                FrameLibraryService library,
                DustRemoveRequest req) => {
            if (req.Paths == null || req.Paths.Length == 0)
                return Results.BadRequest(new { error = "paths is required" });
            var p = new DustMoteRemoval.Params(
                req.Sensitivity ?? 0.6, req.MinSize ?? 2.0,
                req.Feather ?? 2.5, req.Strength ?? 100.0);
            var results = new List<object>();
            var failures = new List<object>();
            foreach (var path in req.Paths) {
                try {
                    var r = svc.Remove(path, p);
                    results.Add(new { sourcePath = path, outputPath = r.OutputPath,
                        width = r.Width, height = r.Height, channels = r.Channels, count = r.Count });
                } catch (Exception ex) { failures.Add(new { sourcePath = path, error = ex.Message }); }
            }
            if (results.Count > 0) { try { await library.RescanAsync(); } catch { } }
            return Results.Ok(new { results, failures });
        });

        // Multiscale HDR: recover blown cores (à-trous, luminance).
        g.MapPost("/wavescale-hdr", async (
                WaveletService svc,
                FrameLibraryService library,
                WaveScaleHdrRequest req) => {
            if (req.Paths == null || req.Paths.Length == 0)
                return Results.BadRequest(new { error = "paths is required" });
            var results = new List<object>();
            var failures = new List<object>();
            foreach (var path in req.Paths) {
                try {
                    var r = svc.Hdr(path, req.Amount ?? 0.5, req.Scales ?? 6);
                    results.Add(new { sourcePath = path, outputPath = r.OutputPath,
                        width = r.Width, height = r.Height, channels = r.Channels });
                } catch (Exception ex) { failures.Add(new { sourcePath = path, error = ex.Message }); }
            }
            if (results.Count > 0) { try { await library.RescanAsync(); } catch { } }
            return Results.Ok(new { results, failures });
        });

        // CLAHE — local contrast (best after a stretch).
        g.MapPost("/clahe", async (
                TonalService svc,
                FrameLibraryService library,
                ClaheRequest req) => {
            if (req.Paths == null || req.Paths.Length == 0)
                return Results.BadRequest(new { error = "paths is required" });
            var results = new List<object>();
            var failures = new List<object>();
            foreach (var path in req.Paths) {
                try {
                    var r = svc.Clahe(path, req.ClipLimit ?? 2.0, req.Tiles ?? 8);
                    results.Add(new { sourcePath = path, outputPath = r.OutputPath,
                        width = r.Width, height = r.Height, channels = r.Channels });
                } catch (Exception ex) { failures.Add(new { sourcePath = path, error = ex.Message }); }
            }
            if (results.Count > 0) { try { await library.RescanAsync(); } catch { } }
            return Results.Ok(new { results, failures });
        });

        // Highlight recovery — soft-knee compression of blown cores.
        g.MapPost("/highlight-recovery", async (
                TonalService svc,
                FrameLibraryService library,
                HighlightRecoveryRequest req) => {
            if (req.Paths == null || req.Paths.Length == 0)
                return Results.BadRequest(new { error = "paths is required" });
            var results = new List<object>();
            var failures = new List<object>();
            foreach (var path in req.Paths) {
                try {
                    var r = svc.HighlightRecovery(path, req.Knee ?? 0.6, req.Strength ?? 0.5);
                    results.Add(new { sourcePath = path, outputPath = r.OutputPath,
                        width = r.Width, height = r.Height, channels = r.Channels });
                } catch (Exception ex) { failures.Add(new { sourcePath = path, error = ex.Message }); }
            }
            if (results.Count > 0) { try { await library.RescanAsync(); } catch { } }
            return Results.Ok(new { results, failures });
        });

        // Morphological star reduction (shrink / dim stars).
        g.MapPost("/star-reduce", async (
                StarReductionService svc,
                FrameLibraryService library,
                StarReduceRequest req) => {
            if (req.Paths == null || req.Paths.Length == 0)
                return Results.BadRequest(new { error = "paths is required" });

            var results = new List<object>();
            var failures = new List<object>();
            foreach (var path in req.Paths) {
                try {
                    var r = svc.RunFits(path, req.Amount ?? 0.5, req.Size ?? 2, req.ProtectCore ?? true);
                    results.Add(new {
                        sourcePath = path,
                        outputPath = r.OutputPath,
                        width = r.Width, height = r.Height, channels = r.Channels,
                        starsReduced = r.StarsReduced
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

    // amount 0..1 = strength; size = erosion radius (px); protectCore keeps
    // bright star cores.
    public record StarReduceRequest(
        string[] Paths, double? Amount = null, int? Size = null, bool? ProtectCore = null);

    // detail 0..1 = fine-detail boost; denoise 0..1 = threshold finest scales;
    // scales = à-trous levels.
    public record WaveletSharpenRequest(
        string[] Paths, double? Detail = null, double? Denoise = null, int? Scales = null);

    /// <summary>WAVE-2: one gain per wavelet scale, FINEST FIRST. 1.0 leaves a
    /// layer alone, above sharpens, below softens. Denoise is optional, one
    /// value per scale, in units of that layer's own noise sigma.</summary>
    public record WaveletLayersRequest(
        string[] Paths, double[] Gains, double[]? Denoise = null);

    /// <summary>WAVE-3: the same numbers on ONE file, rendered as a JPEG at
    /// preview size so a slider drag stays responsive on a big master.</summary>
    public record WaveletPreviewRequest(
        string Path, double[] Gains, double[]? Denoise = null,
        int? MaxDim = null, int? Quality = null);

    // amount 0..1 = core compression strength; scales = à-trous levels.
    public record WaveScaleHdrRequest(
        string[] Paths, double? Amount = null, int? Scales = null);

    /// <summary>DUST-1: dust-mote removal preview on ONE file. Sizes are a
    /// PERCENT OF THE LONG SIDE. Sensitivity = dip depth that counts as a mote
    /// (%); MinSize = smallest mote radius kept; Feather = correction edge
    /// softness; Strength 0..100 = how much of the S/B correction to apply.</summary>
    public record DustPreviewRequest(
        string Path, double? Sensitivity = null, double? MinSize = null,
        double? Feather = null, double? Strength = null,
        int? MaxDim = null, int? Quality = null);

    /// <summary>DUST-2: the same knobs applied at full resolution to one or more
    /// files, each written as `{stem}_dustfix.fits`.</summary>
    public record DustRemoveRequest(
        string[] Paths, double? Sensitivity = null, double? MinSize = null,
        double? Feather = null, double? Strength = null);

    // clipLimit ≥1 caps per-bin count (2-4 typical); tiles = grid per axis.
    public record ClaheRequest(
        string[] Paths, double? ClipLimit = null, int? Tiles = null);

    // knee 0..1 = where compression starts; strength 0..1 = pull-down amount.
    public record HighlightRecoveryRequest(
        string[] Paths, double? Knee = null, double? Strength = null);
}
