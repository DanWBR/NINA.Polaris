using System.Collections.Concurrent;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Polaris.Services.Studio;

namespace NINA.Polaris.Services;

/// <summary>
/// Per-frame calibration pass for the live stacker (LSPP-2). For
/// each incoming frame the service:
///
/// 1. Looks at the frame's gain / exposure / filter / binning metadata
/// 2. Picks matching masters (dark, flat, bias) from the FrameLibrary --
///    either the auto-match best fit (default) or the explicit override
///    ids configured in <see cref="LiveStackPreProcSettings"/>.
/// 3. Lazily loads + caches the master buffers ONCE per session,
///    keyed by (gain, exposure, filter, binning). A 24MP master ushort[]
///    is ~48 MB so re-reading from disk every frame would be wasteful;
///    keeping them in RAM costs ~150 MB peak (dark+bias+flat).
/// 4. Calls <see cref="CalibrationMath.CalibratePixels"/> to produce the
///    calibrated buffer.
///
/// Failure mode is gracious by design: any exception during master
/// load or calibration math returns Success=false and the raw frame
/// pixels in the result. LiveStackingService falls back to feeding the
/// raw frame into the stack so the session keeps going -- a missing
/// master shouldn't abort a 3-hour capture.
///
/// BGE is NOT handled here. BGE runs on the client (WASM) when in
/// MetricsOnly mode; see app.js _stackViaWasm for the client-side hook.
/// </summary>
public class LiveStackPreProcessor {
    private readonly FrameLibraryService _library;
    private readonly ILogger<LiveStackPreProcessor> _logger;

    private readonly ConcurrentDictionary<MasterKey, CachedMasterSet> _cache = new();

    public LiveStackPreProcessor(FrameLibraryService library,
                                  ILogger<LiveStackPreProcessor> logger) {
        _library = library;
        _logger = logger;
    }

    /// <summary>Clear the master cache. Called by LiveStackingService.Reset
    /// so a switched-target session re-resolves masters fresh (the
    /// previous target may have used different exposure / filter
    /// combinations whose cached entries are now dead weight).</summary>
    public void Reset() {
        _cache.Clear();
        _logger.LogDebug("LiveStackPreProcessor cache cleared");
    }

    /// <summary>Apply calibration to a single frame. Synchronous in
    /// spirit but exposed as Task for symmetry with the live-stack
    /// frame handler chain. Returns the raw pixel array on the result
    /// when calibration is disabled OR fails -- the caller never sees
    /// a null buffer.</summary>
    public Task<PreProcessResult> ApplyAsync(IImageData frame,
            LiveStackPreProcSettings settings, CancellationToken ct = default) {
        if (!settings.CalibrationEnabled) {
            // Pre-processing disabled -> pass through unchanged.
            return Task.FromResult(new PreProcessResult(
                Success: true, Pixels: frame.Data,
                MasterDarkUsed: null, MasterFlatUsed: null, MasterBiasUsed: null,
                Error: null));
        }

        try {
            var key = KeyOf(frame);
            var masters = _cache.GetOrAdd(key, k => ResolveMasters(k, settings));

            if (masters.Dark == null && masters.Flat == null && masters.Bias == null) {
                // Auto-match found nothing usable; pass through.
                _logger.LogDebug(
                    "Live-stack calibration: no matching masters for gain={Gain} exp={Exp}s filter='{Filter}' binning={Bin}",
                    key.Gain, key.ExposureSec, key.Filter, key.BinningX);
                return Task.FromResult(new PreProcessResult(
                    Success: true, Pixels: frame.Data,
                    MasterDarkUsed: null, MasterFlatUsed: null, MasterBiasUsed: null,
                    Error: "no matching masters"));
            }

            var calibrated = CalibrationMath.CalibratePixels(
                frame.Data,
                dark: masters.Dark,
                bias: masters.Bias,
                flat: masters.Flat);
            return Task.FromResult(new PreProcessResult(
                Success: true, Pixels: calibrated,
                MasterDarkUsed: masters.DarkName,
                MasterFlatUsed: masters.FlatName,
                MasterBiasUsed: masters.BiasName,
                Error: null));
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Live-stack calibration threw, falling back to raw frame");
            return Task.FromResult(new PreProcessResult(
                Success: false, Pixels: frame.Data,
                MasterDarkUsed: null, MasterFlatUsed: null, MasterBiasUsed: null,
                Error: ex.Message));
        }
    }

    /// <summary>Build the cache key from a frame's metadata. Binning
    /// comes from the camera info; if missing we default to 1x1 which
    /// matches the master matchers' expectations.</summary>
    private static MasterKey KeyOf(IImageData frame) {
        var meta = frame.MetaData;
        var filter = meta.Exposure.Filter ?? meta.FilterWheel.Filter ?? "";
        return new MasterKey(
            Gain: meta.Camera.Gain,
            ExposureSec: meta.Exposure.ExposureTime,
            Filter: filter,
            BinningX: meta.Camera.BinX <= 0 ? (short)1 : meta.Camera.BinX);
    }

