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
        // Safety floor. ComputeStats already swaps to a non-zero
        // population when the buffer is mostly black and clamps
        // MAD to >=1, but a frame where the *real* signal also
        // straddles 0 (heavily-stretched preview, weird subframe)
        // can still yield threshold ≈ 0. Detection from a 0
        // threshold is catastrophic: every non-zero pixel becomes a
        // star seed, the flood-fill swallows the whole image into
        // one mega-blob, the blob exceeds MaxStarSize and gets
        // rejected, and the call returns 0 stars even though the
        // image is full of obvious stars. Cap the threshold at
        // median+5 (≈ 1 step above the background bucket) as a
        // last-resort floor.
        if (threshold <= stats.median + 0.5) threshold = stats.median + 5;

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
        int nonZero = 0;
        for (int i = 0; i < data.Length; i++) {
            var v = data[i];
            histogram[v]++;
            if (v > 0) nonZero++;
        }

        // If the zero bucket dominates the buffer (subframe black
        // border, un-touched live-stack accumulator cells from
        // alignment offsets, masked-out regions), the naive median
        // is 0 and MAD collapses too — every real star gets clipped
        // under a threshold of 0. Skip the zero bucket when zeros
        // outnumber real samples by a wide margin and the buffer
        // still has enough non-zero data to compute meaningful
        // statistics from.
        bool excludeZeros = nonZero > 0
                            && nonZero >= data.Length / 4
                            && histogram[0] > data.Length / 2;
        long population = excludeZeros ? nonZero : data.Length;
        long half = population / 2;
        int startBin = excludeZeros ? 1 : 0;

        long cumulative = 0;
        double median = 0;
        for (int i = startBin; i < histogram.Length; i++) {
            cumulative += histogram[i];
            if (cumulative > half) { median = i; break; }
        }

        var devHist = new int[65536];
        for (int i = 0; i < data.Length; i++) {
            var v = data[i];
            if (excludeZeros && v == 0) continue;
            int dev = (int)Math.Abs(v - median);
            if (dev < 65536) devHist[dev]++;
        }

        cumulative = 0;
        double mad = 0;
        for (int i = 0; i < devHist.Length; i++) {
            cumulative += devHist[i];
            if (cumulative > half) { mad = i; break; }
        }

        // Floor: very flat data (synthetic test frame, all-uniform
        // background) can produce MAD = 0 even after the zero-bucket
        // exclusion above. A 1-ADU floor stops the threshold from
        // collapsing to median and reduces the flood-fill explosion
        // path to "every star found", not "no stars found".
        if (mad < 1) mad = 1;

        return (median, mad);
    }
}
