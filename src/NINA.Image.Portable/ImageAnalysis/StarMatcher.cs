namespace NINA.Image.ImageAnalysis;

public static class StarMatcher {
    /// <summary>
    /// Returns an <see cref="AffineTransform"/> that maps a coordinate
    /// in the <b>current</b> frame back to its position in the
    /// <b>reference</b> frame. Callers feed this directly to
    /// <see cref="ImageResampler.ApplyTransform"/> with the current
    /// frame's pixels as the source: the resampler treats the transform
    /// as source-array → output-array and inverts it internally to look
    /// up the source pixel for each output (reference-grid) location,
    /// so the direction must be cur → ref.
    /// </summary>
    public static AffineTransform? Match(
        List<DetectedStar> referenceStars, List<DetectedStar> currentStars,
        double maxSearchRadius = 50.0, int maxStarsToUse = 50, int ransacIterations = 100) {

        var refStars = referenceStars.Take(maxStarsToUse).ToList();
        var curStars = currentStars.Take(maxStarsToUse).ToList();

        if (refStars.Count < 3 || curStars.Count < 3) return null;

        // Coarse translation pre-alignment. The naive nearest-neighbor
        // pairer breaks for cross-filter alignment when the shift
        // between frames is large (e.g. SHO masters captured across
        // sessions can land 100s of px apart): half the "nearest"
        // pairs are coincidental, RANSAC then has to find 3 inliers
        // in a tiny inlier fraction and almost always lands on a
        // self-consistent-but-degenerate non-rigid affine. We sidestep
        // that by voting on the (ref − cur) offset distribution first:
        // for any real translation, the correct pairs all contribute
        // to the same histogram bin while accidental pairs scatter
        // uniformly. The mode bin is the translation, and pairing
        // around that pre-aligned position with a tight tolerance
        // gives RANSAC an almost-pure inlier set.
        var (tx, ty, votes) = EstimateTranslation(refStars, curStars,
            maxSearchRadius);

        List<(DetectedStar refStar, DetectedStar curStar)> pairs;
        if (votes >= 3 && refStars.Count >= 5 && curStars.Count >= 5) {
            // Tight pairing radius is independent of the caller's
            // maxSearchRadius: once we've pre-aligned, only sub-pixel
            // centroid noise + bin-quantisation error (≤ 5 px) remains,
            // so 8 px gives the affine-fit step a clean inlier set
            // without re-admitting the false pairs maxSearchRadius
            // would let through.
            pairs = FindNearestPairsAroundTranslation(
                refStars, curStars, tx, ty, tightRadius: 8.0);
        } else {
            // Pre-alignment is unreliable when the histogram lacks a
            // clear winner — too few stars, or no shared star field
            // (e.g. one channel was clouded out). Fall back to the
            // original behavior so synthetic 3-4 star inputs and the
            // existing pinned tests keep working.
            pairs = FindNearestPairs(refStars, curStars, maxSearchRadius);
        }
        if (pairs.Count < 3) return null;

        // RANSAC to find best transform rejecting outliers.
        var transform = RansacAffine(pairs, ransacIterations, inlierThreshold: 3.0);
        if (transform == null) return null;

        // Reject grossly non-rigid affines. Every caller of this method
        // aligns frames from the same instrument (cross-sub for stacking,
        // cross-filter for channel combine), so the true transform is
        // rotation + translation with sub-percent scale variation. A fit
        // with |det − 1| > 5% or asymmetric shear > 0.1 (≈ 6° equivalent)
        // is a degenerate RANSAC consensus on noise, not a real alignment.
        // Returning null forces the caller's "could not register" error
        // path instead of silently warping the output image.
        double det = transform.M00 * transform.M11
                   - transform.M01 * transform.M10;
        if (Math.Abs(det - 1.0) > 0.05) return null;
        if (Math.Abs(transform.M01) > 0.1 || Math.Abs(transform.M10) > 0.1) return null;

        return transform;
    }

