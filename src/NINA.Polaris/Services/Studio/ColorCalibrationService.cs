using System.Collections.Concurrent;
using System.Globalization;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;

namespace NINA.Polaris.Services.Studio;

/// <summary>
/// Siril-style color calibration on an RGB FITS master. Three modes,
/// all writing a sibling FITS so the calibration step is non-
/// destructive and slots cleanly between channel combine (CC-1..3)
/// and AI cleanup (GraXpert ONNX) in the mono workflow:
///
///   BgNeutral    Background neutralisation. Subtract per-channel
///                offsets so a chosen background region (auto = the
///                darkest 5% of pixels by luminance; patch = a
///                user-drawn ROI) becomes neutral grey.
///   Manual       BG neutralisation + white-reference scaling.
///                User picks a second patch containing a known-white
///                target (G2V star, galaxy core, neutral nebula
///                region); the service computes per-channel gains
///                that bring the mean of the white patch to neutral.
///   Photometric  Plate-solved + catalog-driven. Photometric Color
///                Calibration (PCC) the Siril way; ships in CCALB-3.
///                Throws NotImplementedException in CCALB-1 / 2.
///
/// Mirrors the job/progress shape of <see cref="ChannelCombineService"/>
/// so the STUDIO modal's progress block, polling cadence, and UI
/// state map across without per-tool work.
/// </summary>
public class ColorCalibrationService {
    private readonly FrameLibraryService _library;
    private readonly ProfileService _profile;
    private readonly ILogger<ColorCalibrationService> _logger;
    private readonly ConcurrentDictionary<string, ColorCalibrationProgress> _jobs = new();

    public ColorCalibrationService(FrameLibraryService library, ProfileService profile,
                                   ILogger<ColorCalibrationService> logger) {
        _library = library;
        _profile = profile;
        _logger = logger;
    }

    public static class Modes {
        public const string BgNeutral   = "bg";
        public const string Manual      = "manual";
        public const string Photometric = "pcc";
    }

    /// <summary>
    /// Patch ROI in image pixel coordinates. (X, Y) is the top-left
    /// corner; W, H are the side lengths. Validated and clamped to
    /// the frame bounds at request time.
    /// </summary>
    public record PatchRoi(int X, int Y, int W, int H);

    public record ColorCalibrationRequest(
        int FrameId,
        string Mode,                       // see Modes.*
        // Sample mode for BG step:
        //   "auto"  -> lowest-luminance 5% of pixels
        //   "patch" -> use BgPatch (must be non-null)
        string BgSample = "auto",
        PatchRoi? BgPatch = null,
        // Manual mode also needs a white-reference patch:
        PatchRoi? WhitePatch = null);

    public string StartJob(ColorCalibrationRequest req) {
        if (req == null) throw new ArgumentNullException(nameof(req));
        if (string.Equals(req.Mode, Modes.Manual, StringComparison.OrdinalIgnoreCase)
            && req.WhitePatch == null) {
            throw new ArgumentException(
                "Manual color calibration requires a white-reference patch.");
        }
        if (string.Equals(req.BgSample, "patch", StringComparison.OrdinalIgnoreCase)
            && req.BgPatch == null) {
            throw new ArgumentException(
                "BgSample='patch' requires a BgPatch ROI.");
        }
        var jobId = Guid.NewGuid().ToString("N")[..8];
        _jobs[jobId] = new ColorCalibrationProgress {
            JobId = jobId,
            Mode = req.Mode,
            InProgress = true,
            Stage = "queued",
        };
        _ = Task.Run(() => RunJob(jobId, req));
        return jobId;
    }

    public ColorCalibrationProgress? GetStatus(string jobId)
        => _jobs.TryGetValue(jobId, out var p) ? p : null;

