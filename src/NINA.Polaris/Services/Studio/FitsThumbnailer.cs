using NINA.Image.FileFormat.FITS;
using SkiaSharp;

namespace NINA.Polaris.Services.Studio;

/// <summary>
/// Shared FITS-and-FITS-like-bitmap → JPEG path. Extracted from
/// <see cref="FrameLibraryService"/> so the new FILES tab can render
/// the same auto-stretched preview for any FITS file on disk —
/// including files that were never indexed by STUDIO (an arbitrary
/// master coming back from PixInsight, for example).
///
/// Output is grayscale for single-plane (mono) input or RGB for
/// NAXIS=3 colour cubes (PixInsight / Siril / GraXpert export
/// convention). Bayer-pattern frames are still rendered as luminance
/// — full debayer is a STUDIO pipeline step.
/// </summary>
public static class FitsThumbnailer {

    /// <summary>
    /// Read a FITS file from disk, auto-stretch, downsample to
    /// <paramref name="maxDim"/> px on the long side, encode JPEG.
    /// Returns the raw JPEG bytes — caller decides whether to cache
    /// them to disk or stream them straight to the response.
    /// </summary>
    public static byte[] RenderJpegFromPath(string fitsPath, int maxDim = 256, int quality = 85,
                                            string? stretchFromPath = null) {
        using var fs = File.OpenRead(fitsPath);
        var img = FITSReader.Read(fs);
        var w = img.Properties.Width;
        var h = img.Properties.Height;
        var bits = img.Properties.BitDepth;
        // GX-12c: when stretchFromPath is set, pin the stretch params to
        // whatever the REFERENCE file computes. Lets the comparator put
        // a denoised/decon'd sibling next to the original without each
        // side's auto-stretch independently re-balancing the histogram
        // (which produced visually wildly different colours for what is
        // basically the same scene with slightly less noise).
        var overrideParams = (stretchFromPath != null)
            ? ComputeParamsFor(stretchFromPath, img.Properties.IsColor ? 3 : 1, bits)
            : null;
        if (img.Properties.IsColor) {
            return RenderJpegFromRgbPlanes(img.Data, w, h, bits, maxDim, quality, overrideParams);
        }
        return RenderJpegFromBuffer(img.Data, w, h, bits, maxDim, quality,
            overrideParams != null && overrideParams.Length > 0 ? overrideParams[0] : null);
    }

