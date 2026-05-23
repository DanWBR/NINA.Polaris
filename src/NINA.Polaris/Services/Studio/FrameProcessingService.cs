using NINA.Image.FileFormat.FITS;
using NINA.Image.FileFormat.TIFF;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using SkiaSharp;

namespace NINA.Polaris.Services.Studio;

/// <summary>
/// On-demand image processing for STUDIO's single-frame viewer:
///   - render stretched JPEG/PNG previews (caller-supplied black/mid/white)
///   - compute full statistics + star detection
///   - export TIFF / PNG / JPEG to the {rig}/processed/{target}/ tree
///
/// Slider drags hit /preview many times per second, so the decoded FITS
/// pixel buffer is kept in a small in-memory LRU keyed by frame id. The
/// stretch itself is just an LUT pass — cheap enough that we don't
/// bother caching rendered bytes.
///
/// Encoding stack: SkiaSharp for JPEG + PNG (matches the rest of the
/// codebase — NINA.Image.Portable.ImageAnalysis.JpegHelper, the live
/// stream encoder, all use Skia). 16-bit linear TIFF goes through our
/// own tiny <see cref="TiffWriter"/> because Skia doesn't ship a TIFF
/// encoder and we don't want a parallel image stack just for that.
/// </summary>
public class FrameProcessingService {
    private readonly FrameLibraryService _library;
    private readonly ProfileService _profile;
    private readonly ILogger<FrameProcessingService> _logger;

    // Tiny LRU — decoded FITS buffers are big (64MP × 2 bytes = 128 MB).
    // Four entries keeps slider drags responsive when the user is
    // alt-tabbing between two or three frames, no more.
    private const int CacheCapacity = 4;
    private readonly object _cacheLock = new();
    private readonly LinkedList<CachedFrame> _cache = new();

    public FrameProcessingService(FrameLibraryService library, ProfileService profile,
                                  ILogger<FrameProcessingService> logger) {
        _library = library;
        _profile = profile;
        _logger = logger;
    }

    private record CachedFrame(int Id, BaseImageData Data);

    private BaseImageData? LoadCached(int frameId) {
        lock (_cacheLock) {
            for (var n = _cache.First; n != null; n = n.Next) {
                if (n.Value.Id == frameId) {
                    _cache.Remove(n);
                    _cache.AddFirst(n);
                    return n.Value.Data;
                }
            }
        }

        var row = _library.GetById(frameId);
        if (row == null || !File.Exists(row.Path)) return null;

        BaseImageData decoded;
        try {
            using var fs = File.OpenRead(row.Path);
            decoded = FITSReader.Read(fs);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "FITS decode failed for frame {Id} ({Path})", frameId, row.Path);
            return null;
        }

