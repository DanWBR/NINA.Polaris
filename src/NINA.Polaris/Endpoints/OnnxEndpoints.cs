using NINA.Polaris.Services.Onnx;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// HTTP surface for the server-as-ONNX-model-CDN used by GX-1..8. The
/// browser fetches the manifest, picks a model, GETs its bytes (with
/// ETag-based conditional re-validation so reload doesn't re-download),
/// caches in IndexedDB by hash, and runs inference via onnxruntime-web.
/// Inference output is POSTed back to /save which writes a sibling FITS
/// next to the source.
/// </summary>
public static class OnnxEndpoints {
    public static void MapOnnxEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/onnx");

        // ─── manifest ────────────────────────────────────────────────
        // Lists every model the server can serve, with size + hash.
        // Hash is computed lazily here (first request pays the SHA-256
        // pass on each model; subsequent requests hit the cache).
        g.MapGet("/manifest", async (OnnxModelRegistry reg, CancellationToken ct) => {
            var items = new List<object>();
            foreach (var m in reg.All()) {
                var hash = await reg.GetHashAsync(m.Family, m.Version, ct);
                items.Add(new {
                    family = m.Family,
                    version = m.Version,
                    sizeBytes = m.SizeBytes,
                    hash,
                });
            }
            // GX-12j: surface enough state for the Settings page to show
            // a useful banner — "using bundled models from wwwroot" vs
            // "using configured Onnx:ModelsPath" vs "no models found
            // anywhere, drop them at <bundled>/graxpert/models".
            var scanned = reg.LastScannedPath();
            var bundled = reg.BundledModelsPath;
            var usingBundled = !string.IsNullOrEmpty(scanned)
                && string.Equals(Path.GetFullPath(scanned),
                                  Path.GetFullPath(bundled),
                                  StringComparison.OrdinalIgnoreCase);
            return Results.Ok(new {
                modelsPath = scanned,
                bundledPath = bundled,
                usingBundled,
                models = items,
            });
        });

        // ─── re-scan ─────────────────────────────────────────────────
        // Called after the user changes OnnxModelsPath in Settings.
        g.MapPost("/rescan", async (OnnxModelRegistry reg, CancellationToken ct) => {
            await reg.RescanAsync(ct);
            return Results.Ok(new { count = reg.All().Count });
        });

        // ─── serve model bytes ───────────────────────────────────────
        // ETag-based conditional GET. The browser sends If-None-Match
        // on the second + Nth load; we return 304 when the hash matches.
        // Cache-Control: immutable means once the hash matches the
        // browser won't even re-validate within the year.
        g.MapGet("/model/{family}/{version}",
            async (OnnxModelRegistry reg, HttpContext ctx,
                   string family, string version, CancellationToken ct) => {
                var entry = reg.Find(family, version);
                if (entry == null) return Results.NotFound(new { error = "Model not found." });

                var hash = await reg.GetHashAsync(family, version, ct);
                if (hash == null) return Results.Problem("Hash compute failed.");
                var etag = "\"" + hash + "\"";

                // Conditional GET — browser cache hit.
                var ifNoneMatch = ctx.Request.Headers.IfNoneMatch.ToString();
                if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch.Contains(etag)) {
                    ctx.Response.Headers.ETag = etag;
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }

                ctx.Response.Headers.ETag = etag;
                ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                ctx.Response.Headers["X-Onnx-Family"] = family;
                ctx.Response.Headers["X-Onnx-Version"] = version;
                ctx.Response.Headers["Access-Control-Expose-Headers"] =
                    "ETag, X-Onnx-Family, X-Onnx-Version";

                return Results.File(entry.Path, "application/octet-stream", enableRangeProcessing: true);
            });

        // ─── source pixels (GX-2) ────────────────────────────────────
        // Decode a FITS file to raw uint16 LE bytes and stream them to
        // the browser. The browser feeds these into the ONNX pipeline's
        // normalization step; pipelines do their own MAD / log normalize
        // before passing into the model.
        g.MapGet("/source-pixels", async (OnnxFileService files, HttpContext ctx,
                                            string path, CancellationToken ct) => {
            var raw = await files.LoadRawAsync(path, ct);
            if (raw == null) return Results.NotFound(new { error = "Failed to load source." });
            ctx.Response.Headers["X-Width"]    = raw.Width.ToString();
            ctx.Response.Headers["X-Height"]   = raw.Height.ToString();
            ctx.Response.Headers["X-Channels"] = raw.Channels.ToString();
            ctx.Response.Headers["X-BitDepth"] = raw.BitDepth.ToString();
            ctx.Response.Headers["Access-Control-Expose-Headers"] =
                "X-Width, X-Height, X-Channels, X-BitDepth";
            return Results.File(raw.PixelsLE16, "application/octet-stream");
        });

        // ─── save inference result (GX-2) ────────────────────────────
        // Browser POSTs back the post-inference uint16 pixels; server
        // writes a sibling FITS next to the source. multipart/form-data
        // with fields: source, suffix, width, height, channels + file
        // part `pixels` (raw uint16 LE bytes). Returns { path }.
        g.MapPost("/save", async (HttpRequest http, OnnxFileService files,
                                    NINA.Polaris.Services.Studio.FrameLibraryService library,
                                    CancellationToken ct) => {
            if (!http.HasFormContentType)
                return Results.BadRequest(new { error = "Multipart form expected." });
            var form = await http.ReadFormAsync(ct);

            string? source   = form["source"];
            string? suffix   = form["suffix"];
            int width        = int.TryParse(form["width"],    out var w) ? w  : 0;
            int height       = int.TryParse(form["height"],   out var h) ? h  : 0;
            int channels     = int.TryParse(form["channels"], out var c) ? c  : 1;

            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(suffix)
                || width <= 0 || height <= 0) {
                return Results.BadRequest(new { error = "Missing or invalid form fields." });
            }
            if (form.Files.Count == 0 || form.Files[0].Length == 0) {
                return Results.BadRequest(new { error = "Missing 'pixels' file part." });
            }

            byte[] bytes;
            using (var ms = new MemoryStream()) {
                await form.Files[0].CopyToAsync(ms, ct);
                bytes = ms.ToArray();
            }

            var outPath = await files.SaveSiblingAsync(
                source, suffix, bytes, width, height, channels, ct);
            if (outPath == null) return Results.Problem("Save failed.");

            // Re-index the frame library so the new sibling FITS shows
            // up in STUDIO / FILES without a manual rescan. Same hook
            // the editor's export endpoint uses.
            try { _ = Task.Run(() => library.RescanAsync()); }
            catch { /* non-fatal */ }

            return Results.Ok(new { path = outPath });
        }).DisableAntiforgery();
    }
}
