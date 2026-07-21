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

using System.Text;
using Microsoft.AspNetCore.Http;
using NINA.Core.Enum;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Studio;
using NINA.Image.FileFormat.FITS;
using SkiaSharp;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// HTTP surface for the FILES tab. The endpoints are thin: every
/// operation that touches the disk routes through
/// <see cref="FileBrowserService"/> so safety + logging stay in one
/// place. The two non-trivial bits that *do* live here are:
///   - the preview routing (FITS → JPEG via FitsThumbnailer; raster
///     passthrough; TIFF decode via Skia; text truncation)
///   - the studio-root mutator (writes through to ProfileService.Save)
/// </summary>
public static class FilesEndpoints {
    // Field-safety: bound concurrent heavy image decodes. The FILES grid
    // fires one /thumb (and /preview) per visible file at once; each FITS
    // decode loads the WHOLE frame into RAM (a 3-channel OSC colour master
    // is ~100 MB peak after the per-channel stretch). On a Raspberry Pi a
    // directory of large FITS spawned N simultaneous decodes and OOM-froze
    // the board (reported: opening FILES right after saving a live stack
    // required a hard reboot). This gate caps concurrent decodes so peak
    // memory stays bounded no matter how many thumbnails the browser asks
    // for at once; the on-disk render cache makes repeat views cheap.
    private static readonly SemaphoreSlim _renderGate = new(2, 2);

    private static async Task<T> RenderGatedAsync<T>(Func<T> render, CancellationToken ct) {
        await _renderGate.WaitAsync(ct);
        try { return await Task.Run(render, ct); }
        finally { _renderGate.Release(); }
    }

    public static void MapFilesEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/files");

        // --- Roots / list / stat -----------------------------------

        g.MapGet("/roots", (FileBrowserService svc) => Results.Ok(svc.ListRoots()));