    /// <summary>Resolve which masters to load for a given key. Honors
    /// the override ids on settings first, then falls back to
    /// auto-match. Loads buffers from disk (potentially expensive --
    /// only happens once per key per session thanks to the cache).</summary>
    private CachedMasterSet ResolveMasters(MasterKey key, LiveStackPreProcSettings settings) {
        // Snapshot the library so we can search by-specs in-process.
        var all = _library.Query(new FrameQuery(
            Type: null, Filter: null, Target: null,
            DateFrom: null, DateTo: null, Limit: 5000, Offset: 0));
        var darks = all.Where(f => string.Equals(f.ImageType, "MASTERDARK",
            StringComparison.OrdinalIgnoreCase)).ToList();
        var flats = all.Where(f => string.Equals(f.ImageType, "MASTERFLAT",
            StringComparison.OrdinalIgnoreCase)).ToList();
        var biases = all.Where(f => string.Equals(f.ImageType, "MASTERBIAS",
            StringComparison.OrdinalIgnoreCase)).ToList();
        var darkFlats = all.Where(f => string.Equals(f.ImageType, "MASTERDARKFLAT",
            StringComparison.OrdinalIgnoreCase)).ToList();

        // Pick master rows (overrides win, otherwise auto-match).
        var darkRow = settings.MasterDarkOverrideId.HasValue
            ? _library.GetById(settings.MasterDarkOverrideId.Value)
            : CalibrationMath.FindNearestDark(darks, key.ExposureSec, key.Gain);
        var flatRow = settings.MasterFlatOverrideId.HasValue
            ? _library.GetById(settings.MasterFlatOverrideId.Value)
            : CalibrationMath.FindMatchingFlat(flats, key.Filter, key.Gain);
        // Bias only useful when no dark; same precedence as the batch
        // CalibrationService -- dark already includes the bias signal.
        var biasRow = settings.MasterBiasOverrideId.HasValue
            ? _library.GetById(settings.MasterBiasOverrideId.Value)
            : (darkRow == null
                ? CalibrationMath.FindMatchingBias(biases, key.Gain)
                : null);

        // Load buffers from disk. Any failure (file deleted, dimensions
        // wrong, FITS unreadable) propagates up to ApplyAsync which
        // turns it into a graceful fallback.
        ushort[]? darkBuf = darkRow != null ? LoadFitsPixels(darkRow.Path) : null;
        ushort[]? biasBuf = (darkRow == null && biasRow != null) ? LoadFitsPixels(biasRow.Path) : null;

        (double[] norm, double mean)? flat = null;
        if (flatRow != null) {
            var flatImg = LoadFitsImage(flatRow.Path);
            // Flat calibrator: prefer a dark-flat sized for the flat's
            // own exposure, then fall back to bias, then nothing.
            BaseImageData? calImg = null;
            var calRow = CalibrationMath.FindNearestDark(darkFlats,
                flatImg.MetaData.Exposure.ExposureTime, flatImg.MetaData.Camera.Gain);
            if (calRow != null) calImg = LoadFitsImage(calRow.Path);
            else if (biasRow != null) calImg = LoadFitsImage(biasRow.Path);
            flat = CalibrationMath.NormalizeFlat(flatImg, calImg);
        }

        _logger.LogInformation(
            "Live-stack pre-proc cached masters for gain={Gain} exp={Exp}s filter='{Filter}': " +
            "dark={Dark} flat={Flat} bias={Bias}",
            key.Gain, key.ExposureSec, key.Filter,
            darkRow?.FileName ?? "-", flatRow?.FileName ?? "-", biasRow?.FileName ?? "-");

        return new CachedMasterSet(
            Dark: darkBuf, Bias: biasBuf, Flat: flat,
            DarkName: darkRow?.FileName,
            FlatName: flatRow?.FileName,
            BiasName: biasRow?.FileName);
    }

    private static ushort[] LoadFitsPixels(string path) {
        if (!File.Exists(path))
            throw new InvalidOperationException($"Master missing on disk: {path}");
        using var fs = File.OpenRead(path);
        var img = FITSReader.Read(fs);
        return img.Data;
    }

    private static BaseImageData LoadFitsImage(string path) {
        if (!File.Exists(path))
            throw new InvalidOperationException($"Master missing on disk: {path}");
        using var fs = File.OpenRead(path);
        return FITSReader.Read(fs);
    }

    private record MasterKey(int Gain, double ExposureSec, string Filter, short BinningX);

    private record CachedMasterSet(
        ushort[]? Dark, ushort[]? Bias, (double[] norm, double mean)? Flat,
        string? DarkName, string? FlatName, string? BiasName);
}

/// <summary>Outcome of one pre-processing pass. Pixels is ALWAYS
/// populated (raw frame pixels on failure / disabled), so the caller
/// can splice the result into the rest of the live-stack pipeline
/// without null-checking.</summary>
public record PreProcessResult(
    bool Success,
    ushort[] Pixels,
    string? MasterDarkUsed,
    string? MasterFlatUsed,
    string? MasterBiasUsed,
    string? Error);
