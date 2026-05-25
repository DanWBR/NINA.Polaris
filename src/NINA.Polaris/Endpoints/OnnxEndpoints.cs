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
            return Results.Ok(new {
                modelsPath = reg.LastScannedPath(),
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

        // ─── save inference result ───────────────────────────────────
        // Browser POSTs the post-inference pixels back; server writes
        // a sibling FITS next to the source. multipart/form-data with:
        //   - source  : original FITS path on disk (string field)
        //   - suffix  : "_bge" / "_denoise" / "_decon" (string field)
        //   - width   : output width (string field, int)
        //   - height  : output height (string field, int)
        //   - channels: 1 or 3 (string field, int)
        //   - pixels  : raw uint16 LE bytes (file part)
        // Returns { path: "..." } of the written file.
        //
        // Implementation note: GX-1a stops at the endpoint scaffold;
        // the actual FITS-write path (with header preservation) lands
        // in GX-2 alongside the first pipeline (BgePipeline) that
        // exercises it end-to-end. For now this returns 501 so the
        // contract is reachable but explicitly unimplemented.
        g.MapPost("/save", () => Results.StatusCode(StatusCodes.Status501NotImplemented))
         .DisableAntiforgery();
    }
}