        lock (_cacheLock) {
            _cache.AddFirst(new CachedFrame(frameId, decoded));
            while (_cache.Count > CacheCapacity) _cache.RemoveLast();
        }
        return decoded;
    }

    public void Invalidate(int frameId) {
        lock (_cacheLock) {
            for (var n = _cache.First; n != null; n = n.Next) {
                if (n.Value.Id == frameId) { _cache.Remove(n); break; }
            }
        }
    }

    /// <summary>Stretch parameters used for a preview / export. Any
    /// field left null is auto-computed.</summary>
    public record StretchOptions(double? Black, double? Mid, double? White);

    /// <summary>Render a stretched preview as JPEG bytes. maxSize caps
    /// the long side in pixels (default 1600).</summary>
    public Task<byte[]?> RenderJpegAsync(int frameId, StretchOptions opts,
                                         int maxSize = 1600, int quality = 85,
                                         CancellationToken ct = default)
        => Task.Run<byte[]?>(() => RenderEncoded(frameId, opts, maxSize, SKEncodedImageFormat.Jpeg, quality), ct);

    /// <summary>Render a stretched preview as 8-bit PNG bytes.</summary>
    public Task<byte[]?> RenderPngAsync(int frameId, StretchOptions opts,
                                        int maxSize = 1600, CancellationToken ct = default)
        => Task.Run<byte[]?>(() => RenderEncoded(frameId, opts, maxSize, SKEncodedImageFormat.Png, 100), ct);

    /// <summary>Compute the auto-stretch defaults the UI should seed
    /// sliders with — black/mid/white normalised 0..1 — without
    /// applying or rendering anything.</summary>
    public AutoStretch.StretchParams? AutoStretchDefaults(int frameId) {
        var img = LoadCached(frameId);
        if (img == null) return null;
        return AutoStretch.ComputeAutoStretchParams(
            img.Data, img.Properties.Width, img.Properties.Height, img.Properties.BitDepth);
    }

    /// <summary>Full statistics + detected-star summary for the frame.
    /// The star list is capped at 500 entries — that's what StarDetector
    /// returns by default and it's more than enough for a viewer
    /// overlay.</summary>
    public FrameStats? ComputeStats(int frameId, bool includeStars = true) {
        var img = LoadCached(frameId);
        if (img == null) return null;
        var s = ImageStatistics.Create(img);
        var stars = new List<DetectedStarDto>();
        double hfrAvg = 0;
        if (includeStars) {
            try {
                var detected = new StarDetector { MaxStars = 500 }.Detect(
                    img.Data, img.Properties.Width, img.Properties.Height);
                if (detected.Count > 0) {
                    hfrAvg = detected.Average(d => d.HFR);
                    foreach (var d in detected) {
                        stars.Add(new DetectedStarDto(d.X, d.Y, d.HFR, d.Peak, d.Flux));
                    }
                }
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Star detection failed for frame {Id}", frameId);
            }
        }
        return new FrameStats(
            Width:    s.Width,
            Height:   s.Height,
            Mean:     s.Mean,
            Median:   s.Median,
            StDev:    s.StDev,
            Mad:      s.MAD,
            Min:      s.Min,
            Max:      s.Max,
            StarCount: stars.Count,
            HfrAvg:   hfrAvg,
            Histogram: BuildHistogram(img.Data, img.Properties.BitDepth, 256),
            Stars:    stars);
    }

    /// <summary>Export the frame to {rig}/processed/{target}/ as TIFF
    /// (16-bit when stretched=false — preserves dynamic range; 8-bit
    /// when stretched=true), PNG (8-bit stretched), or JPEG (8-bit
    /// stretched). Returns the absolute path of the written file.</summary>
    public Task<string?> ExportAsync(int frameId, string format, StretchOptions opts,
                                     bool stretched = true, CancellationToken ct = default)
        => Task.Run<string?>(() => ExportSync(frameId, format, opts, stretched), ct);

    // --- internals ---

    private byte[]? RenderEncoded(int frameId, StretchOptions opts, int maxSize,
                                  SKEncodedImageFormat fmt, int quality) {
        var img = LoadCached(frameId);
        if (img == null) return null;
        var stretched = ApplyStretch(img, opts);

        using var bitmap = LoadGray8Bitmap(stretched, img.Properties.Width, img.Properties.Height);
        using var resized = MaybeResize(bitmap, maxSize);

        // JPEG decoders don't all handle Gray8 reliably, so we round-trip
        // through Rgba8888 for JPEG. PNG is fine with Gray8 directly.
        SKBitmap forEncode = resized;
        SKBitmap? rgbBuf = null;
        try {
            if (fmt == SKEncodedImageFormat.Jpeg && resized.ColorType == SKColorType.Gray8) {
                rgbBuf = new SKBitmap(resized.Width, resized.Height,
                    SKColorType.Rgba8888, SKAlphaType.Opaque);
                using (var canvas = new SKCanvas(rgbBuf)) canvas.DrawBitmap(resized, 0, 0);
                forEncode = rgbBuf;
            }
            using var data = forEncode.Encode(fmt, quality);
            return data?.ToArray();
        } finally {
            rgbBuf?.Dispose();
        }
    }

    private string? ExportSync(int frameId, string format, StretchOptions opts, bool stretched) {
        var row = _library.GetById(frameId);
        if (row == null) return null;
        var img = LoadCached(frameId);
        if (img == null) return null;

        var profile = _profile.Active;
        var outRoot = profile.ImageOutputDir;
        if (string.IsNullOrWhiteSpace(outRoot)) return null;

        var rigName = _profile.ActiveEquipmentProfile?.Name ?? "Default";
        var target = string.IsNullOrEmpty(row.Target) ? "Unknown" : row.Target;
        var processedDir = Path.Combine(outRoot, Sanitize(rigName), "processed", Sanitize(target));
        Directory.CreateDirectory(processedDir);

        var fmt = (format ?? "tif").Trim().ToLowerInvariant();
        var ext = fmt switch {
            "tif" or "tiff" => ".tif",
            "png"           => ".png",
            "jpg" or "jpeg" => ".jpg",
            _               => ".tif"
        };
        var baseName = Path.GetFileNameWithoutExtension(row.FileName);
        var outPath = Path.Combine(processedDir, $"{baseName}{ext}");
        int copy = 1;
        while (File.Exists(outPath)) outPath = Path.Combine(processedDir, $"{baseName}_{copy++}{ext}");

        try {
            int w = img.Properties.Width;
            int h = img.Properties.Height;
            switch (fmt) {
                case "tif":
                case "tiff": {
                    if (stretched) {
                        var bytes = ApplyStretch(img, opts);
                        TiffWriter.Write8(bytes, w, h, outPath);
                    } else {
                        // 16-bit linear — the whole point of TIFF export
                        // for downstream PixInsight / Siril.
                        TiffWriter.Write16(img.Data, w, h, outPath);
                    }
                    break;
                }
                case "png": {
                    var bytes = ApplyStretch(img, opts);
                    WriteSkiaFile(bytes, w, h, outPath, SKEncodedImageFormat.Png, 100, wrapRgb: false);
                    break;
                }
                case "jpg":
                case "jpeg": {
                    var bytes = ApplyStretch(img, opts);
                    WriteSkiaFile(bytes, w, h, outPath, SKEncodedImageFormat.Jpeg, 92, wrapRgb: true);
                    break;
                }
                default:
                    return null;
            }
            _logger.LogInformation("Exported frame {Id} -> {Path}", frameId, outPath);
            return outPath;
        } catch (Exception ex) {
            _logger.LogError(ex, "Export failed for frame {Id}", frameId);
            return null;
        }
    }

    private static byte[] ApplyStretch(BaseImageData img, StretchOptions opts) {
        var w = img.Properties.Width;
        var h = img.Properties.Height;
        var bits = img.Properties.BitDepth;

        if (opts.Black == null || opts.Mid == null || opts.White == null) {
            var auto = AutoStretch.ComputeAutoStretchParams(img.Data, w, h, bits);
            var b  = opts.Black ?? auto.Black;
            var m  = opts.Mid   ?? auto.Mid;
            var wp = opts.White ?? auto.White;
            return AutoStretch.ApplyManual(img.Data, w, h, b, m, wp, bits);
        }
        return AutoStretch.ApplyManual(img.Data, w, h,
            opts.Black.Value, opts.Mid.Value, opts.White.Value, bits);
    }

    /// <summary>Wrap an existing grayscale byte buffer in an SKBitmap
    /// without copying. The buffer must outlive the bitmap (or the
    /// caller passes a fresh array each time, which is what we do).</summary>
    private static SKBitmap LoadGray8Bitmap(byte[] pixels, int width, int height) {
        var bitmap = new SKBitmap(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        unsafe {
            fixed (byte* p = pixels) {
                bitmap.SetPixels((IntPtr)p);
            }
        }
        // SetPixels with a raw pointer doesn't copy; copy now so the
        // backing byte[] can be GC'd after this call returns.
        return bitmap.Copy();
    }

    private static SKBitmap MaybeResize(SKBitmap src, int maxSize) {
        if (src.Width <= maxSize && src.Height <= maxSize) return src;
        double scale = (double)maxSize / Math.Max(src.Width, src.Height);
        int newW = (int)Math.Round(src.Width * scale);
        int newH = (int)Math.Round(src.Height * scale);
        var info = new SKImageInfo(newW, newH, src.ColorType, src.AlphaType);
        // High-quality downsampling — slider previews don't drag this
        // path on the hot loop (only on first open + format change),
        // so the extra CPU is fine.
        return src.Resize(info, SKSamplingOptions.Default);
    }

    private static void WriteSkiaFile(byte[] gray8, int width, int height, string path,
                                      SKEncodedImageFormat fmt, int quality, bool wrapRgb) {
        using var bitmap = LoadGray8Bitmap(gray8, width, height);
        SKBitmap forEncode = bitmap;
        SKBitmap? rgbBuf = null;
        try {
            if (wrapRgb) {
                rgbBuf = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
                using (var canvas = new SKCanvas(rgbBuf)) canvas.DrawBitmap(bitmap, 0, 0);
                forEncode = rgbBuf;
            }
            using var data = forEncode.Encode(fmt, quality);
            using var fs = File.Create(path);
            data?.SaveTo(fs);
        } finally {
            rgbBuf?.Dispose();
        }
    }

    private static int[] BuildHistogram(ushort[] data, int bitDepth, int bins) {
        var h = new int[bins];
        double maxVal = (1 << bitDepth) - 1;
        for (int i = 0; i < data.Length; i++) {
            int bin = (int)(data[i] / maxVal * (bins - 1));
            if (bin < 0) bin = 0;
            else if (bin >= bins) bin = bins - 1;
            h[bin]++;
        }
        return h;
    }

    private static string Sanitize(string s) {
        if (string.IsNullOrWhiteSpace(s)) return "Unknown";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(' ', '_');
    }
}

public record DetectedStarDto(double X, double Y, double Hfr, double Peak, double Flux);

public record FrameStats(
    int Width, int Height,
    double Mean, double Median, double StDev, double Mad,
    int Min, int Max,
    int StarCount, double HfrAvg,
    int[] Histogram,
    IReadOnlyList<DetectedStarDto> Stars);
