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
/// Single endpoint: POST /api/crop/run with a body that names one or
/// more source files + a rectangular ROI in image pixel space.
/// Returns the list of output paths (one per input, all sharing the
/// same ROI). The frame library gets a rescan after the writes so the
/// new files show up in STUDIO without a manual refresh.
///
/// Synchronous on the wire (no async-job dance like GraXpert): the
/// actual work is a buffer slice + a FITS write, totalling &lt; 1 s for
/// typical masters. If batch sizes ever climb large enough to feel
/// slow, this is the obvious place to add streaming progress.
/// </summary>
public static class CropEndpoints {
    public static void MapCropEndpoints(this IEndpointRouteBuilder app) {
        var g = app.MapGroup("/api/crop");

        g.MapPost("/run", async (
                CropService svc,
                FrameLibraryService library,
                CropRequest req) => {
            if (req.Paths == null || req.Paths.Length == 0)
                return Results.BadRequest(new { error = "paths is required" });

            // Prefer the normalised (fraction) ROI when the client sent
            // one: it is resolution-independent and immune to the preview
            // being a downscaled JPEG. Fall back to the legacy absolute
            // pixel ROI for older clients / API callers.
            bool useFraction = req.FracW is > 0 && req.FracH is > 0;
            if (!useFraction && (req.Width <= 0 || req.Height <= 0))
                return Results.BadRequest(new {
                    error = "width and height (or fracW/fracH) must be positive"
                });

            var results = new List<object>();
            var failures = new List<object>();
            foreach (var path in req.Paths) {
                try {
                    var r = useFraction
                        ? svc.CropFitsFraction(path,
                            req.FracX ?? 0, req.FracY ?? 0,
                            req.FracW ?? 0, req.FracH ?? 0)
                        : svc.CropFits(path, req.X, req.Y, req.Width, req.Height);
                    results.Add(new {
                        sourcePath = path,
                        outputPath = r.OutputPath,
                        width = r.Width,
                        height = r.Height,
                        channels = r.Channels
                    });
                } catch (Exception ex) {
                    failures.Add(new { sourcePath = path, error = ex.Message });
                }
            }

            // Reindex so the new _crop.fits siblings show up in STUDIO
            // + the FILES browser without the user hitting Refresh.
            // Same pattern Studio post-processing services use after
            // writing sibling files.
            if (results.Count > 0) {
                try { await library.RescanAsync(); } catch { /* best-effort */ }
            }

            return Results.Ok(new { results, failures });
        });

        // Auto-crop: detect + remove the black/ragged stacking borders on
        // slightly-misaligned integrations. No ROI needed — the largest
        // fully-covered inner rectangle is found per file. Same response
        // shape as /run so the Auto Workflow post-runner can drive it.
        g.MapPost("/auto", async (
                CropService svc,
                FrameLibraryService library,
                AutoCropRequest req) => {
            if (req.Paths == null || req.Paths.Length == 0)
                return Results.BadRequest(new { error = "paths is required" });

            var results = new List<object>();
            var failures = new List<object>();
            foreach (var path in req.Paths) {
                try {
                    var r = svc.AutoCropFits(path, req.Threshold ?? 0, req.Margin ?? 0);
                    results.Add(new {
                        sourcePath = path,
                        outputPath = r.OutputPath,
                        width = r.Width, height = r.Height, channels = r.Channels
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

        // Auto-crop SUGGEST: run the same content-rect detection but write
        // nothing — return the ROI as normalised fractions so the crop picker
        // can pre-fill its rectangle for the user to review/adjust before
        // committing with /run. One file (the modal shows one image).
        g.MapPost("/auto-suggest", (CropService svc, AutoCropRequest req) => {
            if (req.Paths == null || req.Paths.Length == 0)
                return Results.BadRequest(new { error = "paths is required" });
            try {
                var s = svc.SuggestAutoCropFraction(
                    req.Paths[0], req.Threshold ?? 0, req.Margin ?? 0);
                return Results.Ok(new {
                    fracX = s.FracX, fracY = s.FracY, fracW = s.FracW, fracH = s.FracH,
                    x = s.X, y = s.Y, width = s.Width, height = s.Height,
                    sourceWidth = s.SourceWidth, sourceHeight = s.SourceHeight,
                    // Whole frame already covered → nothing to trim; the UI can
                    // tell the user instead of drawing a full-image rectangle.
                    full = s.Width >= s.SourceWidth && s.Height >= s.SourceHeight
                });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    // X/Y/Width/Height are legacy absolute pixel coords (kept for API
    // callers). FracX/FracY/FracW/FracH are the preferred normalised ROI
    // (0..1, top-left origin) the web picker sends so the crop is
    // independent of the downscaled preview resolution.
    public record CropRequest(
        string[] Paths, int X, int Y, int Width, int Height,
        double? FracX = null, double? FracY = null,
        double? FracW = null, double? FracH = null);

    // Threshold = per-channel level a pixel must clear to count as covered
    // (0 = exact black, what integrators write for uncovered areas). Margin =
    // extra inward shrink in px (safety against low-SNR partial edges).
    public record AutoCropRequest(
        string[] Paths, int? Threshold = null, int? Margin = null);
}