    private void RunJob(string jobId, ColorCalibrationRequest req) {
        try {
            // ── Phase 1: load + validate ─────────────────────────────
            _jobs[jobId] = _jobs[jobId] with { Stage = "loading" };
            var row = _library.GetById(req.FrameId);
            if (row == null || !File.Exists(row.Path)) {
                throw new InvalidOperationException(
                    $"Frame id {req.FrameId} not found in the library or missing on disk.");
            }
            BaseImageData img;
            using (var fs = File.OpenRead(row.Path)) {
                img = FITSReader.Read(fs);
            }
            if (img.Properties.Channels != 3) {
                throw new InvalidOperationException(
                    $"Color calibration requires a 3-channel RGB FITS (got {img.Properties.Channels} channels). " +
                    $"Run Channel combine on per-filter masters first.");
            }
            int W = img.Properties.Width, H = img.Properties.Height;

            // ── Phase 2: compute per-channel offsets + gains by mode ─
            _jobs[jobId] = _jobs[jobId] with { Stage = "computing" };
            double[] offsets = new double[3];
            double[] gains   = new double[] { 1, 1, 1 };
            string prefix;

            switch ((req.Mode ?? "").ToLowerInvariant()) {
                case Modes.BgNeutral:
                    // BG-only mode: preserve background brightness at
                    // the dimmest channel's level (zeroBackground=false).
                    offsets = ColorCalibrationMath.ComputeBgOffsets(
                        img.Data, W, H, req.BgSample, req.BgPatch,
                        zeroBackground: false);
                    prefix = "bgneu";
                    break;
                case Modes.Manual:
                    // Manual mode: push BG to zero across channels so
                    // the white-reference gain step keeps BG neutral.
                    offsets = ColorCalibrationMath.ComputeBgOffsets(
                        img.Data, W, H, req.BgSample, req.BgPatch,
                        zeroBackground: true);
                    gains = ColorCalibrationMath.ComputeWhiteGains(
                        img.Data, W, H, req.WhitePatch!, offsets);
                    prefix = "ccal";
                    break;
                case Modes.Photometric:
                    throw new NotImplementedException(
                        "Photometric color calibration ships in CCALB-3. " +
                        "Use BG neutralisation or Manual mode for now.");
                default:
                    throw new ArgumentException(
                        $"Unknown color-calibration mode '{req.Mode}'. " +
                        "Expected one of: bg, manual, pcc.");
            }

            // ── Phase 3: apply per-pixel ─────────────────────────────
            _jobs[jobId] = _jobs[jobId] with { Stage = "applying" };
            int planeSize = W * H;
            var output = new ushort[planeSize * 3];
            for (int c = 0; c < 3; c++) {
                int baseIdx = c * planeSize;
                double off = offsets[c];
                double g = gains[c];
                for (int i = 0; i < planeSize; i++) {
                    double v = (img.Data[baseIdx + i] - off) * g;
                    output[baseIdx + i] = (ushort)Math.Clamp(v, 0, 65535);
                }
            }

            // ── Phase 4: write FITS ──────────────────────────────────
            _jobs[jobId] = _jobs[jobId] with { Stage = "writing" };
            var outPath = WriteOutput(output, W, H, img.Properties.BitDepth,
                row.Path, row.Target, prefix, req, offsets, gains);

            // ── Phase 5: reindex ─────────────────────────────────────
            _logger.LogInformation(
                "Color calibration {Job} ({Mode}): wrote {Path} " +
                "(offsets R={Or:F1} G={Og:F1} B={Ob:F1}, gains R={Gr:F3} G={Gg:F3} B={Gb:F3})",
                jobId, req.Mode, outPath, offsets[0], offsets[1], offsets[2],
                gains[0], gains[1], gains[2]);
            _ = Task.Run(() => _library.RescanAsync());

            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false,
                Stage = "done",
                OutputPath = outPath,
                OffsetR = offsets[0], OffsetG = offsets[1], OffsetB = offsets[2],
                GainR = gains[0], GainG = gains[1], GainB = gains[2],
            };
        } catch (Exception ex) {
            _logger.LogError(ex, "Color calibration job {JobId} failed", jobId);
            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false,
                Stage = "error",
                Error = ex.Message,
            };
        }
    }

    private string WriteOutput(ushort[] data, int W, int H, int bitDepth,
            string sourcePath, string target, string prefix,
            ColorCalibrationRequest req, double[] offsets, double[] gains) {
        // Sibling FITS: same directory as the source, suffix appended
        // to the stem. Keeps the calibrated output next to the
        // un-calibrated source so a diff is one-folder away (same
        // convention the GraXpert AI step uses for its outputs).
        var dir = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrEmpty(dir)) dir = ".";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var outBase = Path.Combine(dir, stem + "_" + prefix);
        var outPath = outBase + ".fits";
        int copy = 1;
        while (File.Exists(outPath)) outPath = outBase + "_" + (++copy) + ".fits";

        var props = new ImageProperties {
            Width = W, Height = H, BitDepth = bitDepth,
            BayerPattern = NINA.Core.Enum.BayerPatternEnum.None,
            IsBayered = false,
            Channels = 3,
        };
        var meta = new ImageMetaData {
            CreationTime = DateTime.UtcNow,
            Camera   = new ImageMetaData.CameraInfo(),
            Telescope = new ImageMetaData.TelescopeInfo(),
            Observer = new ImageMetaData.ObserverInfo(),
            Target   = new ImageMetaData.TargetInfo {
                Name = string.IsNullOrEmpty(target) ? "Unknown" : target,
            },
            Exposure = new ImageMetaData.ExposureInfo {
                Filter = "RGB",
                ImageType = "MASTERCAL",
            },
        };
        var masterData = new BaseImageData(data, props, meta);

        var customKeywords = new List<KeyValuePair<string, string>> {
            new("CCAL_MOD", req.Mode),
            new("CCAL_OFR", Fmt(offsets[0])),
            new("CCAL_OFG", Fmt(offsets[1])),
            new("CCAL_OFB", Fmt(offsets[2])),
            new("CCAL_GNR", Fmt(gains[0])),
            new("CCAL_GNG", Fmt(gains[1])),
            new("CCAL_GNB", Fmt(gains[2])),
            new("CCAL_SRC", Path.GetFileName(sourcePath)),
        };

        FITSWriter.Write(masterData, outPath, customKeywords: customKeywords);
        return outPath;
    }

    private static string Fmt(double d)
        => d.ToString("0.####", CultureInfo.InvariantCulture);
}

public record ColorCalibrationProgress {
    public string JobId { get; init; } = "";
    public string Mode { get; init; } = "";
    public bool InProgress { get; init; }
    public string Stage { get; init; } = "";
    public string? Error { get; init; }
    public string? OutputPath { get; init; }
    public double OffsetR { get; init; }
    public double OffsetG { get; init; }
    public double OffsetB { get; init; }
    public double GainR { get; init; } = 1;
    public double GainG { get; init; } = 1;
    public double GainB { get; init; } = 1;
    public int MatchedStars { get; init; }   // PCC only
}
