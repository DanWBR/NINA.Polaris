using NINA.Image.Editor;
using NINA.Polaris.Services.Editor;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// HTTP surface for the STUDIO Lightroom-style editor.
///
/// Session lifecycle: client POSTs /load with a source path → gets a
/// session id → POSTs many /preview + /histogram requests as the user
/// drags sliders → optionally POSTs /sidecar to persist the edit set →
/// POSTs /export to write a final file → optionally POSTs /release to
/// free the session (or just lets it idle out after 30 min).
/// </summary>
public static class EditorEndpoints {
    public static void MapEditorEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/editor");

        // ─── load ────────────────────────────────────────────────────
        g.MapPost("/load", async (ImageEditService svc, EditSidecarStore sidecar,
                                   LoadRequest req, CancellationToken ct) => {
            if (string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new { error = "Path is required." });
            var info = await svc.LoadAsync(req.Path, ct);
            if (info == null)
                return Results.BadRequest(new { error = "Failed to load source." });

            // Hydrate the sidecar so the client can seed sliders to the
            // previously saved values.
            var savedEdits = sidecar.Load(req.Path);
            return Results.Ok(new {
                sessionId = info.SessionId,
                sourcePath = info.SourcePath,
                width = info.Width,
                height = info.Height,
                channels = info.Channels,
                edits = savedEdits   // null when no sidecar exists yet
            });
        });

        // ─── preview ─────────────────────────────────────────────────
        g.MapPost("/preview", async (ImageEditService svc, PreviewRequest req,
                                       CancellationToken ct) => {
            var bytes = await svc.RenderPreviewAsync(req.SessionId,
                req.Edits ?? EditParams.Defaults, req.MaxDim, req.Quality, ct);
            return bytes == null
                ? Results.NotFound(new { error = "Session not found." })
                : Results.File(bytes, "image/jpeg");
        });

        // ─── histogram ───────────────────────────────────────────────
        g.MapPost("/histogram", async (ImageEditService svc, PreviewRequest req,
                                         CancellationToken ct) => {
            var hist = await svc.ComputeHistogramAsync(req.SessionId,
                req.Edits ?? EditParams.Defaults, ct);
            return hist == null
                ? Results.NotFound(new { error = "Session not found." })
                : Results.Ok(hist);
        });

        // ─── export ──────────────────────────────────────────────────
        g.MapPost("/export", async (ImageEditService svc, Services.Studio.FrameLibraryService library,
                                      ExportRequestDto req, CancellationToken ct) => {
            var fmt = (req.Format ?? "jpg").ToLowerInvariant();
            var quality = Math.Clamp(req.Quality ?? 92, 1, 100);
            var path = await svc.ExportAsync(new ExportRequest(
                SessionId: req.SessionId,
                Edits: req.Edits ?? EditParams.Defaults,
                Format: fmt,
                Quality: quality,
                TargetWidth: req.TargetWidth,
                TargetHeight: req.TargetHeight,
                OutputPath: req.OutputPath), ct);
            if (path == null)
                return Results.BadRequest(new { error = "Export failed." });

            // Best-effort: tell the frame library a new file landed so the
            // FILES tab picks it up without a manual rescan.
            try { _ = Task.Run(() => library.RescanAsync()); }
            catch { /* non-fatal */ }

            return Results.Ok(new { path });
        });

        // ─── sidecar GET ─────────────────────────────────────────────
        // path comes via query string so it's friendly for browser-side
        // fetch() without having to build a body.
        g.MapGet("/sidecar", (EditSidecarStore sidecar, string path) => {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path query string is required." });
            var edits = sidecar.Load(path);
            return edits == null
                ? Results.Ok(new { exists = false })
                : Results.Ok(new { exists = true, edits });
        });

        // ─── sidecar PUT ─────────────────────────────────────────────
        g.MapPut("/sidecar", (EditSidecarStore sidecar, SidecarRequest req) => {
            if (string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new { error = "Path is required." });
            var saved = sidecar.Save(req.Path, req.Edits ?? EditParams.Defaults);
            return saved == null
                ? Results.Problem("Failed to write sidecar.")
                : Results.Ok(new { sidecarPath = saved });
        });

        // ─── upload (multipart) ──────────────────────────────────────
        // Saves the upload to {AppData}/Polaris/uploads/{guid}/{filename}
        // and returns the path so the client can immediately POST /load
        // with it.
        g.MapPost("/upload", async (HttpRequest http, ILogger<ImageEditService> log,
                                      CancellationToken ct) => {
            if (!http.HasFormContentType)
                return Results.BadRequest(new { error = "Multipart form expected." });
            var form = await http.ReadFormAsync(ct);
            if (form.Files.Count == 0)
                return Results.BadRequest(new { error = "No file uploaded." });
            var file = form.Files[0];
            if (file.Length <= 0)
                return Results.BadRequest(new { error = "Empty file." });

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var uploadDir = Path.Combine(appData, "Polaris", "uploads", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(uploadDir);
            var safeName = string.Join('_', file.FileName.Split(Path.GetInvalidFileNameChars()));
            var outPath = Path.Combine(uploadDir, safeName);
            await using (var fs = File.Create(outPath)) {
                await file.CopyToAsync(fs, ct);
            }
            log.LogInformation("Editor upload: {Name} -> {Path} ({Bytes} bytes)",
                file.FileName, outPath, file.Length);
            return Results.Ok(new { path = outPath });
        }).DisableAntiforgery();

        // ─── release ─────────────────────────────────────────────────
        g.MapPost("/release", (ImageEditService svc, ReleaseRequest req) => {
            svc.Release(req.SessionId);
            return Results.Ok(new { released = true });
        });

        // ─── sessions list (debugging) ───────────────────────────────
        g.MapGet("/sessions", (ImageEditService svc) => Results.Ok(svc.ActiveSessions()));
    }

    public record LoadRequest(string Path);
    public record PreviewRequest(string SessionId, EditParams? Edits,
                                  int MaxDim = 1600, int Quality = 85);
    public record ExportRequestDto(string SessionId, EditParams? Edits,
                                    string? Format, int? Quality,
                                    int? TargetWidth, int? TargetHeight,
                                    string? OutputPath);
    public record SidecarRequest(string Path, EditParams? Edits);
    public record ReleaseRequest(string SessionId);
}
