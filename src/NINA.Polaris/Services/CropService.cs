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

using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;

namespace NINA.Polaris.Services;

/// <summary>
/// Rectangular crop on a FITS file. Reads the source, slices the pixel
/// buffer to the requested ROI, and writes a sibling FITS named
/// `{stem}_crop.fits`. Mono (NAXIS=2) and RGB plane-sequential (NAXIS=3)
/// are both honoured.
///
/// Pure I/O — no dependency on SkiaSharp, ONNX, or any external binary.
/// Synchronous on the wire (caller awaits the response): even a 24 Mpx
/// RGB master takes &lt; 300 ms on a Pi 5 because the only real work is
/// `Buffer.BlockCopy` per row × channels.
///
/// Typical workflow: user opens a stacked master, sees the dark borders
/// from registration / stacking misalignment, drags a rectangle past
/// those borders, clicks Crop. The clean output is then fed into
/// GraXpert BGE / Decon / Denoise (which all reject border noise as
/// "background gradient" otherwise).
/// </summary>
public class CropService {
    private readonly ILogger<CropService> _logger;

    public CropService(ILogger<CropService> logger) {
        _logger = logger;
    }

    public sealed record CropResult(string OutputPath, int Width, int Height, int Channels);

    /// <summary>
    /// Read <paramref name="sourcePath"/>, crop to (<paramref name="x"/>,
    /// <paramref name="y"/>, <paramref name="width"/>, <paramref name="height"/>),
    /// write `{stem}_crop.fits` next to the source. Coords are in image
    /// pixel space, top-left origin. Throws ArgumentException on
    /// out-of-bounds ROI so the caller surfaces a clear error instead of
    /// silently producing a tiny black slice.
    /// </summary>
    public CropResult CropFits(string sourcePath, int x, int y, int width, int height) {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("sourcePath is required", nameof(sourcePath));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source FITS not found", sourcePath);
        if (width <= 0 || height <= 0)
            throw new ArgumentException(
                $"Crop must have positive size, got {width}×{height}");

        BaseImageData src;
        using (var fs = File.OpenRead(sourcePath)) {
            src = FITSReader.Read(fs);
        }
        return CropCore(src, sourcePath, x, y, width, height);
    }

    /// <summary>
    /// Crop using a NORMALISED ROI (fractions of the image, 0..1, top-left
    /// origin). This is the resolution-independent entry point the web UI
    /// uses: the picker draws on a downscaled JPEG preview, so it cannot
    /// know the master's true pixel dimensions. By sending fractions and
    /// resolving them here against the actual FITS width/height, the crop
    /// lands exactly where the user drew regardless of preview scale.
    /// Out-of-range fractions are clamped to the image rather than
    /// rejected, since a near-edge drag legitimately produces 0 or 1.
    /// </summary>
    public CropResult CropFitsFraction(string sourcePath,
                                       double fx, double fy, double fw, double fh) {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("sourcePath is required", nameof(sourcePath));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source FITS not found", sourcePath);
        if (fw <= 0 || fh <= 0)
            throw new ArgumentException(
                $"Crop fraction must have positive size, got {fw}×{fh}");

        BaseImageData src;
        using (var fs = File.OpenRead(sourcePath)) {
            src = FITSReader.Read(fs);
        }
        int srcW = src.Properties.Width;
        int srcH = src.Properties.Height;

        // Fractions → pixels against the REAL dimensions, then clamp so a
        // drag that touched the edge (fraction 0 or 1, or a sub-pixel
        // overshoot from float math) still yields an in-bounds ROI.
        int x = (int)Math.Round(Math.Clamp(fx, 0.0, 1.0) * srcW);
        int y = (int)Math.Round(Math.Clamp(fy, 0.0, 1.0) * srcH);
        int w = (int)Math.Round(Math.Clamp(fw, 0.0, 1.0) * srcW);
        int h = (int)Math.Round(Math.Clamp(fh, 0.0, 1.0) * srcH);
        x = Math.Clamp(x, 0, Math.Max(0, srcW - 1));
        y = Math.Clamp(y, 0, Math.Max(0, srcH - 1));
        w = Math.Clamp(w, 1, srcW - x);
        h = Math.Clamp(h, 1, srcH - y);

        return CropCore(src, sourcePath, x, y, w, h);
    }