    /// <summary>
    /// Histogram-based translation estimator. Bins all (ref − cur)
    /// offsets that fall within ±<paramref name="maxOffset"/> into a
    /// 5-px grid and returns the bin centre with the most votes, plus
    /// the vote count so callers can decide whether to trust it.
    ///
    /// For matched star fields the correct pairs all map to the same
    /// bin (giving a vote count on the order of min(refStars,
    /// curStars)), while accidental pairs spread thinly across the
    /// rest of the grid. The 5-px bin size is wide enough to absorb
    /// sub-pixel centroid jitter without merging two real translations.
    /// </summary>
    private static (double tx, double ty, int votes) EstimateTranslation(
            List<DetectedStar> refs, List<DetectedStar> curs, double maxOffset) {
        const double bin = 5.0;
        var votes = new Dictionary<(int, int), int>();
        foreach (var r in refs) {
            foreach (var c in curs) {
                double dx = r.X - c.X;
                double dy = r.Y - c.Y;
                if (Math.Abs(dx) > maxOffset || Math.Abs(dy) > maxOffset) continue;
                var key = ((int)Math.Round(dx / bin), (int)Math.Round(dy / bin));
                votes.TryGetValue(key, out int v);
                votes[key] = v + 1;
            }
        }
        if (votes.Count == 0) return (0, 0, 0);
        var best = votes.OrderByDescending(kv => kv.Value).First();
        return (best.Key.Item1 * bin, best.Key.Item2 * bin, best.Value);
    }

    /// <summary>
    /// Greedy nearest-neighbor pairing in the pre-aligned frame: each
    /// reference star claims the closest unused current star whose
    /// position, after shifting by (<paramref name="tx"/>,
    /// <paramref name="ty"/>), lands inside <paramref name="tightRadius"/>.
    /// </summary>
    private static List<(DetectedStar refStar, DetectedStar curStar)>
            FindNearestPairsAroundTranslation(
                List<DetectedStar> refs, List<DetectedStar> curs,
                double tx, double ty, double tightRadius) {
        var pairs = new List<(DetectedStar, DetectedStar)>();
        var used = new HashSet<int>();
        foreach (var rs in refs) {
            double bestDist = tightRadius;
            int bestIdx = -1;
            for (int i = 0; i < curs.Count; i++) {
                if (used.Contains(i)) continue;
                double sx = curs[i].X + tx;
                double sy = curs[i].Y + ty;
                double dist = Math.Sqrt((rs.X - sx) * (rs.X - sx)
                                      + (rs.Y - sy) * (rs.Y - sy));
                if (dist < bestDist) {
                    bestDist = dist;
                    bestIdx = i;
                }
            }
            if (bestIdx >= 0) {
                pairs.Add((rs, curs[bestIdx]));
                used.Add(bestIdx);
            }
        }
        return pairs;
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
            // Pick 3 random pairs. We solve for the cur → ref direction
            // (src = cur, dst = ref) so the returned transform plugs
            // straight into ImageResampler.ApplyTransform: the resampler
            // treats the supplied transform as source-array → output and
            // inverts it to locate the source pixel for each output
            // position. The source array is the current frame, so the
            // transform must take cur coordinates to ref coordinates.
            Shuffle(indices, rng);
            var src = new (double x, double y)[3];
            var dst = new (double x, double y)[3];
            for (int i = 0; i < 3; i++) {
                var p = pairs[indices[i]];
                src[i] = (p.curStar.X, p.curStar.Y);
                dst[i] = (p.refStar.X, p.refStar.Y);
            }

            var transform = AffineTransform.FromPointPairs(src, dst);
            if (transform == null) continue;

            // Count inliers. Transform maps cur → ref, so apply to the
            // current star and compare against the reference position.
            int inlierCount = 0;
            foreach (var (rs, cs) in pairs) {
                var (tx, ty) = transform.Apply(cs.X, cs.Y);
                double err = Math.Sqrt((tx - rs.X) * (tx - rs.X) + (ty - rs.Y) * (ty - rs.Y));
                if (err < inlierThreshold) inlierCount++;
            }

            if (inlierCount > bestInlierCount) {
                bestInlierCount = inlierCount;
                bestTransform = transform;
            }
        }

        // Re-fit with all inliers, same cur → ref direction as above.
        if (bestTransform != null && bestInlierCount >= 3) {
            var inlierSrc = new List<(double x, double y)>();
            var inlierDst = new List<(double x, double y)>();

            foreach (var (rs, cs) in pairs) {
                var (tx, ty) = bestTransform.Apply(cs.X, cs.Y);
                double err = Math.Sqrt((tx - rs.X) * (tx - rs.X) + (ty - rs.Y) * (ty - rs.Y));
                if (err < inlierThreshold) {
                    inlierSrc.Add((cs.X, cs.Y));
                    inlierDst.Add((rs.X, rs.Y));
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
