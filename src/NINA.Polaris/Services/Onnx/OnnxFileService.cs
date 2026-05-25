using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;

namespace NINA.Polaris.Services.Onnx;

/// <summary>
/// Bridges FITS files on disk with the browser-side ONNX pipelines.
/// Two operations:
///
///   1) <see cref="LoadRawAsync"/> reads a source FITS, returns the
///      raw uint16 pixel buffer + dimensions. The browser feeds these
///      into the pipeline's normalization step (BGE / Denoise / Decon
///      each do their own normalization) and runs inference locally.
///
///   2) <see cref="SaveSiblingAsync"/> takes the post-inference uint16
///      pixels back and writes a sibling FITS next to the source —
///      <c>{stem}{suffix}.fits</c>, where suffix is "_bge" / "_denoise"
///      / "_decon" depending on the operation. Output dimensions must
///      match the source (pipelines preserve size).
///
/// FITS-only for v1; PNG/JPG/TIFF support comes when the editor
/// integration (GX-5) needs it. Keeping the surface narrow lets us
/// avoid the Skia color-space round-trip + sticking to the actual
/// data path GraXpert AI was trained on.
/// </summary>
public class OnnxFileService {
    private readonly ILogger<OnnxFileService> _logger;

    public OnnxFileService(ILogger<OnnxFileService> logger) {
        _logger = logger;
    }

    /// <summary>
    /// Decode a FITS file's pixel data into a raw uint16 little-endian
    /// byte buffer ready to ship to the browser. Returns null if the
    /// file isn't readable as FITS.
    /// </summary>
    public Task<RawPixels?> LoadRawAsync(string path, CancellationToken ct = default)
        => Task.Run<RawPixels?>(() => LoadRawSync(path), ct);

    private RawPixels? LoadRawSync(string path) {
        if (!File.Exists(path)) {
            _logger.LogWarning("OnnxFile load: not found {Path}", path);
            return null;
        }
        try {
            BaseImageData img;
            using (var fs = File.OpenRead(path)) {
                img = FITSReader.Read(fs);
            }
            int w = img.Properties.Width;
            int h = img.Properties.Height;
            // FITS is mono today — Bayer-pattern RGB is debayered
            // elsewhere; here we treat the raw plane as 1 channel and
            // let the browser pipeline replicate to 3 channels for the
            // model (BGE / Denoise expect NHWC channels=3, even on
            // mono inputs — same pattern GraXpert's Python does).
            int channels = 1;

            // ushort[] -> byte[] little-endian. Browser reads via
            // DataView + Uint16Array.from() on the LE-friendly typed
            // view, no per-byte JS loop needed.
            var bytes = new byte[w * h * channels * 2];
            Buffer.BlockCopy(img.Data, 0, bytes, 0, bytes.Length);

            return new RawPixels(
                Path:        path,
                Width:       w,
                Height:      h,
                Channels:    channels,
                BitDepth:    img.Properties.BitDepth,
                PixelsLE16:  bytes);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "OnnxFile load: FITS decode failed {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Write a sibling FITS file: <c>{sourceStem}{suffix}.fits</c> in the
    /// same directory as the source. Pixels are uint16 LE bytes (same
    /// layout LoadRawAsync emits), dimensions + channel count must
    /// match the source. Returns the written path or null on failure.
    /// </summary>
    public Task<string?> SaveSiblingAsync(
        string sourcePath, string suffix,
        byte[] pixelsLE16, int width, int height, int channels,
        CancellationToken ct = default)
        => Task.Run<string?>(() =>
            SaveSiblingSync(sourcePath, suffix, pixelsLE16, width, height, channels), ct);

    private string? SaveSiblingSync(
        string sourcePath, string suffix,
        byte[] pixelsLE16, int width, int height, int channels) {

        if (channels != 1) {
            // GX-3/4 may emit RGB outputs — when that ships we extend
            // FITSWriter to plane-stack or add NAXIS=3 paths. Today
            // BGE applied to a mono FITS still yields a mono result.
            _logger.LogWarning("OnnxFile save: channels={Ch} not supported yet (v1 mono-only)", channels);
            return null;
        }
        if (pixelsLE16.Length != width * height * 2) {
            _logger.LogWarning("OnnxFile save: pixel length mismatch (got {Got}, need {Want})",
                pixelsLE16.Length, width * height * 2);
            return null;
        }

        try {
            // Resolve the output path. We don't overwrite an existing
            // file — append _2, _3, ... so a curious user can re-run
            // BGE on the same source without losing the previous
            // result.
            var dir = Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrEmpty(dir)) dir = ".";
            var stem = Path.GetFileNameWithoutExtension(sourcePath);
            var outBase = Path.Combine(dir, stem + suffix);
            var outPath = outBase + ".fits";
            int copy = 1;
            while (File.Exists(outPath)) outPath = outBase + "_" + (++copy) + ".fits";

            // Inflate to ushort[] for the writer + carry an empty
            // MetaData. The browser doesn't see the original FITS
            // headers; preserving them precisely would require a
            // header copy step which is a follow-up.
            var data = new ushort[width * height];
            Buffer.BlockCopy(pixelsLE16, 0, data, 0, pixelsLE16.Length);

            var props = new ImageProperties {
                Width = width, Height = height,
                BitDepth = 16, IsBayered = false,
                BayerPattern = NINA.Core.Enum.BayerPatternEnum.None,
                Channels = 1,
            };
            var image = new BaseImageData(data, props);
            FITSWriter.Write(image, outPath);

            _logger.LogInformation("OnnxFile save: {Path}", outPath);
            return outPath;
        } catch (Exception ex) {
            _logger.LogError(ex, "OnnxFile save failed for {Source}{Suffix}", sourcePath, suffix);
            return null;
        }
    }
}

/// <summary>One source frame, decoded and ready for ONNX preprocessing.</summary>
public record RawPixels(
    string Path,
    int Width,
    int Height,
    int Channels,
    int BitDepth,
    byte[] PixelsLE16);