    /// <summary>
    /// Auto-crop: detect the largest fully-stacked (non-black) inner rectangle
    /// and crop to it, removing the ragged registration borders that stacking
    /// leaves on slightly misaligned subs. <paramref name="threshold"/> is the
    /// per-channel level below which a pixel counts as an uncovered border
    /// (default 0 = exact black, what integrators write for uncovered areas);
    /// raise it to also trim near-black partial-coverage edges.
    /// <paramref name="margin"/> shrinks the detected rectangle inward by N px
    /// as a safety against low-SNR partial edges. Writes `{stem}_crop.fits`
    /// like the manual crop. Returns the detected geometry.
    /// </summary>
    public CropResult AutoCropFits(string sourcePath, int threshold = 0, int margin = 0) {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("sourcePath is required", nameof(sourcePath));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source FITS not found", sourcePath);

        BaseImageData src;
        using (var fs = File.OpenRead(sourcePath)) {
            src = FITSReader.Read(fs);
        }
        int channels = src.Properties.Channels == 3 ? 3 : 1;
        var r = AutoCrop.FindContentRect(src.Data, src.Properties.Width, src.Properties.Height,
            channels, threshold, margin);
        _logger.LogInformation(
            "AutoCrop: {Src} ({SrcW}×{SrcH}) → content ({X},{Y} {W}×{H}) thr={Thr} margin={M}",
            sourcePath, src.Properties.Width, src.Properties.Height, r.X, r.Y, r.Width, r.Height,
            threshold, margin);
        return CropCore(src, sourcePath, r.X, r.Y, r.Width, r.Height);
    }

    private CropResult CropCore(BaseImageData src, string sourcePath,
                                int x, int y, int width, int height) {
        int srcW = src.Properties.Width;
        int srcH = src.Properties.Height;
        int channels = src.Properties.Channels == 3 ? 3 : 1;

        // Clamp + validate. We intentionally reject ROIs that extend past
        // the image edge rather than silently truncating; a user who set
        // a 4000-wide crop on a 3840-wide image needs to know the picker
        // overshot, not get a smaller-than-requested output.
        if (x < 0 || y < 0 || x + width > srcW || y + height > srcH) {
            throw new ArgumentException(
                $"Crop ({x},{y} {width}×{height}) extends outside image " +
                $"bounds ({srcW}×{srcH})");
        }

        // Slice the buffer plane-by-plane. Plane-sequential layout means
        // pixel (px, py) on channel c sits at index (c*srcW*srcH) +
        // py*srcW + px. Copy row by row into the output plane.
        long outPlane = (long)width * height;
        long outTotal = outPlane * channels;
        var outPixels = new ushort[outTotal];
        for (int c = 0; c < channels; c++) {
            long srcPlaneBase = (long)c * srcW * srcH;
            long dstPlaneBase = (long)c * outPlane;
            for (int row = 0; row < height; row++) {
                long srcRow = srcPlaneBase + (long)(y + row) * srcW + x;
                long dstRow = dstPlaneBase + (long)row * width;
                Array.Copy(src.Data, srcRow, outPixels, dstRow, width);
            }
        }

        // Build new ImageProperties + preserve metadata (DATE-OBS, GAIN,
        // OBJECT, TELESCOPE, etc.) from the source so the cropped output
        // is still a usable scientific FITS, not a dimension-only blob.
        var newProps = src.Properties with {
            Width = width,
            Height = height
            // Channels is preserved automatically by the with-record copy
        };
        var dst = new BaseImageData(outPixels, newProps, src.MetaData);

        // Output path: sibling, suffix _crop.fits, replace any existing
        // file (idempotent re-runs).
        var dir = Path.GetDirectoryName(sourcePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var outPath = Path.Combine(dir, stem + "_crop.fits");

        FITSWriter.Write(dst, outPath, customKeywords: new[] {
            new KeyValuePair<string, string>("CROPSRCX", x.ToString()),
            new KeyValuePair<string, string>("CROPSRCY", y.ToString()),
            new KeyValuePair<string, string>("CROPSRCW", srcW.ToString()),
            new KeyValuePair<string, string>("CROPSRCH", srcH.ToString())
        });

        _logger.LogInformation(
            "Crop: {Src} ({SrcW}×{SrcH} ch={Ch}) → {Out} (x={X} y={Y} {W}×{H})",
            sourcePath, srcW, srcH, channels, outPath, x, y, width, height);

        return new CropResult(outPath, width, height, channels);
    }
}