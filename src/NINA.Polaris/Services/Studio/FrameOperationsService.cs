using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;

namespace NINA.Polaris.Services.Studio;

/// <summary>
/// One-shot per-frame operations the STUDIO viewer can invoke:
///   - <see cref="DebayerAsync"/> — demosaic an OSC frame to a
///     luminance plane (R/G/B blended via Rec.601). Writes a new FITS
///     under {rig}/processed/{target}/ with BAYERPAT cleared so
///     downstream tools don't re-demosaic.
///   - <see cref="RemoveGradientAsync"/> — fit a 2D polynomial
///     gradient and subtract it. Same output location.
///
/// Both ops trigger a library rescan so the resulting FITS shows up in
/// the browser immediately. v1 chooses a synthetic luminance output
/// for debayer (single-channel) rather than dragging full-RGB through
/// the FITS pipeline; full-colour TIFF export of the 3 channels is a
/// follow-up if anyone needs it for on-server processing (most users
/// export to PixInsight / Siril for colour work anyway).
/// </summary>
public class FrameOperationsService {
    private readonly FrameLibraryService _library;
    private readonly ProfileService _profile;
    private readonly FrameProcessingService _processing;
    private readonly ILogger<FrameOperationsService> _logger;

    public FrameOperationsService(FrameLibraryService library, ProfileService profile,
                                  FrameProcessingService processing,
                                  ILogger<FrameOperationsService> logger) {
        _library = library;
        _profile = profile;
        _processing = processing;
        _logger = logger;
    }

    public async Task<string?> DebayerAsync(int frameId, CancellationToken ct = default)
        => await Task.Run<string?>(() => DebayerSync(frameId), ct);

    public async Task<string?> RemoveGradientAsync(int frameId, int? samplesX, int? samplesY,
                                                   int? polyDegree, CancellationToken ct = default)
        => await Task.Run<string?>(() => RemoveGradientSync(frameId, samplesX, samplesY, polyDegree), ct);

    public async Task<string?> NoiseReductionAsync(int frameId, int? radius, CancellationToken ct = default)
        => await Task.Run<string?>(() => NoiseReductionSync(frameId, radius), ct);

    public async Task<string?> SharpenAsync(int frameId, double? amount, int? radius, int? threshold,
                                            CancellationToken ct = default)
        => await Task.Run<string?>(() => SharpenSync(frameId, amount, radius, threshold), ct);

    // --- internals ---

    private string? DebayerSync(int frameId) {
        var row = _library.GetById(frameId);
        if (row == null || !File.Exists(row.Path)) return null;

        BaseImageData img;
        using (var fs = File.OpenRead(row.Path)) img = FITSReader.Read(fs);

        if (!img.Properties.IsBayered ||
            img.Properties.BayerPattern == NINA.Core.Enum.BayerPatternEnum.None) {
            _logger.LogWarning("Frame {Id} has no BAYERPAT header; cannot debayer.", frameId);
            return null;
        }

        var ch = BayerDebayer.Bilinear(img.Data, img.Properties.Width, img.Properties.Height,
            img.Properties.BayerPattern);
        var lum = BayerDebayer.ToLuminance(ch);

        var outPath = BuildOutputPath(row, suffix: "deb");
        // BAYERPAT cleared (none) on the output so re-reading it
        // doesn't suggest the file is still mosaiced.
        var outProps = new ImageProperties {
            Width = img.Properties.Width,
            Height = img.Properties.Height,
            BitDepth = img.Properties.BitDepth,
            BayerPattern = NINA.Core.Enum.BayerPatternEnum.None,
            IsBayered = false
        };
        var outImg = new BaseImageData(lum, outProps, img.MetaData);
        var kw = new List<KeyValuePair<string, string>> {
            new("DEBAYER", "Bilinear"),
            new("BAYERIN", img.Properties.BayerPattern.ToString())
        };
        FITSWriter.Write(outImg, outPath, customKeywords: kw);
        _logger.LogInformation("Debayered frame {Id} → {Path}", frameId, outPath);

        _processing.Invalidate(frameId);
        _ = Task.Run(() => _library.RescanAsync());
        return outPath;
    }