    /// <summary>
    /// Read a reference FITS and compute per-channel auto-stretch params
    /// without rendering anything. Returns length-1 (mono) or length-3
    /// (RGB) array. Silently returns null on any error so the caller can
    /// fall back to self-computed params instead of failing the render.
    /// </summary>
    private static NINA.Image.ImageAnalysis.AutoStretch.StretchParams[]? ComputeParamsFor(
            string refPath, int expectedChannels, int bitDepth) {
        try {
            if (!File.Exists(refPath)) return null;
            using var fs = File.OpenRead(refPath);
            var img = FITSReader.Read(fs);
            int rw = img.Properties.Width;
            int rh = img.Properties.Height;
            int plane = rw * rh;
            // Mono ref + RGB target (or vice versa) is a degenerate case;
            // just use the ref's first plane and broadcast.
            int refChannels = img.Properties.IsColor && img.Data.Length >= plane * 3 ? 3 : 1;
            var result = new NINA.Image.ImageAnalysis.AutoStretch.StretchParams[expectedChannels];
            for (int c = 0; c < expectedChannels; c++) {
                int srcC = Math.Min(c, refChannels - 1);
                var chan = new ushort[plane];
                Array.Copy(img.Data, plane * srcC, chan, 0, plane);
                result[c] = NINA.Image.ImageAnalysis.AutoStretch.ComputeAutoStretchParams(
                    chan, rw, rh, bitDepth);
            }
            return result;
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Render directly from an in-memory grayscale ushort buffer.
    /// Used both by <see cref="RenderJpegFromPath"/> (mono branch) and
    /// by callers that already have decoded pixel data (live-stack
    /// preview, future XISF reader, etc.).
    /// </summary>
    public static byte[] RenderJpegFromBuffer(ushort[] pixels, int width, int height,
                                              int bitDepth, int maxDim = 256, int quality = 85,
                                              NINA.Image.ImageAnalysis.AutoStretch.StretchParams? overrideParams = null) {
        // Auto-stretch lives in NINA.Image (vendored portable copy).
        // GX-12c: when overrideParams is set, skip the auto-stretch
        // computation and apply the caller-supplied black/mid/white.
        // Used by the comparator to pin both BEFORE and AFTER to the
        // same histogram.
        byte[] stretched = overrideParams != null
            ? NINA.Image.ImageAnalysis.AutoStretch.ApplyManual(
                pixels, width, height,
                overrideParams.Black, overrideParams.Mid, overrideParams.White, bitDepth)
            : NINA.Image.ImageAnalysis.AutoStretch.Apply(pixels, width, height, bitDepth);

        // Wrap the byte[] as a Gray8 SKBitmap, copy so we own the
        // backing storage, then resize. JPEG encoders are flaky with
        // Gray8 input, so the final step is a round-trip via Rgba8888.
        using var gray = new SKBitmap(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        unsafe {
            fixed (byte* p = stretched) {
                gray.SetPixels((IntPtr)p);
            }
        }

        using var grayCopy = gray.Copy();
        double scale = (double)maxDim / Math.Max(grayCopy.Width, grayCopy.Height);
        // Caller passes maxDim=int.MaxValue (or any larger-than-the-source
        // value) to skip downsampling — useful for full-res preview.
        if (scale > 1) scale = 1;
        int newW = Math.Max(1, (int)Math.Round(grayCopy.Width * scale));
        int newH = Math.Max(1, (int)Math.Round(grayCopy.Height * scale));

        // Pick the source bitmap for the final RGBA pass. When we
        // *do* need to resize, we own a fresh SKBitmap; otherwise we
        // draw straight from grayCopy. The `needsResize` flag drives
        // the dispose path below — aliasing two `using` variables to
        // the same SkiaSharp handle double-frees the native object,
        // which presented as silent all-black JPEG output.
        bool needsResize = newW != grayCopy.Width || newH != grayCopy.Height;
        SKBitmap? resized = needsResize
            ? grayCopy.Resize(
                new SKImageInfo(newW, newH, SKColorType.Gray8, SKAlphaType.Opaque),
                SKSamplingOptions.Default)
            : null;
        try {
            var drawSrc = resized ?? grayCopy;
            using var rgb = new SKBitmap(newW, newH, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(rgb)) canvas.DrawBitmap(drawSrc, 0, 0);
            using var data = rgb.Encode(SKEncodedImageFormat.Jpeg, quality);
            return data.ToArray();
        } finally {
            resized?.Dispose();
        }
    }

    /// <summary>
    /// Render an RGB colour preview from a plane-sequential ushort
    /// buffer (R plane, then G plane, then B plane — each
    /// width*height long). Each channel is auto-stretched
    /// independently so a stacked OSC integration looks natural
    /// (per-channel MTF is what most viewers do for FITS RGB cubes).
    /// </summary>
    public static byte[] RenderJpegFromRgbPlanes(ushort[] pixels, int width, int height,
                                                 int bitDepth, int maxDim = 256, int quality = 85,
                                                 NINA.Image.ImageAnalysis.AutoStretch.StretchParams[]? overrideParams = null) {
        int planeSize = width * height;
        if (pixels.Length < planeSize * 3)
            // Defensive — caller mis-claimed colour. Fall back to mono
            // so the user at least sees something instead of a crash.
            return RenderJpegFromBuffer(pixels, width, height, bitDepth, maxDim, quality,
                overrideParams != null && overrideParams.Length > 0 ? overrideParams[0] : null);

        // Stretch each channel separately. The vendored AutoStretch
        // doesn't take a stride/offset, so we slice into temporary
        // buffers. The slices are short-lived; for a 24 MP frame we're
        // talking ~48 MB peak which the RPi handles fine.
        var r = new ushort[planeSize];
        var g = new ushort[planeSize];
        var b = new ushort[planeSize];
        Array.Copy(pixels, planeSize * 0, r, 0, planeSize);
        Array.Copy(pixels, planeSize * 1, g, 0, planeSize);
        Array.Copy(pixels, planeSize * 2, b, 0, planeSize);

        // GX-12c: when overrideParams is set, apply the reference-file's
        // per-channel params instead of recomputing. Each channel still
        // uses its own R/G/B param so colour balance survives.
        byte[] rs, gs, bs;
        if (overrideParams != null && overrideParams.Length >= 3) {
            var ps = overrideParams;
            rs = NINA.Image.ImageAnalysis.AutoStretch.ApplyManual(r, width, height,
                ps[0].Black, ps[0].Mid, ps[0].White, bitDepth);
            gs = NINA.Image.ImageAnalysis.AutoStretch.ApplyManual(g, width, height,
                ps[1].Black, ps[1].Mid, ps[1].White, bitDepth);
            bs = NINA.Image.ImageAnalysis.AutoStretch.ApplyManual(b, width, height,
                ps[2].Black, ps[2].Mid, ps[2].White, bitDepth);
        } else {
            rs = NINA.Image.ImageAnalysis.AutoStretch.Apply(r, width, height, bitDepth);
            gs = NINA.Image.ImageAnalysis.AutoStretch.Apply(g, width, height, bitDepth);
            bs = NINA.Image.ImageAnalysis.AutoStretch.Apply(b, width, height, bitDepth);
        }

        // Interleave into RGBA8888 for Skia. Alpha is opaque.
        var rgba = new byte[planeSize * 4];
        for (int i = 0; i < planeSize; i++) {
            int o = i * 4;
            rgba[o + 0] = rs[i];     // R
            rgba[o + 1] = gs[i];     // G
            rgba[o + 2] = bs[i];     // B
            rgba[o + 3] = 255;       // A
        }

        using var color = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        unsafe {
            fixed (byte* p = rgba) {
                color.SetPixels((IntPtr)p);
            }
        }
        using var colorCopy = color.Copy();   // own the backing storage

        double scale = (double)maxDim / Math.Max(colorCopy.Width, colorCopy.Height);
        if (scale > 1) scale = 1;
        int newW = Math.Max(1, (int)Math.Round(colorCopy.Width * scale));
        int newH = Math.Max(1, (int)Math.Round(colorCopy.Height * scale));

        bool needsResize = newW != colorCopy.Width || newH != colorCopy.Height;
        SKBitmap? resized = needsResize
            ? colorCopy.Resize(
                new SKImageInfo(newW, newH, SKColorType.Rgba8888, SKAlphaType.Opaque),
                SKSamplingOptions.Default)
            : null;
        try {
            var src = resized ?? colorCopy;
            using var data = src.Encode(SKEncodedImageFormat.Jpeg, quality);
            return data.ToArray();
        } finally {
            resized?.Dispose();
        }
    }
}
