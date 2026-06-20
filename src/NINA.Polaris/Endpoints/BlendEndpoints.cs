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

using NINA.Polaris.Services.Editor;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// HTTP surface for the "Image Blend" tool (the in-app equivalent of
/// PixInsight's ImageBlend script): recombine a base image (e.g. a stretched
/// starless) with a blend image (e.g. the stars-only image) using independent
/// per-image MTF stretch + a blend mode + opacity.
///
/// Session lifecycle mirrors the editor: POST /load with two paths → session id
/// → many /preview requests as the user drags sliders → /render to write the
/// final 16-bit FITS → /release (or idle out after 30 min).
/// </summary>
public static class BlendEndpoints {
    public static void MapBlendEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/blend");

        // ─── load ────────────────────────────────────────────────────
        g.MapPost("/load", async (ImageBlendService svc, BlendLoadRequest req,
                                   CancellationToken ct) => {
            if (string.IsNullOrWhiteSpace(req.BasePath) || string.IsNullOrWhiteSpace(req.BlendPath))
                return Results.BadRequest(new { error = "basePath and blendPath are required." });
            var info = await svc.LoadAsync(req.BasePath, req.BlendPath, ct);
            return info == null
                ? Results.BadRequest(new { error = "Failed to load image pair (missing file or geometry mismatch)." })
                : Results.Ok(new {
                    sessionId = info.SessionId,
                    basePath = info.BasePath,
                    blendPath = info.BlendPath,
                    width = info.Width,
                    height = info.Height,
                    channels = info.Channels
                });
        });

        // ─── preview ─────────────────────────────────────────────────
        g.MapPost("/preview", async (ImageBlendService svc, BlendRenderRequest req,
                                      CancellationToken ct) => {
            var bytes = await svc.RenderPreviewAsync(req.SessionId, ToParams(req),
                req.MaxDim ?? 1600, req.Quality ?? 85, ct);
            return bytes == null
                ? Results.NotFound(new { error = "Session not found." })
                : Results.File(bytes, "image/jpeg");
        });

        // ─── render (final 16-bit FITS) ──────────────────────────────
        g.MapPost("/render", async (ImageBlendService svc,
                                     Services.Studio.FrameLibraryService library,
                                     BlendRenderRequest req, CancellationToken ct) => {
            var path = await svc.RenderAsync(req.SessionId, ToParams(req), req.OutputPath, ct);
            if (path == null)
                return Results.BadRequest(new { error = "Render failed (session not found?)." });
            // Best-effort: surface the new file in the FILES tab without a manual rescan.
            try { _ = Task.Run(() => library.RescanAsync()); } catch { /* non-fatal */ }
            return Results.Ok(new { path });
        });

        // ─── release ─────────────────────────────────────────────────
        g.MapPost("/release", (ImageBlendService svc, BlendReleaseRequest req) => {
            svc.Release(req.SessionId);
            return Results.Ok(new { released = true });
        });
    }

    private static ImageBlendService.BlendParams ToParams(BlendRenderRequest r) => new(
        BaseBlack: r.BaseBlack, BaseMid: r.BaseMid, BaseWhite: r.BaseWhite,
        BlendBlack: r.BlendBlack, BlendMid: r.BlendMid, BlendWhite: r.BlendWhite,
        Mode: r.Mode ?? "screen", Opacity: r.Opacity);

    public record BlendLoadRequest(string BasePath, string BlendPath);

    public record BlendRenderRequest(
        string SessionId,
        double BaseBlack = 0.0, double BaseMid = 0.5, double BaseWhite = 1.0,
        double BlendBlack = 0.0, double BlendMid = 0.5, double BlendWhite = 1.0,
        string? Mode = "screen", double Opacity = 1.0,
        int? MaxDim = 1600, int? Quality = 85, string? OutputPath = null);

    public record BlendReleaseRequest(string SessionId);
}
