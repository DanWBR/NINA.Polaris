using NUnit.Framework;
using NINA.Polaris.Services.Studio;
using SkiaSharp;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the FitsThumbnailer renderer against the regression that
/// gave us silent black output: when <c>maxDim</c> is larger than
/// the source image (so we'd "skip" resize), an earlier version
/// aliased two <c>using</c> variables onto the same SKBitmap. The
/// double dispose corrupted Skia's native handle, so the final
/// <c>Encode</c> wrote all-zero pixels.
///
/// We don't have a sample FITS file checked in. Instead we exercise
/// <see cref="FitsThumbnailer.RenderJpegFromBuffer"/> directly with
/// a synthetic gradient, which is the *exact* code path the
/// <see cref="FitsThumbnailer.RenderJpegFromPath"/> entry point
/// hits after FITSReader decode.
/// </summary>
[TestFixture]
public class FitsThumbnailerTests {

    // Mid-gray ushort buffer with a vertical brightness gradient.
    // After auto-stretch this produces a JPEG that is *not* uniform
    // (so the "all-black-output" regression would fail the assertion).
    private static ushort[] Gradient(int w, int h) {
        var buf = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                buf[y * w + x] = (ushort)(((long)y * 65535) / Math.Max(1, h - 1));
        return buf;
    }

    [Test]
    public void RenderJpegFromBuffer_LargeMaxDim_NoResizePath_ProducesNonBlackImage() {
        // 100x80 source, maxDim=2400 → the renderer takes the
        // "no resize needed" branch. Was returning all-zero pixels
        // due to the double-dispose bug.
        var jpeg = FitsThumbnailer.RenderJpegFromBuffer(Gradient(100, 80), 100, 80,
            bitDepth: 16, maxDim: 2400, quality: 90);

        Assert.That(jpeg, Is.Not.Null);
        Assert.That(jpeg.Length, Is.GreaterThan(200), "Encoded JPEG suspiciously small");

        // Decode and verify there's actual non-zero content in the pixels.
        using var bmp = SKBitmap.Decode(jpeg);
        Assert.That(bmp, Is.Not.Null, "JPEG failed to decode");
        Assert.That(bmp!.Width, Is.EqualTo(100));
        Assert.That(bmp.Height, Is.EqualTo(80));

        // Auto-stretched gradient must contain bright pixels somewhere.
        bool hasBright = false;
        for (int y = 0; y < bmp.Height && !hasBright; y++)
            for (int x = 0; x < bmp.Width && !hasBright; x++)
                if (bmp.GetPixel(x, y).Red > 128) hasBright = true;

        Assert.That(hasBright, Is.True,
            "Rendered JPEG is all dark — the double-dispose regression is back");
    }

    [Test]
    public void RenderJpegFromBuffer_SmallMaxDim_ResizePath_ProducesNonBlackImage() {
        // Same gradient, but maxDim=64 forces the resize branch. This
        // is the path that was already working before the bug fix;
        // pinning it here so a future refactor doesn't break the
        // non-aliasing case either.
        var jpeg = FitsThumbnailer.RenderJpegFromBuffer(Gradient(1000, 800), 1000, 800,
            bitDepth: 16, maxDim: 64, quality: 85);

        using var bmp = SKBitmap.Decode(jpeg);
        Assert.That(bmp, Is.Not.Null);
        // Longest side should be exactly maxDim, the other proportional.
        Assert.That(Math.Max(bmp!.Width, bmp.Height), Is.EqualTo(64));

        bool hasBright = false;
        for (int y = 0; y < bmp.Height && !hasBright; y++)
            for (int x = 0; x < bmp.Width && !hasBright; x++)
                if (bmp.GetPixel(x, y).Red > 128) hasBright = true;
        Assert.That(hasBright, Is.True);
    }

    [Test]
    public void RenderJpegFromBuffer_TinyMaxDim_ClampsButStillRenders() {
        // Degenerate case: maxDim=1. Output is 1x1 but the call
        // shouldn't throw and the JPEG should still be parseable.
        var jpeg = FitsThumbnailer.RenderJpegFromBuffer(Gradient(50, 50), 50, 50,
            bitDepth: 16, maxDim: 1, quality: 85);

        using var bmp = SKBitmap.Decode(jpeg);
        Assert.That(bmp, Is.Not.Null);
        Assert.That(bmp!.Width, Is.EqualTo(1));
        Assert.That(bmp.Height, Is.EqualTo(1));
    }
}
