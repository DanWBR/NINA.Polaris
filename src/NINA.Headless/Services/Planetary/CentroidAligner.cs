namespace NINA.Headless.Services.Planetary;

/// <summary>
/// Brightest-region centroid finder with sub-pixel parabolic refinement.
/// Used to align planetary frames before stacking. Works well for bright
/// targets (Moon, Jupiter, Mars, Saturn body — not Saturn rings, where
/// a thresholded centroid would be better; that's future work).
///
/// Algorithm:
///   1. Locate the brightest pixel in the frame
///   2. Take a 5×5 neighbourhood around it
///   3. Compute intensity-weighted centroid for X and Y separately
///   4. Refine the brightest pixel position via parabolic fit
///      on the 3 horizontal + 3 vertical neighbours
///
/// Returns (cx, cy) in pixel coordinates (with sub-pixel precision).
/// </summary>
public static class CentroidAligner {

    public record Centroid(double X, double Y);

    public static Centroid Find(ushort[] pixels, int width, int height) {
        if (pixels == null || pixels.Length != width * height || width < 3 || height < 3)
            return new Centroid(width / 2.0, height / 2.0);

        // Step 1: brightest pixel (avoiding the outer 2-pixel border so
        // we always have 5×5 neighbourhood + 3-point parabolic fit room).
        int bestX = width / 2, bestY = height / 2;
        ushort bestV = 0;
        for (int y = 2; y < height - 2; y++) {
            int row = y * width;
            for (int x = 2; x < width - 2; x++) {
                if (pixels[row + x] > bestV) {
                    bestV = pixels[row + x];
                    bestX = x;
                    bestY = y;
                }
            }
        }

        // Step 2: 5×5 intensity-weighted centroid around it.
        double sumI = 0, sumIX = 0, sumIY = 0;
        for (int dy = -2; dy <= 2; dy++) {
            int row = (bestY + dy) * width;
            for (int dx = -2; dx <= 2; dx++) {
                double v = pixels[row + bestX + dx];
                sumI  += v;
                sumIX += v * (bestX + dx);
                sumIY += v * (bestY + dy);
            }
        }
        if (sumI <= 0) return new Centroid(bestX, bestY);
        double cx = sumIX / sumI;
        double cy = sumIY / sumI;

        // Step 3: parabolic sub-pixel refinement on the brightest pixel's
        // 3-neighbour profile. Locks the centroid to the actual peak when
        // intensity is asymmetric.
        // x-offset = (left - right) / (2 * (left - 2*centre + right))
        int idx = bestY * width + bestX;
        double left = pixels[idx - 1], centre = pixels[idx], right = pixels[idx + 1];
        double denomX = left - 2 * centre + right;
        if (Math.Abs(denomX) > 1e-6) {
            double dx = (left - right) / (2 * denomX);
            if (Math.Abs(dx) < 1) cx = 0.5 * (cx + bestX + dx);  // average centroid + parabolic
        }
        double up = pixels[idx - width], down = pixels[idx + width];
        double denomY = up - 2 * centre + down;
        if (Math.Abs(denomY) > 1e-6) {
            double dy = (up - down) / (2 * denomY);
            if (Math.Abs(dy) < 1) cy = 0.5 * (cy + bestY + dy);
        }
        return new Centroid(cx, cy);
    }
}
