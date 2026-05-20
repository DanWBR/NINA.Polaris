namespace NINA.Image.ImageAnalysis;

public static class AutoStretch {
    public static byte[] Apply(ushort[] data, int width, int height, int bitDepth = 16) {
        int pixelCount = width * height;
        var result = new byte[pixelCount];

        if (data.Length == 0) return result;

        // Compute median and MAD via histogram
        var histogram = new int[65536];
        for (int i = 0; i < data.Length && i < pixelCount; i++) {
            histogram[data[i]]++;
        }

        long half = pixelCount / 2;
        long cumulative = 0;
        double median = 0;
        for (int i = 0; i < histogram.Length; i++) {
            cumulative += histogram[i];
            if (cumulative > half) {
                median = i;
                break;
            }
        }

        // MAD
        var devHistogram = new int[65536];
        for (int i = 0; i < data.Length && i < pixelCount; i++) {
            int dev = (int)Math.Abs(data[i] - median);
            if (dev < 65536) devHistogram[dev]++;
        }

        cumulative = 0;
        double mad = 0;
        for (int i = 0; i < devHistogram.Length; i++) {
            cumulative += devHistogram[i];
            if (cumulative > half) {
                mad = i;
                break;
            }
        }

        // MTF (Midtone Transfer Function) stretch
        // Normalized median and deviation
        double maxVal = (1 << bitDepth) - 1;
        double normalizedMedian = median / maxVal;
        double normalizedMAD = mad / maxVal;

        double shadow = Math.Max(0, normalizedMedian - 2.8 * normalizedMAD);
        double midtone = MTF(normalizedMedian - shadow, 0.25);

        // Build lookup table
        var lut = new byte[65536];
        for (int i = 0; i < 65536; i++) {
            double normalized = i / maxVal;
            double clipped = Math.Clamp((normalized - shadow) / (1.0 - shadow), 0, 1);
            double stretched = MTF(clipped, midtone);
            lut[i] = (byte)(stretched * 255);
        }

        // Apply LUT
        for (int i = 0; i < data.Length && i < pixelCount; i++) {
            result[i] = lut[data[i]];
        }

        return result;
    }

    private static double MTF(double x, double midtone) {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        if (midtone <= 0) return 1;
        if (midtone >= 1) return 0;
        return (midtone - 1.0) * x / ((2.0 * midtone - 1.0) * x - midtone);
    }
}
