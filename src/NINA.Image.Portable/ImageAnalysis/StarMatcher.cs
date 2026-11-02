// Copyright (C) 2016-2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This file is derived from N.I.N.A. - Nighttime Imaging 'N' Astronomy.
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

// Copyright (C) 2016-2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This file is derived from N.I.N.A. - Nighttime Imaging 'N' Astronomy.
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

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
        double maxSearchRadius = 50.0, int maxStarsToUse = 50, int ransacIterations = 100,
        int minVotes = 3) {

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

        // A wide window lets chance coincidences pile up in one bin: with 50
        // stars a side and a 250 px window, three accidental votes happen,
        // and a live stack accepted a frame that was only shifted as a
        // "meridian flip", stacking it upside down. A caller searching wide
        // asks for more agreement, and no fallback pairing is tried.
        if (minVotes > 3 && votes < minVotes) return null;

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
            // clear winner, too few stars, or no shared star field
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
        if (!IsRigidEnough(transform)) return null;

        // Field rotation. The pairing above happens around ONE translation
        // with an 8 px window, so a star whose rotation offset exceeds that
        // never enters the fit: on a 6224 px sensor 0.15 degrees already moves
        // a corner 10 px. The fit then rests on the stars near the rotation
        // centre, and its small error in the angle grows with distance from
        // there. On a two-hour T Cep run (SQA55 + ATR2600C, 2026-11-01, 1.2
        // degrees of rotation by the last frame) that left the centre sharp
        // and the corners trailing by up to 11 px. Re-pairing through the
        // fitted transform brings the edge stars in.
        var refined = Refine(transform, referenceStars, currentStars);
        return IsRigidEnough(refined) ? refined : transform;
    }

    private static bool IsRigidEnough(AffineTransform t) {
        double det = t.M00 * t.M11 - t.M01 * t.M10;
        return Math.Abs(det - 1.0) <= 0.05 && Math.Abs(t.M01) <= 0.1 && Math.Abs(t.M10) <= 0.1;
    }

    /// <summary>Pair every star through the current transform, refit on the
    /// pairs, and tighten. Each pass sees the whole field, so the angle is
    /// fitted across the full baseline. Keeps whichever transform puts the
    /// most stars within 2 px, the smaller residual breaking a tie, so a pass
    /// that does not help changes nothing.</summary>
    private static AffineTransform Refine(AffineTransform t,
            List<DetectedStar> refs, List<DetectedStar> curs) {
        var best = t;
        var bestScore = Score(t, refs, curs);
        var current = t;
        foreach (var tol in new[] { 6.0, 3.0, 2.0 }) {
            var pairs = PairThrough(current, refs, curs, tol);
            if (pairs.Count < 6) break;
            var next = AffineTransform.FromPointPairs(
                pairs.Select(p => (p.cur.X, p.cur.Y)).ToArray(),
                pairs.Select(p => (p.refStar.X, p.refStar.Y)).ToArray());
            if (next == null) break;
            current = next;
            var score = Score(current, refs, curs);
            if (score.count > bestScore.count
                    || (score.count == bestScore.count && score.rms < bestScore.rms)) {
                best = current;
                bestScore = score;
            }
        }
        return best;
    }

    private static (int count, double rms) Score(AffineTransform t,
            List<DetectedStar> refs, List<DetectedStar> curs) {
        var pairs = PairThrough(t, refs, curs, 2.0);
        if (pairs.Count == 0) return (0, double.MaxValue);
        double sum = 0;
        foreach (var (r, c) in pairs) {
            var (x, y) = t.Apply(c.X, c.Y);
            sum += (r.X - x) * (r.X - x) + (r.Y - y) * (r.Y - y);
        }
        return (pairs.Count, Math.Sqrt(sum / pairs.Count));
    }

    /// <summary>Each current star, mapped through <paramref name="t"/>, paired
    /// with the nearest reference star within <paramref name="tol"/> px. A
    /// reference star is taken at most once.</summary>
    private static List<(DetectedStar refStar, DetectedStar cur)> PairThrough(
            AffineTransform t, List<DetectedStar> refs, List<DetectedStar> curs, double tol) {
        var pairs = new List<(DetectedStar, DetectedStar)>();
        var taken = new HashSet<int>();
        double tol2 = tol * tol;
        foreach (var c in curs) {
            var (x, y) = t.Apply(c.X, c.Y);
            int bestIdx = -1;
            double bestD2 = tol2;
            for (int i = 0; i < refs.Count; i++) {
                double dx = refs[i].X - x, dy = refs[i].Y - y;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestD2 && !taken.Contains(i)) { bestD2 = d2; bestIdx = i; }
            }
            if (bestIdx >= 0) {
                taken.Add(bestIdx);
                pairs.Add((refs[bestIdx], c));
            }
        }
        return pairs;
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