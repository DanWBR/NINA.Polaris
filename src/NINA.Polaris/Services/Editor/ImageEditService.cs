using System.Collections.Concurrent;
using NINA.Image.Editor;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using SkiaSharp;

namespace NINA.Polaris.Services.Editor;

/// <summary>
/// Server-side editor pipeline + session cache. The Lightroom-style editor
/// on the front-end opens a frame once (decode + auto-stretch to an 8-bit
/// working buffer), then sends many edit-preview requests against the same
/// session as the user drags sliders. We hold the working buffer in memory
/// so each preview only pays for the cheap byte[] → EditPipeline.Apply →
/// JPEG encode, never re-decodes the source.
///
/// Why 8-bit working buffer (not full ushort): the user is already in
/// "viewing" space — the FITS has been auto-stretched into a displayable
/// tone range before they entered the editor. Lightroom's sliders feel the
/// way they do because they operate on display-referenced pixels, not
/// scene-referred radiance. This also makes the WASM version (ED-6)
/// drop-in identical — same EditPipeline, same byte[] in/out.
///
/// Sessions auto-evict after 30 min idle. A background reaper task runs
/// every 5 min — keeps the cache from holding gigabytes of decoded masters
/// across an overnight session.
/// </summary>
public class ImageEditService : IDisposable {
    private readonly ILogger<ImageEditService> _logger;
    private readonly ProfileService _profile;
    private readonly ConcurrentDictionary<string, EditSession> _sessions = new();
    private readonly Timer _reaper;
    private static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromMinutes(30);

