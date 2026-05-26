namespace NINA.Polaris.Services.Planetary;

/// <summary>
/// Pure-function frame quality scoring for lucky-imaging frame ranking.
/// Higher value = sharper / more detail.
///
/// Algorithm: variance of the 3×3 Laplacian filter applied to the
/// (optionally cropped) central ROI. This is the classic computer
/// vision sharpness metric (Pertuz et al. 2013 reviews it as one of
/// the most reliable focus measures), and works well for planetary
/// targets where seeing causes per-frame blur differences.
///
/// Math:
///   L(x,y) = 4·I(x,y) − I(x−1,y) − I(x+1,y) − I(x,y−1) − I(x,y+1)
///   score = Var(L)
/// </summary>
public static class FrameQualityAnalyzer {

    /// <summary>
    /// Variance of the 3×3 Laplacian over the central ROI of the frame.
    /// <paramref name="roiSize"/> = side length of a centred square ROI
    /// (e.g. 256 → 256×256 centred). null means use the whole frame.
    /// Returns 0 for degenerate inputs (no variance), never NaN.
    /// </summary>
    public static double LaplacianVariance(ushort[] pixels, int width, int height, int? roiSize = null) {
        if (pixels == null || pixels.Length != width * height || width < 3 || height < 3) return 0;

        int x0, y0, x1, y1;
        if (roiSize is int s && s > 2 && s < Math.Min(width, height)) {
            x0 = (width - s) / 2;
            y0 = (height - s) / 2;
            x1 = x0 + s;
            y1 = y0 + s;
        } else {
            x0 = 1; y0 = 1; x1 = width - 1; y1 = height - 1;
        }

        // Two-pass for numerical stability: sum, then sum of squared
        // deviations. Single-pass Welford would also work but
        // two-pass is simpler and the data fits in cache.
        double sum = 0;
        long count = 0;
        for (int y = y0; y < y1; y++) {
            int row = y * width;
            for (int x = x0; x < x1; x++) {
                int c = pixels[row + x];
                int l = (4 * c) - pixels[row + x - 1] - pixels[row + x + 1]
                                - pixels[row - width + x] - pixels[row + width + x];
                sum += l;
                count++;
            }
        }
        if (count == 0) return 0;
        double mean = sum / count;
        double sumSq = 0;
        for (int y = y0; y < y1; y++) {
            int row = y * width;
            for (int x = x0; x < x1; x++) {
                int c = pixels[row + x];
                double l = (4.0 * c) - pixels[row + x - 1] - pixels[row + x + 1]
                                     - pixels[row - width + x] - pixels[row + width + x];
                double d = l - mean;
                sumSq += d * d;
            }
        }
        return sumSq / count;
    }
}
