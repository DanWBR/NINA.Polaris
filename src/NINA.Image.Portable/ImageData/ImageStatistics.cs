using NINA.Image.Interfaces;

namespace NINA.Image.ImageData;

public class ImageStatistics : IImageStatistics {
    public int Width { get; private set; }
    public int Height { get; private set; }
    public double Mean { get; private set; }
    public double Median { get; private set; }
    public double StDev { get; private set; }
    public double MAD { get; private set; }
    public int Min { get; private set; }
    public int Max { get; private set; }
    public long StarCount { get; set; }
    public double HFR { get; set; }

    private ImageStatistics() { }

    public static ImageStatistics Create(IImageData imageData) {
        var data = imageData.Data;
        var props = imageData.Properties;
        var stats = new ImageStatistics {
            Width = props.Width,
            Height = props.Height
        };

        if (data.Length == 0) return stats;

        long sum = 0;
        int min = int.MaxValue;
        int max = int.MinValue;

        for (int i = 0; i < data.Length; i++) {
            int val = data[i];
            sum += val;
            if (val < min) min = val;
            if (val > max) max = val;
        }

        stats.Min = min;
        stats.Max = max;
        stats.Mean = (double)sum / data.Length;

        // Standard deviation
        double sumSqDiff = 0;
        for (int i = 0; i < data.Length; i++) {
            double diff = data[i] - stats.Mean;
            sumSqDiff += diff * diff;
        }
        stats.StDev = Math.Sqrt(sumSqDiff / data.Length);

        // Median via histogram (faster than sort for 16-bit data)
        stats.Median = ComputeMedianViaHistogram(data);

        // MAD (Median Absolute Deviation)
        stats.MAD = ComputeMAD(data, stats.Median);

        return stats;
    }

    private static double ComputeMedianViaHistogram(ushort[] data) {
        var histogram = new int[65536];
        for (int i = 0; i < data.Length; i++) {
            histogram[data[i]]++;
        }

        long half = data.Length / 2;
        long cumulative = 0;
        for (int i = 0; i < histogram.Length; i++) {
            cumulative += histogram[i];
            if (cumulative > half) return i;
        }
        return 0;
    }

    private static double ComputeMAD(ushort[] data, double median) {
        var deviations = new ushort[data.Length];
        for (int i = 0; i < data.Length; i++) {
            deviations[i] = (ushort)Math.Abs(data[i] - median);
        }

        var histogram = new int[65536];
        for (int i = 0; i < deviations.Length; i++) {
            histogram[deviations[i]]++;
        }

        long half = deviations.Length / 2;
        long cumulative = 0;
        for (int i = 0; i < histogram.Length; i++) {
            cumulative += histogram[i];
            if (cumulative > half) return i;
        }
        return 0;
    }
}
