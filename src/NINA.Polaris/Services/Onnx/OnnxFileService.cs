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
            // GX-9: RGB FITS (NAXIS=3 / NAXIS3=3) loads with all
            // three planes plane-sequentially (R...G...B). Mono
            // FITS stays at 1 channel and the browser pipeline
            // replicates to 3 for the model. Anything other than
            // 1 or 3 collapses to mono — same path FITSReader
            // already takes.
            int channels = img.Properties.Channels == 3 ? 3 : 1;

            // ushort[] -> byte[] little-endian. Browser reads via
            // DataView + Uint16Array.from() on the LE-friendly typed
            // view, no per-byte JS loop needed.
            var bytes = new byte[(long)w * h * channels * 2];
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

        if (channels != 1 && channels != 3) {
            _logger.LogWarning("OnnxFile save: channels={Ch} unsupported (only 1 or 3)", channels);
            return null;
        }
        long needBytes = (long)width * height * channels * 2;
        if (pixelsLE16.LongLength != needBytes) {
            _logger.LogWarning("OnnxFile save: pixel length mismatch (got {Got}, need {Want} for {W}x{H}x{C})",
                pixelsLE16.LongLength, needBytes, width, height, channels);
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
            // GX-9: for RGB, pixels arrive plane-sequential (R...G...B)
            // matching what FITSReader produces and what FITSWriter now
            // expects — no transpose needed here.
            var data = new ushort[(long)width * height * channels];
            Buffer.BlockCopy(pixelsLE16, 0, data, 0, pixelsLE16.Length);

            var props = new ImageProperties {
                Width = width, Height = height,
                BitDepth = 16, IsBayered = false,
                BayerPattern = NINA.Core.Enum.BayerPatternEnum.None,
                Channels = channels,
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
