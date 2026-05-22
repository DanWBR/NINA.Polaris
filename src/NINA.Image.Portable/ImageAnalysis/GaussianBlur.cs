namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Separable Gaussian blur for single-channel ushort images. Used by
/// STUDIO's noise-reduction op and as the low-pass component of the
/// unsharp-mask sharpen op.
///
/// Separability: a 2D Gaussian decomposes into one horizontal and one
/// vertical 1D pass, dropping the kernel-size cost from O(r²) per
/// pixel to O(r). At r = 4 that's 9 multiplies/adds per pixel instead
/// of 81 — meaningful even at 32 MP.
///
/// Edge handling: replicate. Pixels off the canvas re-use the nearest
/// in-bounds value. Avoids the dark border zero-padding would create
/// while staying simpler than mirroring.
/// </summary>
public static class GaussianBlur {

    /// <summary>Blur an image with the given pixel radius. σ defaults
    /// to <c>radius / 2</c>, which gives the typical "barely visible
    /// at r=1, soft at r=3, lots at r=5" feel.</summary>
    public static ushort[] Apply(ushort[] data, int width, int height,
                                 int radius, double? sigma = null) {
        if (radius < 1) return (ushort[])data.Clone();
        var actualSigma = sigma ?? (radius / 2.0);
        if (actualSigma <= 0) actualSigma = 0.5;

        var kernel = BuildKernel(radius, actualSigma);

        // Horizontal pass into a float scratch buffer (keeps the
        // intermediate from quantising twice).
        int n = width * height;
        var temp = new float[n];
        var output = new ushort[n];

        for (int y = 0; y < height; y++) {
            int rowOff = y * width;
            for (int x = 0; x < width; x++) {
                double acc = 0;
                for (int k = -radius; k <= radius; k++) {
                    int xs = Math.Clamp(x + k, 0, width - 1);
                    acc += data[rowOff + xs] * kernel[k + radius];
                }
                temp[rowOff + x] = (float)acc;
            }
        }

        // Vertical pass.
        for (int y = 0; y < height; y++) {
            int rowOff = y * width;
            for (int x = 0; x < width; x++) {
                double acc = 0;
                for (int k = -radius; k <= radius; k++) {
                    int ys = Math.Clamp(y + k, 0, height - 1);
                    acc += temp[ys * width + x] * kernel[k + radius];
                }
                output[rowOff + x] = (ushort)Math.Clamp(Math.Round(acc), 0, 65535);
            }
        }

        return output;
    }

    private static double[] BuildKernel(int radius, double sigma) {
        int size = 2 * radius + 1;
        var k = new double[size];
        double twoSigmaSq = 2 * sigma * sigma;
        double sum = 0;
        for (int i = -radius; i <= radius; i++) {
            var v = Math.Exp(-(i * i) / twoSigmaSq);
            k[i + radius] = v;
            sum += v;
        }
        // Normalise so the kernel sums to 1 (preserves overall brightness).
        for (int i = 0; i < size; i++) k[i] /= sum;
        return k;
    }
}