    private string? RemoveGradientSync(int frameId, int? samplesX, int? samplesY, int? polyDegree) {
        var row = _library.GetById(frameId);
        if (row == null || !File.Exists(row.Path)) return null;

        BaseImageData img;
        using (var fs = File.OpenRead(row.Path)) img = FITSReader.Read(fs);

        var opts = new BackgroundExtractor.Options(
            SamplesX:   Math.Clamp(samplesX   ?? 8, 4, 32),
            SamplesY:   Math.Clamp(samplesY   ?? 6, 4, 32),
            PolyDegree: Math.Clamp(polyDegree ?? 2, 1, 2));
        var corrected = BackgroundExtractor.Subtract(img.Data,
            img.Properties.Width, img.Properties.Height, opts);

        var outPath = BuildOutputPath(row, suffix: "bgsub");
        var outImg = new BaseImageData(corrected, img.Properties, img.MetaData);
        var kw = new List<KeyValuePair<string, string>> {
            new("BGSUB",   "Poly2D"),
            new("BGSAMPX", opts.SamplesX.ToString()),
            new("BGSAMPY", opts.SamplesY.ToString()),
            new("BGDEG",   opts.PolyDegree.ToString())
        };
        FITSWriter.Write(outImg, outPath, customKeywords: kw);
        _logger.LogInformation("Background-subtracted frame {Id} → {Path}", frameId, outPath);

        _processing.Invalidate(frameId);
        _ = Task.Run(() => _library.RescanAsync());
        return outPath;
    }

    private string? NoiseReductionSync(int frameId, int? radius) {
        var row = _library.GetById(frameId);
        if (row == null || !File.Exists(row.Path)) return null;

        BaseImageData img;
        using (var fs = File.OpenRead(row.Path)) img = FITSReader.Read(fs);

        var r = Math.Clamp(radius ?? 2, 1, 8);
        var blurred = GaussianBlur.Apply(img.Data,
            img.Properties.Width, img.Properties.Height, r);

        var outPath = BuildOutputPath(row, suffix: "nr");
        var outImg = new BaseImageData(blurred, img.Properties, img.MetaData);
        var kw = new List<KeyValuePair<string, string>> {
            new("NRMETHOD", "Gaussian"),
            new("NRRADIUS", r.ToString())
        };
        FITSWriter.Write(outImg, outPath, customKeywords: kw);
        _logger.LogInformation("Noise-reduced frame {Id} → {Path}", frameId, outPath);
        _processing.Invalidate(frameId);
        _ = Task.Run(() => _library.RescanAsync());
        return outPath;
    }

    private string? SharpenSync(int frameId, double? amount, int? radius, int? threshold) {
        var row = _library.GetById(frameId);
        if (row == null || !File.Exists(row.Path)) return null;

        BaseImageData img;
        using (var fs = File.OpenRead(row.Path)) img = FITSReader.Read(fs);

        var a = Math.Clamp(amount ?? 1.0, 0.1, 5.0);
        var r = Math.Clamp(radius ?? 2, 1, 8);
        var t = Math.Max(0, threshold ?? 0);
        var sharpened = UnsharpMask.Apply(img.Data,
            img.Properties.Width, img.Properties.Height, a, r, t);

        var outPath = BuildOutputPath(row, suffix: "sharp");
        var outImg = new BaseImageData(sharpened, img.Properties, img.MetaData);
        var kw = new List<KeyValuePair<string, string>> {
            new("SHARPEN",  "UnsharpMask"),
            new("SHARPAMT", a.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)),
            new("SHARPRAD", r.ToString()),
            new("SHARPTHR", t.ToString())
        };
        FITSWriter.Write(outImg, outPath, customKeywords: kw);
        _logger.LogInformation("Sharpened frame {Id} → {Path}", frameId, outPath);
        _processing.Invalidate(frameId);
        _ = Task.Run(() => _library.RescanAsync());
        return outPath;
    }

    private string BuildOutputPath(FrameRow row, string suffix) {
        var rigName = _profile.ActiveEquipmentProfile?.Name ?? "Default";
        var outRoot = _profile.Active.ImageOutputDir
            ?? throw new InvalidOperationException("ImageOutputDir not set.");
        var target = string.IsNullOrEmpty(row.Target) ? "Unknown" : row.Target;
        var dir = Path.Combine(outRoot, Sanitize(rigName), "processed", Sanitize(target));
        Directory.CreateDirectory(dir);
        var stem = Path.GetFileNameWithoutExtension(row.FileName);
        var path = Path.Combine(dir, $"{stem}_{suffix}.fits");
        int copy = 1;
        while (File.Exists(path)) path = Path.Combine(dir, $"{stem}_{suffix}_{copy++}.fits");
        return path;
    }

    private static string Sanitize(string s) {
        if (string.IsNullOrWhiteSpace(s)) return "Unknown";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(' ', '_');
    }
}
