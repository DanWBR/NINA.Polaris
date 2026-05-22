using NINA.Core.Enum;

namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Bilinear demosaicing for the four common Bayer patterns. Given a raw
/// single-channel CFA buffer (one ushort per pixel, each pixel sees only
/// one of R/G/B) it produces three full-resolution channel buffers.
///
/// Convention: the pattern name describes the top-left 2×2 block read
/// row-major. RGGB means row 0 = R G R G..., row 1 = G B G B..., etc.
///
/// Output convention: each channel is a width×height ushort[] aligned
/// with the input. <see cref="ToLuminance"/> collapses an (R, G, B)
/// triple to a perceptual luminance plane for FITS output paths that
/// only carry one channel.
///
/// Why bilinear and not VNG / AHD? Bilinear is ~30 lines, fast, and the
/// downstream STUDIO pipeline (calibration, integration) operates on
/// luminance for star detection anyway. Higher-quality debayer is a
/// follow-up if anyone cares about colour fidelity in the on-server
/// preview — most users export to PixInsight for that.
/// </summary>
public static class BayerDebayer {

    public record Channels(ushort[] R, ushort[] G, ushort[] B);

    public static Channels Bilinear(ushort[] cfa, int width, int height, BayerPatternEnum pattern) {
        if (pattern == BayerPatternEnum.None || pattern == BayerPatternEnum.Auto)
            throw new ArgumentException("Pattern must be RGGB / GRBG / GBRG / BGGR.", nameof(pattern));
        if (cfa.Length < width * height)
            throw new ArgumentException("CFA buffer too small for declared dimensions.", nameof(cfa));

        int n = width * height;
        var r = new ushort[n];
        var g = new ushort[n];
        var b = new ushort[n];

        // For each output pixel, identify which CFA colour it has and
        // bilinear-interpolate the other two from neighbours.
        // ColorAt(x, y) returns the channel index 0=R, 1=G, 2=B for the
        // raw pixel at (x, y) under the chosen pattern. Edges fall back
        // to clamping; demosaic noise on the 1-pixel border is fine for
        // a preview path.
        Func<int, int, int> colorAt = ColorMapFor(pattern);

        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) {
                int idx = y * width + x;
                int colour = colorAt(x, y);
                ushort raw = cfa[idx];

                switch (colour) {
                    case 0:  // R location
                        r[idx] = raw;
                        g[idx] = AvgN4(cfa, x, y, width, height);   // greens at N/E/S/W
                        b[idx] = AvgDiag4(cfa, x, y, width, height); // blues at diagonals
                        break;
                    case 1:  // G location — interpolate R + B from
                             // horizontal/vertical neighbours depending
                             // on which row we're on.
                        g[idx] = raw;
                        // Use the colour map to find which axis has R
                        // vs B around this green pixel.
                        if (HasColourOnRow(colorAt, x, y, 0, width)) {
                            // Reds on the same row (left/right), blues
                            // above/below.
                            r[idx] = AvgH(cfa, x, y, width);
                            b[idx] = AvgV(cfa, x, y, width, height);
                        } else {
                            r[idx] = AvgV(cfa, x, y, width, height);
                            b[idx] = AvgH(cfa, x, y, width);
                        }
                        break;
                    case 2:  // B location
                        b[idx] = raw;
                        g[idx] = AvgN4(cfa, x, y, width, height);
                        r[idx] = AvgDiag4(cfa, x, y, width, height);
                        break;
                }
            }
        }

        return new Channels(r, g, b);
    }

    /// <summary>
    /// Collapse (R, G, B) into perceptual luminance using the standard
    /// Rec.601 coefficients (Y = 0.299R + 0.587G + 0.114B). The result
    /// is a single ushort[] suitable for the FITS pipeline.
    /// </summary>
    public static ushort[] ToLuminance(Channels c) {
        var y = new ushort[c.R.Length];
        for (int i = 0; i < y.Length; i++) {
            double v = 0.299 * c.R[i] + 0.587 * c.G[i] + 0.114 * c.B[i];
            y[i] = (ushort)Math.Clamp(Math.Round(v), 0, 65535);
        }
        return y;
    }

    // --- internals ---

    private static Func<int, int, int> ColorMapFor(BayerPatternEnum pattern) {
        // 2×2 block read row-major: returns 0=R, 1=G, 2=B for each
        // (x % 2, y % 2). Each pattern is fully described by its
        // top-left block.
        return pattern switch {
            BayerPatternEnum.RGGB => (x, y) => ((y & 1) == 0)
                ? ((x & 1) == 0 ? 0 : 1)
                : ((x & 1) == 0 ? 1 : 2),
            BayerPatternEnum.GRBG => (x, y) => ((y & 1) == 0)
                ? ((x & 1) == 0 ? 1 : 0)
                : ((x & 1) == 0 ? 2 : 1),
            BayerPatternEnum.GBRG => (x, y) => ((y & 1) == 0)
                ? ((x & 1) == 0 ? 1 : 2)
                : ((x & 1) == 0 ? 0 : 1),
            BayerPatternEnum.BGGR => (x, y) => ((y & 1) == 0)
                ? ((x & 1) == 0 ? 2 : 1)
                : ((x & 1) == 0 ? 1 : 0),
            _ => throw new ArgumentException($"Unsupported pattern {pattern}")
        };
    }

    /// <summary>Is the queried <paramref name="colour"/> present on the
    /// same row as the pixel at (x, y)? Used at green sites to figure
    /// out whether R is horizontal or vertical from this green.</summary>
    private static bool HasColourOnRow(Func<int, int, int> map, int x, int y, int colour, int width) {
        if (x + 1 < width  && map(x + 1, y) == colour) return true;
        if (x - 1 >= 0      && map(x - 1, y) == colour) return true;
        return false;
    }

    private static ushort AvgN4(ushort[] cfa, int x, int y, int w, int h) {
        // North / East / South / West.
        int sum = 0, n = 0;
        if (y > 0)        { sum += cfa[(y - 1) * w + x]; n++; }
        if (y + 1 < h)    { sum += cfa[(y + 1) * w + x]; n++; }
        if (x > 0)        { sum += cfa[y * w + (x - 1)]; n++; }
        if (x + 1 < w)    { sum += cfa[y * w + (x + 1)]; n++; }
        return n == 0 ? (ushort)0 : (ushort)(sum / n);
    }

    private static ushort AvgDiag4(ushort[] cfa, int x, int y, int w, int h) {
        // NW / NE / SE / SW.
        int sum = 0, n = 0;
        if (x > 0      && y > 0)      { sum += cfa[(y - 1) * w + (x - 1)]; n++; }
        if (x + 1 < w  && y > 0)      { sum += cfa[(y - 1) * w + (x + 1)]; n++; }
        if (x > 0      && y + 1 < h)  { sum += cfa[(y + 1) * w + (x - 1)]; n++; }
        if (x + 1 < w  && y + 1 < h)  { sum += cfa[(y + 1) * w + (x + 1)]; n++; }
        return n == 0 ? (ushort)0 : (ushort)(sum / n);
    }

    private static ushort AvgH(ushort[] cfa, int x, int y, int w) {
        int sum = 0, n = 0;
        if (x > 0)     { sum += cfa[y * w + (x - 1)]; n++; }
        if (x + 1 < w) { sum += cfa[y * w + (x + 1)]; n++; }
        return n == 0 ? (ushort)0 : (ushort)(sum / n);
    }

    private static ushort AvgV(ushort[] cfa, int x, int y, int w, int h) {
        int sum = 0, n = 0;
        if (y > 0)     { sum += cfa[(y - 1) * w + x]; n++; }
        if (y + 1 < h) { sum += cfa[(y + 1) * w + x]; n++; }
        return n == 0 ? (ushort)0 : (ushort)(sum / n);
    }
}
