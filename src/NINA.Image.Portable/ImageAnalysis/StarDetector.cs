namespace NINA.Image.ImageAnalysis;

public class StarDetector {
    public int MinStarSize { get; set; } = 5;
    public int MaxStarSize { get; set; } = 200;
    public double SigmaThreshold { get; set; } = 5.0;
    public int MaxStars { get; set; } = 500;
    public int BorderExclusion { get; set; } = 20;

    public List<DetectedStar> Detect(ushort[] data, int width, int height) {
        var stats = ComputeStats(data);
        double threshold = stats.median + SigmaThreshold * stats.mad * 1.4826;

        var visited = new bool[width * height];
        var stars = new List<DetectedStar>();

        for (int y = BorderExclusion; y < height - BorderExclusion; y++) {
            for (int x = BorderExclusion; x < width - BorderExclusion; x++) {
                int idx = y * width + x;
                if (visited[idx] || data[idx] < threshold) continue;

                var pixels = FloodFill(data, width, height, x, y, threshold, visited);
                if (pixels.Count < MinStarSize || pixels.Count > MaxStarSize) continue;

                var star = ComputeStarProperties(data, width, pixels);
                if (star.HFR > 0.5 && star.HFR < 50)
                    stars.Add(star);
            }
        }

        stars.Sort((a, b) => b.Flux.CompareTo(a.Flux));
        if (stars.Count > MaxStars)
            stars.RemoveRange(MaxStars, stars.Count - MaxStars);

        return stars;
    }

    private List<(int x, int y)> FloodFill(ushort[] data, int width, int height,
        int startX, int startY, double threshold, bool[] visited) {
        var result = new List<(int x, int y)>();
        var stack = new Stack<(int x, int y)>();
        stack.Push((startX, startY));

        while (stack.Count > 0 && result.Count < MaxStarSize * 2) {
            var (px, py) = stack.Pop();
            int idx = py * width + px;

            if (px < 0 || px >= width || py < 0 || py >= height) continue;
            if (visited[idx] || data[idx] < threshold) continue;

            visited[idx] = true;
            result.Add((px, py));

            stack.Push((px + 1, py));
            stack.Push((px - 1, py));
            stack.Push((px, py + 1));
            stack.Push((px, py - 1));
        }

        return result;
    }

    private static DetectedStar ComputeStarProperties(ushort[] data, int width, List<(int x, int y)> pixels) {
        double sumX = 0, sumY = 0, sumFlux = 0;
        double peak = 0;

        double background = double.MaxValue;
        foreach (var (x, y) in pixels) {
            double val = data[y * width + x];
            if (val < background) background = val;
        }

        foreach (var (x, y) in pixels) {
            double val = data[y * width + x] - background;
            sumX += x * val;
            sumY += y * val;
            sumFlux += val;
            if (data[y * width + x] > peak) peak = data[y * width + x];
        }

        double cx = sumFlux > 0 ? sumX / sumFlux : pixels[0].x;
        double cy = sumFlux > 0 ? sumY / sumFlux : pixels[0].y;

        // HFR: half-flux radius
        double totalFlux = sumFlux;
        double sumWeightedDist = 0;
        foreach (var (x, y) in pixels) {
            double val = data[y * width + x] - background;
            double dist = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
            sumWeightedDist += val * dist;
        }

        double hfr = totalFlux > 0 ? sumWeightedDist / totalFlux : 0;

        return new DetectedStar {
            X = cx,
            Y = cy,
            HFR = hfr,
            Peak = peak,
            Flux = totalFlux,
            PixelCount = pixels.Count
        };
    }

    private static (double median, double mad) ComputeStats(ushort[] data) {
        var histogram = new int[65536];
        for (int i = 0; i < data.Length; i++) histogram[data[i]]++;

        long half = data.Length / 2;
        long cumulative = 0;
        double median = 0;
        for (int i = 0; i < histogram.Length; i++) {
            cumulative += histogram[i];
            if (cumulative > half) { median = i; break; }
        }

        var devHist = new int[65536];
        for (int i = 0; i < data.Length; i++) {
            int dev = (int)Math.Abs(data[i] - median);
            if (dev < 65536) devHist[dev]++;
        }

        cumulative = 0;
        double mad = 0;
        for (int i = 0; i < devHist.Length; i++) {
            cumulative += devHist[i];
            if (cumulative > half) { mad = i; break; }
        }

        return (median, mad);
    }
}