        // Effective Studio root: the configured ImageOutputDir if it still
        // exists, otherwise a safe fallback (the user's home directory). Lets
        // the FILES / STUDIO tabs land somewhere valid when the configured
        // root was deleted, on a now-unmounted drive, or never created.
        g.MapGet("/studio-root", (FileBrowserService svc, ProfileService profiles) => {
            var configured = profiles.Active?.ImageOutputDir ?? "";
            var effective = svc.ResolveStudioRoot(configured);
            var exists = !string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured);
            return Results.Ok(new {
                configured,
                effective,
                exists,
                fellBack = !exists && !string.IsNullOrWhiteSpace(effective)
            });
        });

        g.MapGet("/list", (FileBrowserService svc,
                           NINA.Polaris.Services.Studio.FrameLibraryService lib,
                           ProfileService profiles,
                           string path, bool? hidden, bool? withMeta) => {
            try {
                var entries = svc.List(path, hidden ?? false);
                if (withMeta == true) {
                    // UNIF-4: decorate each entry with the FITS metadata cached by
                    // FrameLibraryService (IMAGETYP, FILTER, OBJECT, EXPOSURE, GAIN).
                    // The lookup is a single SQL "WHERE path IN (...)" so a 200-file
                    // listing stays under ~30ms even on the Pi. Files not indexed yet
                    // (fresh captures, non-FITS) get a null fitsMeta on their wire shape.
                    var paths = entries
                        .Where(e => !e.IsDirectory)
                        .Select(e => e.FullPath)
                        .ToList();
                    var meta = lib.BatchLookupByPath(paths);

                    // FITS files not yet in the index. We must NOT parse their headers
                    // synchronously here (that opened + read every file on the request
                    // thread and froze the listing on large / USB roots). Instead:
                    //   - inside the studio root: kick a background rescan (deduped by
                    //     the scan gate) so they get cached; they show meta next load.
                    //   - outside the root (a rescan never reaches it): read the headers
                    //     directly, but in parallel so a big folder cannot freeze the list.
                    static bool IsFits(string p) =>
                        p.EndsWith(".fit", StringComparison.OrdinalIgnoreCase) ||
                        p.EndsWith(".fits", StringComparison.OrdinalIgnoreCase);
                    var unindexed = paths.Where(p => IsFits(p) && !meta.ContainsKey(p)).ToList();

                    var extra = new System.Collections.Concurrent.ConcurrentDictionary<
                        string, NINA.Polaris.Services.Studio.FrameMeta>();
                    if (unindexed.Count > 0) {
                        var root = svc.ResolveStudioRoot(profiles.Active?.ImageOutputDir ?? "");
                        var inRoot = !string.IsNullOrEmpty(root) && IsUnder(path, root);
                        if (inRoot) {
                            _ = lib.RescanAsync();
                        } else {
                            Parallel.ForEach(unindexed,
                                new ParallelOptions {
                                    MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount)
                                },
                                p => { var d = lib.ReadMetaFromFile(p); if (d != null) extra[p] = d; });
                        }
                    }

                    var decorated = entries.Select(e => {
                        object? fm = null;
                        if (!e.IsDirectory) {
                            if (meta.TryGetValue(e.FullPath, out var m)) {
                                fm = new {
                                    imageType = m.ImageType, filter = m.Filter,
                                    target = m.Target, exposureSec = m.ExposureSec,
                                    gain = m.Gain
                                };
                            } else if (extra.TryGetValue(e.FullPath, out var d)) {
                                fm = new {
                                    imageType = d.ImageType, filter = d.Filter,
                                    target = d.Target, exposureSec = d.ExposureSec,
                                    gain = d.Gain
                                };
                            }
                        }
                        return new {
                            e.Name, e.FullPath, e.IsDirectory,
                            e.SizeBytes, e.ModifiedUtc, e.Mime,
                            e.IsHidden, e.IsReadOnly,
                            fitsMeta = fm
                        };
                    }).ToList<object>();
                    return Results.Ok(new { path, entries = decorated });
                }
                return Results.Ok(new { path, entries });
            } catch (UnauthorizedAccessException ex) {
                return Results.Json(new { error = ex.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            } catch (DirectoryNotFoundException ex) {
                return Results.NotFound(new { error = ex.Message });
            } catch (Exception ex) {
                return Results.Problem(ex.Message);
            }
        });

        g.MapGet("/stat", (FileBrowserService svc, string path) => {
            try {
                var entry = svc.Stat(path);
                return entry == null ? Results.NotFound() : Results.Ok(entry);
            } catch (UnauthorizedAccessException ex) {
                return Results.Json(new { error = ex.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            }
        });

        // --- Download (single file) --------------------------------

        // Streams the file straight from disk via FileStream so a 60MB
        // FITS doesn't sit in memory. Content-Disposition: attachment
        // tells the browser to save-as instead of trying to render.
        g.MapGet("/download", (FileBrowserService svc, string path) => {
            try {
                var stream = svc.OpenRead(path);
                var name = Path.GetFileName(path);
                var mime = FileBrowserService.GuessMime(Path.GetExtension(path));
                return Results.File(stream, mime, fileDownloadName: name);
            } catch (UnauthorizedAccessException ex) {
                return Results.Json(new { error = ex.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            } catch (FileNotFoundException ex) {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // --- Multi-download as streaming ZIP -----------------------

        // POST body: { paths: string[], rootForNames?: string }.
        // The response body is the ZIP archive itself; Kestrel writes
        // it incrementally as ZipArchive flushes entries.
        g.MapPost("/download-zip", async (FileBrowserService svc, HttpContext ctx,
                                          ZipRequest req, CancellationToken ct) => {
            if (req.Paths == null || req.Paths.Count == 0)
                return Results.BadRequest(new { error = "paths is required" });
            try {
                var fileName = (req.FileName ?? "polaris-files.zip");
                ctx.Response.ContentType = "application/zip";
                ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
                await svc.WriteZipAsync(req.Paths, ctx.Response.Body, req.RootForNames, ct);
                return Results.Empty;
            } catch (UnauthorizedAccessException ex) {
                return Results.Json(new { error = ex.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            } catch (FileNotFoundException ex) {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // --- Preview -----------------------------------------------

        // Per type: FITS → stretched JPEG via FitsThumbnailer; raster
        // formats pass through unchanged (browser decodes natively);
        // TIFF gets decoded via Skia to PNG; text gets the first
        // ~32 KB as text/plain. Unknown formats → 415.
        g.MapGet("/preview", async (HttpContext ctx, FileBrowserService svc, string path,
                                    int? maxDim, string? stretchFrom,
                                    string? bayer,
                                    CancellationToken ct) => {
            try {
                var full = svc.ResolveSafe(path, mustExist: true);
                if (!File.Exists(full))
                    return Results.NotFound(new { error = "Not a file" });
                var kind = FileBrowserService.ClassifyForPreview(full);
                var max  = maxDim ?? 1600;

                // GX-12c: optional reference path. When set, the FITS
                // stretch params are computed from THAT file's
                // histogram and applied to the requested file's pixels.
                // Used by the before/after comparator so both sides
                // share the same auto-stretch, otherwise a slightly
                // denoised sibling re-stretches with a tighter MAD
                // and the comparator shows two different colour
                // mappings instead of two states of the same scene.
                string? stretchRefFull = null;
                if (!string.IsNullOrWhiteSpace(stretchFrom)) {
                    try { stretchRefFull = svc.ResolveSafe(stretchFrom, mustExist: true); }
                    catch { /* silently ignore, fall back to self-stretch */ }
                }

                // Optional Bayer pattern override from the image-viewer
                // dropdown. When set, the preview debayers with the
                // chosen pattern instead of whatever BAYERPAT says.
                BayerPatternEnum? bayerOverride = null;
                if (!string.IsNullOrWhiteSpace(bayer)) {
                    bayerOverride = bayer.ToUpperInvariant() switch {
                        "RGGB" => BayerPatternEnum.RGGB,
                        "BGGR" => BayerPatternEnum.BGGR,
                        "GBRG" => BayerPatternEnum.GBRG,
                        "GRBG" => BayerPatternEnum.GRBG,
                        _ => null
                    };
                }

                switch (kind) {
                    case PreviewKind.Fits: {
                        // CACHE: render once per (file, mtime, size, maxDim,
                        // stretchFrom, bayer) and serve the physical file so
                        // ASP.NET answers conditional GETs with 304.
                        var key = RenderCache.KeyForFile(full, "fits", max,
                            stretchRefFull, bayer);
                        return await RenderGatedAsync(() => RenderCache.ServeCached(
                            ctx, key, "jpg", "image/jpeg",
                            () => FitsThumbnailer.RenderJpegFromPath(full,
                                    maxDim: max, quality: 90,
                                    stretchFromPath: stretchRefFull,
                                    bayerOverride: bayerOverride)), ct);
                    }
                    case PreviewKind.RasterPassthrough: {
                        // Serve the physical source file directly: validators
                        // (ETag + Last-Modified) and 304 handling for free,
                        // no cache copy needed since the source IS the bytes.
                        ctx.Response.Headers.CacheControl = "private";
                        return Results.File(full,
                            FileBrowserService.GuessMime(Path.GetExtension(full)),
                            enableRangeProcessing: true);
                    }
                    case PreviewKind.TiffDecode: {
                        var key = RenderCache.KeyForFile(full, "tiff", max);
                        try {
                            return await RenderGatedAsync(() => RenderCache.ServeCached(
                                ctx, key, "png", "image/png",
                                () => DecodeRasterToPng(full, max)
                                      ?? throw new RenderFailedException()), ct);
                        } catch (RenderFailedException) {
                            return Results.UnprocessableEntity(new { error = "TIFF decode failed" });
                        }
                    }
                    case PreviewKind.Text: {
                        var text = await ReadHeadAsync(full, maxBytes: 32 * 1024, ct);
                        return Results.Text(text, "text/plain", Encoding.UTF8);
                    }
                    default:
                        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
                }
            } catch (UnauthorizedAccessException ex) {
                return Results.Json(new { error = ex.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            } catch (FileNotFoundException ex) {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // Parsed FITS header cards as JSON, grouped into sensible
        // sections for the viewer side panel. Reads headers only
        // (skips the pixel block, 64 MB of memory and ~100 ms saved
        // per file) so opening the panel is essentially free even on
        // a Pi over a slow USB SSD.
        g.MapGet("/fits-headers", (FileBrowserService svc, string path) => {
            try {
                var full = svc.ResolveSafe(path, mustExist: true);
                if (!File.Exists(full)) return Results.NotFound();
                var ext = Path.GetExtension(full).ToLowerInvariant();
                if (ext != ".fits" && ext != ".fit" && ext != ".fts")
                    return Results.BadRequest(new { error = "Not a FITS file" });

                using var fs = File.OpenRead(full);
                var headers = FITSReader.ReadHeadersOnly(fs);

                // Project to JSON-friendly DTOs grouped by topic. The
                // grouping mirrors the categories the FITS spec uses
                // and matches how PixInsight/Siril display headers
                // (Observation / Instrument / Image / Other), so an
                // astrophotographer sees a layout that feels familiar.
                static GroupedCard Card(FITSHeaderCard c)
                    => new(c.Keyword, c.Value?.Trim() ?? "", c.Comment ?? "");
                bool In(string key, params string[] set)
                    => set.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));

                var imageKeys = new[] {
                    "SIMPLE","BITPIX","NAXIS","NAXIS1","NAXIS2","NAXIS3",
                    "BZERO","BSCALE","BAYERPAT","XBAYROFF","YBAYROFF",
                    "DATATYPE","CTYPE1","CTYPE2","CRVAL1","CRVAL2"
                };
                var observationKeys = new[] {
                    "OBJECT","OBJCTRA","OBJCTDEC","OBJCTROT","RA","DEC",
                    "DATE-OBS","DATE-AVG","MJD-OBS","EXPTIME","EXPOSURE",
                    "FILTER","IMAGETYP","NCOMBINE","EXPTOTAL","FRAMENR"
                };
                var instrumentKeys = new[] {
                    "INSTRUME","TELESCOP","OTA","FOCALLEN","FOCRATIO","APERTURE",
                    "XPIXSZ","YPIXSZ","XBINNING","YBINNING","GAIN","EGAIN",
                    "OFFSET","READOUTM","CCD-TEMP","SET-TEMP","FWHEEL",
                    "ROTATOR","ROTATANG","FOCNAME","FOCPOS","FOCTEMP","PIERSIDE"
                };
                var siteKeys = new[] {
                    "SITELAT","SITELONG","SITEELEV","SITENAME","OBSERVER",
                    "OBSERVAT","CLOUDCVR","DEWPOINT","HUMIDITY","PRESSURE",
                    "SKYBRGHT","MPSAS","AMBTEMP","WINDSPD","WINDDIR","WINDGUST"
                };
                var processingKeys = new[] {
                    "CREATOR","SWCREATE","CALSTAT","INTMETH","REJECT","BGREMOVE",
                    "NRMETHOD","NRRADIUS","SHARPEN","SHARPAMT","SHARPRAD","SHARPTHR"
                };

                var image       = new List<GroupedCard>();
                var observation = new List<GroupedCard>();
                var instrument  = new List<GroupedCard>();
                var site        = new List<GroupedCard>();
                var processing  = new List<GroupedCard>();
                var other       = new List<GroupedCard>();

                foreach (var c in headers.Values) {
                    if (c.Keyword is "END" or "") continue;
                    var dto = Card(c);
                    if (In(c.Keyword, imageKeys))           image.Add(dto);
                    else if (In(c.Keyword, observationKeys)) observation.Add(dto);
                    else if (In(c.Keyword, instrumentKeys))  instrument.Add(dto);
                    else if (In(c.Keyword, siteKeys))        site.Add(dto);
                    else if (In(c.Keyword, processingKeys))  processing.Add(dto);
                    else                                     other.Add(dto);
                }

                static List<GroupedCard> Sort(List<GroupedCard> xs)
                    => xs.OrderBy(c => c.Keyword, StringComparer.OrdinalIgnoreCase).ToList();

                return Results.Ok(new {
                    fileName = Path.GetFileName(full),
                    totalCards = headers.Count,
                    groups = new[] {
                        new { name = "Observation", cards = Sort(observation) },
                        new { name = "Instrument",  cards = Sort(instrument)  },
                        new { name = "Image",       cards = Sort(image)       },
                        new { name = "Site & Weather", cards = Sort(site)     },
                        new { name = "Processing",  cards = Sort(processing)  },
                        new { name = "Other",       cards = Sort(other)       }
                    }
                });
            } catch (UnauthorizedAccessException ex) {
                return Results.Json(new { error = ex.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            } catch (FileNotFoundException ex) {
                return Results.NotFound(new { error = ex.Message });
            } catch (Exception ex) {
                return Results.Problem(ex.Message);
            }
        });

        // Update individual FITS header keywords in place. The pixel
        // data stays untouched -- only the 80-byte card images in the
        // header block(s) are rewritten. Used by the editable-headers
        // panel in the image viewer.
        g.MapPut("/fits-headers", async (FileBrowserService svc,
                                         UpdateFitsHeadersRequest req) => {
            try {
                if (string.IsNullOrWhiteSpace(req.Path))
                    return Results.BadRequest(new { error = "path is required" });
                if (req.Headers == null || req.Headers.Count == 0)
                    return Results.BadRequest(new { error = "headers list is empty" });

                var full = svc.ResolveSafe(req.Path, mustExist: true);
                if (!File.Exists(full))
                    return Results.NotFound(new { error = "File not found" });
                var ext = Path.GetExtension(full).ToLowerInvariant();
                if (ext != ".fits" && ext != ".fit" && ext != ".fts")
                    return Results.BadRequest(new { error = "Not a FITS file" });

                // Reject read-only files up front so the error message
                // is clearer than a generic IOException.
                var attrs = File.GetAttributes(full);
                if (attrs.HasFlag(FileAttributes.ReadOnly))
                    return Results.Json(new { error = "File is read-only" },
                        statusCode: StatusCodes.Status409Conflict);

                var updates = req.Headers
                    .Where(h => !string.IsNullOrWhiteSpace(h.Keyword))
                    .Select(h => (h.Keyword.Trim().ToUpperInvariant(), h.Value ?? ""))
                    .ToList();

                if (updates.Count == 0)
                    return Results.BadRequest(new { error = "No valid keywords" });

                await Task.Run(() => FITSHeaderWriter.UpdateHeaders(full, updates));
                return Results.Ok(new { ok = true, updated = updates.Count });
            } catch (UnauthorizedAccessException ex) {
                return Results.Json(new { error = ex.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            } catch (FileNotFoundException ex) {
                return Results.NotFound(new { error = ex.Message });
            } catch (Exception ex) {
                return Results.Problem(ex.Message);
            }
        });

        // 256 px thumbnail with on-disk cache keyed by path hash so a
        // grid of 200 FITS doesn't keep regenerating on every refresh.
        g.MapGet("/thumb", async (FileBrowserService svc, IWebHostEnvironment env,
                                  string path, CancellationToken ct) => {
            try {
                var full = svc.ResolveSafe(path, mustExist: true);
                var kind = FileBrowserService.ClassifyForPreview(full);
                if (kind != PreviewKind.Fits && kind != PreviewKind.RasterPassthrough
                    && kind != PreviewKind.TiffDecode)
                    return Results.NotFound();

                var cacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NINA.Polaris", "files", "thumbs");
                Directory.CreateDirectory(cacheDir);
                var cachePath = Path.Combine(cacheDir, FileBrowserService.PathHash(full) + ".jpg");

                // Regenerate if the source is newer than the cache (the
                // user might have re-processed the file in the meantime).
                var srcMtime = File.GetLastWriteTimeUtc(full);
                if (File.Exists(cachePath)
                    && File.GetLastWriteTimeUtc(cachePath) >= srcMtime) {
                    return Results.File(cachePath, "image/jpeg");
                }

                byte[]? jpeg = kind switch {
                    PreviewKind.Fits             => await RenderGatedAsync(()
                        => FitsThumbnailer.RenderJpegFromPath(full, 256, 80), ct),
                    PreviewKind.RasterPassthrough => await RenderGatedAsync(()
                        => DecodeRasterToJpeg(full, 256), ct),
                    PreviewKind.TiffDecode       => await RenderGatedAsync(()
                        => DecodeRasterToJpeg(full, 256), ct),
                    _ => null
                };
                if (jpeg == null) return Results.NotFound();
                await File.WriteAllBytesAsync(cachePath, jpeg, ct);
                return Results.File(jpeg, "image/jpeg");
            } catch (UnauthorizedAccessException ex) {
                return Results.Json(new { error = ex.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            } catch {
                return Results.NotFound();
            }
        });

        // --- Mutations ---------------------------------------------

        g.MapPost("/copy", async (FileBrowserService svc, CopyMoveRequest req, CancellationToken ct) => {
            try {
                await svc.CopyAsync(req.Src, req.Dst, req.Overwrite, ct);
                return Results.Ok(new { ok = true });
            } catch (Exception ex) { return MapError(ex); }
        });

        g.MapPost("/move", async (FileBrowserService svc, CopyMoveRequest req, CancellationToken ct) => {
            try {
                await svc.MoveAsync(req.Src, req.Dst, req.Overwrite, ct);
                return Results.Ok(new { ok = true });
            } catch (Exception ex) { return MapError(ex); }
        });

        // Silent "discard": move a file into a `discarded/` folder at the studio
        // root instead of deleting it, so a mis-click in the viewer's cull loop
        // is recoverable. No confirmation — that's the point of a quick cull.
        // The destination folder is created on demand and names are de-duped.
        g.MapPost("/discard", async (FileBrowserService svc, ProfileService profiles,
                                     DiscardRequest req, CancellationToken ct) => {
            try {
                var root = profiles.Active.ImageOutputDir;
                if (string.IsNullOrWhiteSpace(root))
                    return Results.BadRequest(new { error = "No studio root configured." });
                var src = svc.ResolveSafe(req.Path, mustExist: true);
                var discardDir = Path.Combine(root, "discarded");
                var name = Path.GetFileName(src);
                var stem = Path.GetFileNameWithoutExtension(name);
                var ext = Path.GetExtension(name);
                var dst = Path.Combine(discardDir, name);
                for (int i = 2; File.Exists(dst); i++)
                    dst = Path.Combine(discardDir, $"{stem}_{i}{ext}");
                await svc.MoveAsync(src, dst, overwrite: false, ct);
                return Results.Ok(new { ok = true, movedTo = dst });
            } catch (Exception ex) { return MapError(ex); }
        });

        // Delete is the only mutator that requires an explicit
        // confirmed=true flag. The UI sets it after window.confirm().
        // Server-side guard so anything else hitting the API (curl,
        // a buggy client) can't blow away files by accident.
        g.MapPost("/delete", async (FileBrowserService svc, DeleteRequest req, CancellationToken ct) => {
            if (!req.Confirmed)
                return Results.Json(new { error = "confirmed=true is required" },
                    statusCode: StatusCodes.Status409Conflict);
            try {
                foreach (var path in req.Paths)
                    await svc.DeleteAsync(path, recursive: true, ct);
                return Results.Ok(new { ok = true, deleted = req.Paths.Count });
            } catch (Exception ex) { return MapError(ex); }
        });

        g.MapPost("/mkdir", async (FileBrowserService svc, MkdirRequest req) => {
            try {
                await svc.CreateFolderAsync(req.Parent, req.Name);
                return Results.Ok(new { ok = true });
            } catch (Exception ex) { return MapError(ex); }
        });

        g.MapPost("/rename", async (FileBrowserService svc, RenameRequest req) => {
            try {
                await svc.RenameAsync(req.Path, req.NewName);
                return Results.Ok(new { ok = true });
            } catch (Exception ex) { return MapError(ex); }
        });

        // Batch rename from FITS header values. dryRun=true returns the
        // old→new mapping for the preview without touching disk; the apply
        // call (dryRun=false) performs the renames. See
        // FileBrowserService.BatchRenameAsync for the template + collision
        // rules.
        g.MapPost("/batch-rename", async (FileBrowserService svc,
                                          BatchRenameRequest req, CancellationToken ct) => {
            try {
                var result = await svc.BatchRenameAsync(
                    req.Paths ?? [], req.Template ?? "", req.DryRun, ct);
                return Results.Ok(result);
            } catch (Exception ex) { return MapError(ex); }
        });

        // --- Studio root setter -----------------------------------

        // Convenience endpoint: validates the path, writes through to
        // the profile's ImageOutputDir, saves the profile. The STUDIO
        // tab rescans on its next visit (it reads ImageOutputDir live).
        g.MapPost("/studio-root", (FileBrowserService svc, ProfileService profiles,
                                   StudioRootRequest req) => {
            try {
                var full = svc.ResolveSafe(req.Path, mustExist: true);
                if (!Directory.Exists(full))
                    return Results.BadRequest(new { error = "Path is not a directory" });
                profiles.Active.ImageOutputDir = full;
                profiles.Save();
                return Results.Ok(new { ok = true, imageOutputDir = full });
            } catch (Exception ex) { return MapError(ex); }
        });
    }

    // True when `child` is `root` itself or nested under it. Used to decide
    // whether a background frame-library rescan (which only walks the studio
    // root) can ever cover the folder being listed.
    private static bool IsUnder(string child, string root) {
        try {
            var rel = Path.GetRelativePath(root, child);
            return rel == "." || (!rel.StartsWith("..") && !Path.IsPathRooted(rel));
        } catch {
            return false;
        }
    }

    private static IResult MapError(Exception ex) => ex switch {
        UnauthorizedAccessException uae => Results.Json(new { error = uae.Message },
            statusCode: StatusCodes.Status403Forbidden),
        ArgumentException ae => Results.BadRequest(new { error = ae.Message }),
        FileNotFoundException fnf => Results.NotFound(new { error = fnf.Message }),
        DirectoryNotFoundException dnf => Results.NotFound(new { error = dnf.Message }),
        IOException ioe => Results.Json(new { error = ioe.Message },
            statusCode: StatusCodes.Status409Conflict),
        _ => Results.Problem(ex.Message)
    };

    // --- Helpers for the preview endpoint --------------------------

    /// <summary>Sentinel thrown from a RenderCache render lambda when a
    /// decode fails, so the caller can map it to 422 instead of letting
    /// the failure surface as a generic 500.</summary>
    private sealed class RenderFailedException : Exception { }

    private static byte[]? DecodeRasterToPng(string path, int maxDim) {
        using var input = File.OpenRead(path);
        using var bmp = SKBitmap.Decode(input);
        if (bmp == null) return null;
        using var resized = ResizeIfLarger(bmp, maxDim);
        using var img = SKImage.FromBitmap(resized ?? bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[]? DecodeRasterToJpeg(string path, int maxDim) {
        using var input = File.OpenRead(path);
        using var bmp = SKBitmap.Decode(input);
        if (bmp == null) return null;
        using var resized = ResizeIfLarger(bmp, maxDim);
        using var img = SKImage.FromBitmap(resized ?? bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }

    private static SKBitmap? ResizeIfLarger(SKBitmap bmp, int maxDim) {
        var longSide = Math.Max(bmp.Width, bmp.Height);
        if (longSide <= maxDim) return null;
        var scale = (double)maxDim / longSide;
        var w = Math.Max(1, (int)Math.Round(bmp.Width * scale));
        var h = Math.Max(1, (int)Math.Round(bmp.Height * scale));
        return bmp.Resize(new SKImageInfo(w, h, bmp.ColorType, bmp.AlphaType),
            SKSamplingOptions.Default);
    }

    private static async Task<string> ReadHeadAsync(string path, int maxBytes, CancellationToken ct) {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buf = new byte[Math.Min(maxBytes, fs.Length)];
        var read = await fs.ReadAsync(buf, ct);
        // Skip a leading UTF-8 BOM if present so the editor doesn't
        // show a stray glyph at the top of the preview.
        var start = 0;
        if (read >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) start = 3;
        var truncated = read >= maxBytes;
        var text = Encoding.UTF8.GetString(buf, start, read - start);
        if (truncated) text += "\n\n--- truncated to first " + maxBytes + " bytes ---\n";
        return text;
    }

    // --- DTOs --------------------------------------------------------

    public record CopyMoveRequest(string Src, string Dst, bool Overwrite);
    public record DiscardRequest(string Path);
    public record DeleteRequest(List<string> Paths, bool Confirmed);
    public record MkdirRequest(string Parent, string Name);
    public record RenameRequest(string Path, string NewName);
    public record BatchRenameRequest(List<string> Paths, string Template, bool DryRun);
    public record ZipRequest(List<string> Paths, string? RootForNames, string? FileName);
    public record StudioRootRequest(string Path);
    public record GroupedCard(string Keyword, string Value, string Comment);
    public record UpdateFitsHeadersRequest(string Path, List<HeaderUpdate> Headers);
    public record HeaderUpdate(string Keyword, string Value);
}