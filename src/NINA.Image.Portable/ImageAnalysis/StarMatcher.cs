namespace NINA.Image.ImageAnalysis;

public static class StarMatcher {
    public static AffineTransform? Match(
        List<DetectedStar> referenceStars, List<DetectedStar> currentStars,
        double maxSearchRadius = 50.0, int maxStarsToUse = 50, int ransacIterations = 100) {

        var refStars = referenceStars.Take(maxStarsToUse).ToList();
        var curStars = currentStars.Take(maxStarsToUse).ToList();

        if (refStars.Count < 3 || curStars.Count < 3) return null;

        // Nearest-neighbor matching by position (works for small offsets between frames)
        var pairs = FindNearestPairs(refStars, curStars, maxSearchRadius);
        if (pairs.Count < 3) return null;

        // RANSAC to find best transform rejecting outliers
        return RansacAffine(pairs, ransacIterations, inlierThreshold: 3.0);
    }

    private static List<(DetectedStar refStar, DetectedStar curStar)> FindNearestPairs(
        List<DetectedStar> refStars, List<DetectedStar> curStars, double maxRadius) {

        var pairs = new List<(DetectedStar, DetectedStar)>();
        var used = new HashSet<int>();

        foreach (var rs in refStars) {
            double bestDist = maxRadius;
            int bestIdx = -1;

            for (int i = 0; i < curStars.Count; i++) {
                if (used.Contains(i)) continue;
                double dist = rs.DistanceTo(curStars[i]);
                if (dist < bestDist) {
                    bestDist = dist;
                    bestIdx = i;
                }
            }

            if (bestIdx >= 0) {
                pairs.Add((rs, curStars[bestIdx]));
                used.Add(bestIdx);
            }
        }

        return pairs;
    }

    private static AffineTransform? RansacAffine(
        List<(DetectedStar refStar, DetectedStar curStar)> pairs,
        int iterations, double inlierThreshold) {

        var rng = new Random(42);
        AffineTransform? bestTransform = null;
        int bestInlierCount = 0;

        var indices = Enumerable.Range(0, pairs.Count).ToArray();

        for (int iter = 0; iter < iterations; iter++) {
            // Pick 3 random pairs
            Shuffle(indices, rng);
            var src = new (double x, double y)[3];
            var dst = new (double x, double y)[3];
            for (int i = 0; i < 3; i++) {
                var p = pairs[indices[i]];
                src[i] = (p.refStar.X, p.refStar.Y);
                dst[i] = (p.curStar.X, p.curStar.Y);
            }

            var transform = AffineTransform.FromPointPairs(src, dst);
            if (transform == null) continue;

            // Count inliers
            int inlierCount = 0;
            foreach (var (rs, cs) in pairs) {
                var (tx, ty) = transform.Apply(rs.X, rs.Y);
                double err = Math.Sqrt((tx - cs.X) * (tx - cs.X) + (ty - cs.Y) * (ty - cs.Y));
                if (err < inlierThreshold) inlierCount++;
            }

            if (inlierCount > bestInlierCount) {
                bestInlierCount = inlierCount;
                bestTransform = transform;
            }
        }

        // Re-fit with all inliers
        if (bestTransform != null && bestInlierCount >= 3) {
            var inlierSrc = new List<(double x, double y)>();
            var inlierDst = new List<(double x, double y)>();

            foreach (var (rs, cs) in pairs) {
                var (tx, ty) = bestTransform.Apply(rs.X, rs.Y);
                double err = Math.Sqrt((tx - cs.X) * (tx - cs.X) + (ty - cs.Y) * (ty - cs.Y));
                if (err < inlierThreshold) {
                    inlierSrc.Add((rs.X, rs.Y));
                    inlierDst.Add((cs.X, cs.Y));
                }
            }

            bestTransform = AffineTransform.FromPointPairs(
                inlierSrc.ToArray(), inlierDst.ToArray()) ?? bestTransform;
        }

        return bestTransform;
    }

    private static void Shuffle(int[] array, Random rng) {
        for (int i = array.Length - 1; i > 0; i--) {
            int j = rng.Next(i + 1);
            (array[i], array[j]) = (array[j], array[i]);
        }
    }
}