    public ImageEditService(ProfileService profile, ILogger<ImageEditService> logger) {
        _profile = profile;
        _logger = logger;
        _reaper = new Timer(_ => Reap(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Open a source file. Decodes via the appropriate reader, applies an
    /// initial auto-stretch (so the 8-bit working buffer is in displayable
    /// tone space), and returns the session id + dimensions.
    /// </summary>
    public Task<EditSessionInfo?> LoadAsync(string path, CancellationToken ct = default)
        => Task.Run<EditSessionInfo?>(() => LoadSync(path), ct);

    /// <summary>
    /// Apply <paramref name="edits"/> to the cached working buffer and
    /// return an encoded preview JPEG. <paramref name="maxDim"/> caps the
    /// long side so slider drags stay snappy on big masters; pass 0 to
    /// render at full resolution.
    /// </summary>
    public Task<byte[]?> RenderPreviewAsync(string sessionId, EditParams edits,
                                            int maxDim = 1600, int quality = 85,
                                            CancellationToken ct = default)
        => Task.Run<byte[]?>(() => RenderPreviewSync(sessionId, edits, maxDim, quality), ct);

    /// <summary>
    /// Apply <paramref name="edits"/> then compute a 256-bin histogram per
    /// channel. Returns a flat int[] of length 256 (mono) or 768 (RGB
    /// interleaved by R/G/B blocks).
    /// </summary>
    public Task<int[]?> ComputeHistogramAsync(string sessionId, EditParams edits,
                                               CancellationToken ct = default)
        => Task.Run<int[]?>(() => HistogramSync(sessionId, edits), ct);

    /// <summary>
    /// Full-resolution export — applies edits + crop/resize, encodes to
    /// the requested format and writes the file. Returns the absolute path
    /// of the written file (or null on failure).
    /// </summary>
    public Task<string?> ExportAsync(ExportRequest req, CancellationToken ct = default)
        => Task.Run<string?>(() => ExportSync(req), ct);

    /// <summary>Release a session early. Idempotent.</summary>
    public void Release(string sessionId) {
        if (_sessions.TryRemove(sessionId, out var s)) {
            s.Dispose();
        }
    }

    public IReadOnlyList<EditSessionInfo> ActiveSessions() {
        return _sessions.Values
            .Select(s => new EditSessionInfo(s.Id, s.SourcePath, s.Width, s.Height, s.Channels))
            .ToList();
    }

    /// <summary>
    /// ED-6: hand out the decoded working buffer for the WASM dispatch.
    /// Returns null if the session id isn't known (caller treats that
    /// as "fall back to server-mode"). Caller is responsible for
    /// streaming the byte[] back to the browser with appropriate
    /// content-type + dimension headers.
    /// </summary>
    public (byte[] data, int w, int h, int channels)? GetWorkingBuffer(string sessionId) {
        if (!_sessions.TryGetValue(sessionId, out var s)) return null;
        s.Touch();
        // Return a defensive copy — clients of the API shouldn't see the
        // session's live buffer (mutating it would silently corrupt
        // subsequent server-side previews).
        var copy = (byte[])s.Working.Clone();
        return (copy, s.Width, s.Height, s.Channels);
    }

    // ─── load ────────────────────────────────────────────────────────

    private EditSessionInfo? LoadSync(string path) {
        if (!File.Exists(path)) {
            _logger.LogWarning("Editor load: file not found ({Path})", path);
            return null;
        }

        try {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            byte[] working;
            int width, height, channels;

            switch (ext) {
                case ".fits":
                case ".fit":
                case ".fts": {
                    using var fs = File.OpenRead(path);
                    var img = FITSReader.Read(fs);
                    width = img.Properties.Width;
                    height = img.Properties.Height;
                    channels = 1;
                    // Auto-stretch into 8-bit working space.
                    working = AutoStretch.Apply(img.Data, width, height, img.Properties.BitDepth);
                    break;
                }
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".tif":
                case ".tiff": {
                    using var skBmp = SKBitmap.Decode(path);
                    if (skBmp == null) {
                        _logger.LogWarning("Editor load: SkiaSharp couldn't decode {Path}", path);
                        return null;
                    }
                    width = skBmp.Width;
                    height = skBmp.Height;
                    // Force RGB (drop alpha). Skia gives us BGRA8888 by
                    // default — convert to plain RGB interleaved for the
                    // pipeline.
                    var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
                    using var rgba = new SKBitmap(info);
                    using (var canvas = new SKCanvas(rgba)) canvas.DrawBitmap(skBmp, 0, 0);
                    var src = rgba.GetPixelSpan();
                    // Detect grayscale-vs-RGB: if R==G==B at a few sample
                    // points the source is likely mono.
                    bool isMono = LooksMono(rgba);
                    if (isMono) {
                        channels = 1;
                        working = new byte[width * height];
                        for (int i = 0, j = 0; j < working.Length; i += 4, j++) {
                            working[j] = src[i]; // R == G == B
                        }
                    } else {
                        channels = 3;
                        working = new byte[width * height * 3];
                        for (int i = 0, j = 0; i < src.Length; i += 4, j += 3) {
                            working[j]     = src[i];     // R
                            working[j + 1] = src[i + 1]; // G
                            working[j + 2] = src[i + 2]; // B
                        }
                    }
                    break;
                }
                default:
                    _logger.LogWarning("Editor load: unsupported extension {Ext}", ext);
                    return null;
            }

            var id = Guid.NewGuid().ToString("N");
            var session = new EditSession(id, path, working, width, height, channels);
            _sessions[id] = session;
            _logger.LogInformation("Editor session {Id} opened ({W}x{H} ch={Ch}) for {Path}",
                id, width, height, channels, path);
            return new EditSessionInfo(id, path, width, height, channels);
        } catch (Exception ex) {
            _logger.LogError(ex, "Editor load failed for {Path}", path);
            return null;
        }
    }

    private static bool LooksMono(SKBitmap bmp) {
        // Sample 25 points in a grid; if every one has R==G==B the image
        // is monochrome stored in RGB containers.
        var px = bmp.GetPixelSpan();
        int stride = 4;
        int samples = 0;
        int w = bmp.Width, h = bmp.Height;
        for (int y = 0; y < 5; y++) {
            for (int x = 0; x < 5; x++) {
                int sx = x * (w - 1) / 4;
                int sy = y * (h - 1) / 4;
                int o = (sy * w + sx) * stride;
                if (px[o] != px[o + 1] || px[o + 1] != px[o + 2]) return false;
                samples++;
            }
        }
        return samples > 0;
    }

    // ─── preview ─────────────────────────────────────────────────────

    private byte[]? RenderPreviewSync(string sessionId, EditParams edits, int maxDim, int quality) {
        if (!_sessions.TryGetValue(sessionId, out var s)) {
            _logger.LogDebug("Editor preview: session {Id} not found", sessionId);
            return null;
        }
        s.Touch();

        // Down-sample first so the pipeline runs on a smaller buffer when
        // preview maxDim is set. Crop is applied at full-res to keep the
        // crop tight to pixel boundaries; resize after the pipeline so
        // sharpening / clarity radii are computed in source pixels.
        var working = (byte[])s.Working.Clone();
        int w = s.Width, h = s.Height;

        // Apply the edit pipeline first (on either full-res or already
        // cropped buffer if a crop is active in edits — but for preview
        // we want speed, so downscale BEFORE the pipeline if no crop).
        if (edits.Crop == null && maxDim > 0 && (w > maxDim || h > maxDim)) {
            double scale = (double)maxDim / Math.Max(w, h);
            int tw = (int)Math.Round(w * scale);
            int th = (int)Math.Round(h * scale);
            var (downscaled, dw, dh) = EditPipeline.ApplyCropResize(working, w, h, s.Channels, null, tw, th);
            working = downscaled; w = dw; h = dh;
        }

        EditPipeline.Apply(working, w, h, s.Channels, edits);

        // If a crop is set, apply it after the pipeline (keep things in
        // the working frame's coordinate system).
        if (edits.Crop != null || (maxDim > 0 && edits.Crop != null)) {
            var (cropped, cw, ch) = EditPipeline.ApplyCropResize(
                working, w, h, s.Channels, edits.Crop, null, null);
            // And resize to fit maxDim after crop.
            if (maxDim > 0 && (cw > maxDim || ch > maxDim)) {
                double scale = (double)maxDim / Math.Max(cw, ch);
                int tw = (int)Math.Round(cw * scale);
                int th = (int)Math.Round(ch * scale);
                var (resized, rw, rh) = EditPipeline.ApplyCropResize(cropped, cw, ch, s.Channels, null, tw, th);
                working = resized; w = rw; h = rh;
            } else {
                working = cropped; w = cw; h = ch;
            }
        }

        return EncodeJpeg(working, w, h, s.Channels, quality);
    }

    // ─── histogram ───────────────────────────────────────────────────

    private int[]? HistogramSync(string sessionId, EditParams edits) {
        if (!_sessions.TryGetValue(sessionId, out var s)) return null;
        s.Touch();

        // Histogram on a small downsampled copy is statistically equivalent
        // to the full-res one for chart purposes and ~50× faster.
        var working = (byte[])s.Working.Clone();
        int w = s.Width, h = s.Height;
        if (w > 512 || h > 512) {
            double scale = 512.0 / Math.Max(w, h);
            int tw = (int)Math.Round(w * scale);
            int th = (int)Math.Round(h * scale);
            var (down, dw, dh) = EditPipeline.ApplyCropResize(working, w, h, s.Channels, null, tw, th);
            working = down; w = dw; h = dh;
        }
        EditPipeline.Apply(working, w, h, s.Channels, edits);

        if (s.Channels == 1) {
            var hist = new int[256];
            for (int i = 0; i < working.Length; i++) hist[working[i]]++;
            return hist;
        } else {
            var hist = new int[768];      // R[0..255] | G[256..511] | B[512..767]
            for (int i = 0; i < working.Length; i += 3) {
                hist[working[i]]++;
                hist[256 + working[i + 1]]++;
                hist[512 + working[i + 2]]++;
            }
            return hist;
        }
    }

    // ─── export ──────────────────────────────────────────────────────

    private string? ExportSync(ExportRequest req) {
        if (!_sessions.TryGetValue(req.SessionId, out var s)) {
            _logger.LogWarning("Editor export: session {Id} not found", req.SessionId);
            return null;
        }
        s.Touch();

        var working = (byte[])s.Working.Clone();
        int w = s.Width, h = s.Height;

        EditPipeline.Apply(working, w, h, s.Channels, req.Edits);

        // Apply crop + final resize in one pass.
        var (final, fw, fh) = EditPipeline.ApplyCropResize(
            working, w, h, s.Channels, req.Edits.Crop, req.TargetWidth, req.TargetHeight);

        // Resolve output path. If caller omitted it, default to
        // {rig}/processed/{sourceStem}/edited/{stem}__edited_{stamp}.{ext}
        // alongside the source file.
        var outPath = req.OutputPath;
        if (string.IsNullOrWhiteSpace(outPath)) {
            outPath = DefaultExportPath(s.SourcePath, req.Format);
        }

        try {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            EncodeToFile(final, fw, fh, s.Channels, req.Format, req.Quality, outPath);
            _logger.LogInformation("Editor export: {Path} ({W}x{H} ch={Ch}, fmt={Fmt})",
                outPath, fw, fh, s.Channels, req.Format);
            return outPath;
        } catch (Exception ex) {
            _logger.LogError(ex, "Editor export failed for session {Id}", req.SessionId);
            return null;
        }
    }

    private string DefaultExportPath(string sourcePath, string format) {
        var ext = format.ToLowerInvariant() switch {
            "png" => ".png",
            "tif" or "tiff" => ".tif",
            _ => ".jpg"
        };
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var sourceDir = Path.GetDirectoryName(sourcePath) ?? ".";
        var editedDir = Path.Combine(sourceDir, "edited");
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        return Path.Combine(editedDir, $"{stem}__edited_{stamp}{ext}");
    }

    // ─── encode helpers ──────────────────────────────────────────────

    private static byte[] EncodeJpeg(byte[] buf, int w, int h, int channels, int quality) {
        using var bmp = WrapAsSkiaBitmap(buf, w, h, channels);
        // JPEG decoders are friendliest with Rgba8888 source.
        SKBitmap toEncode = bmp;
        SKBitmap? rgba = null;
        try {
            if (bmp.ColorType != SKColorType.Rgba8888) {
                rgba = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
                using (var canvas = new SKCanvas(rgba)) canvas.DrawBitmap(bmp, 0, 0);
                toEncode = rgba;
            }
            using var data = toEncode.Encode(SKEncodedImageFormat.Jpeg, quality);
            return data?.ToArray() ?? Array.Empty<byte>();
        } finally { rgba?.Dispose(); }
    }

    private static void EncodeToFile(byte[] buf, int w, int h, int channels,
                                      string format, int quality, string path) {
        using var bmp = WrapAsSkiaBitmap(buf, w, h, channels);
        var fmt = format.ToLowerInvariant() switch {
            "png" => SKEncodedImageFormat.Png,
            "tif" or "tiff" => SKEncodedImageFormat.Png, // Skia ships no TIFF; PNG used as 8-bit-lossless surrogate
            _ => SKEncodedImageFormat.Jpeg
        };
        SKBitmap toEncode = bmp;
        SKBitmap? rgba = null;
        try {
            // JPEG needs Rgba8888 source; PNG handles Gray8 natively.
            if (fmt == SKEncodedImageFormat.Jpeg && bmp.ColorType != SKColorType.Rgba8888) {
                rgba = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
                using (var canvas = new SKCanvas(rgba)) canvas.DrawBitmap(bmp, 0, 0);
                toEncode = rgba;
            }
            using var data = toEncode.Encode(fmt, Math.Clamp(quality, 1, 100));
            using var fs = File.Create(path);
            data?.SaveTo(fs);
        } finally { rgba?.Dispose(); }
    }

    private static SKBitmap WrapAsSkiaBitmap(byte[] buf, int w, int h, int channels) {
        if (channels == 1) {
            var bmp = new SKBitmap(w, h, SKColorType.Gray8, SKAlphaType.Opaque);
            unsafe {
                fixed (byte* p = buf) bmp.SetPixels((IntPtr)p);
            }
            return bmp.Copy();
        } else {
            // RGB interleaved → expand to RGBA for Skia (which doesn't
            // have a packed-RGB ColorType).
            var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
            var dst = new byte[w * h * 4];
            for (int i = 0, j = 0; i < buf.Length; i += 3, j += 4) {
                dst[j]     = buf[i];
                dst[j + 1] = buf[i + 1];
                dst[j + 2] = buf[i + 2];
                dst[j + 3] = 255;
            }
            unsafe {
                fixed (byte* p = dst) bmp.SetPixels((IntPtr)p);
            }
            return bmp.Copy();
        }
    }

    // ─── reaper ──────────────────────────────────────────────────────

    private void Reap() {
        var cutoff = DateTime.UtcNow - SessionIdleTimeout;
        foreach (var kv in _sessions) {
            if (kv.Value.LastTouchedUtc < cutoff) {
                if (_sessions.TryRemove(kv.Key, out var s)) {
                    _logger.LogDebug("Editor session {Id} reaped (idle > 30 min)", kv.Key);
                    s.Dispose();
                }
            }
        }
    }

    public void Dispose() {
        _reaper.Dispose();
        foreach (var s in _sessions.Values) s.Dispose();
        _sessions.Clear();
    }

    // ─── session bag ─────────────────────────────────────────────────

    private sealed class EditSession : IDisposable {
        public string Id { get; }
        public string SourcePath { get; }
        public byte[] Working { get; }
        public int Width { get; }
        public int Height { get; }
        public int Channels { get; }
        public DateTime LastTouchedUtc { get; private set; }

        public EditSession(string id, string sourcePath, byte[] working, int w, int h, int channels) {
            Id = id; SourcePath = sourcePath; Working = working;
            Width = w; Height = h; Channels = channels;
            LastTouchedUtc = DateTime.UtcNow;
        }

        public void Touch() => LastTouchedUtc = DateTime.UtcNow;
        public void Dispose() { /* nothing native to release */ }
    }
}

public record EditSessionInfo(string SessionId, string SourcePath,
                              int Width, int Height, int Channels);

public record ExportRequest(
    string SessionId,
    EditParams Edits,
    string Format = "jpg",         // jpg | png | tif
    int Quality = 92,              // 1..100 (jpeg)
    int? TargetWidth = null,       // null = natural width (post-crop)
    int? TargetHeight = null,
    string? OutputPath = null      // null = default path next to source
);
