using NUnit.Framework;
using NINA.Image.ImageAnalysis;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the StarMatcher + ImageResampler convention used by every
/// alignment-driven service (LiveStackingService,
/// BatchStackingService, ChannelCombineService, WASM Interop).
///
/// The contract under test: given a current frame whose stars are
/// shifted by dx,dy relative to a reference, the resampler must
/// produce an output where stars land at their reference positions.
/// If this regresses, integrated masters smear and plate-solve fails
/// (see the BatchStacking ASTAP-quad-match bug).
/// </summary>
[TestFixture]
public class StarAlignmentTests {

    private static List<DetectedStar> StarsFromPoints((double x, double y)[] pts) {
        var list = new List<DetectedStar>(pts.Length);
        foreach (var (x, y) in pts) {
            list.Add(new DetectedStar { X = x, Y = y, HFR = 2.0 });
        }
        return list;
    }

    private static ushort[] FrameWithStars(int w, int h, (int x, int y)[] starPixels) {
        var img = new ushort[w * h];
        foreach (var (sx, sy) in starPixels) {
            // Cross-shaped impulse so bilinear sampling has a clear
            // peak to read from after resampling. Pure delta would
            // average to 25% on a half-pixel shift; a 3x3 plateau
            // survives bilinear cleanly.
            for (int dy = -1; dy <= 1; dy++) {
                for (int dx = -1; dx <= 1; dx++) {
                    int x = sx + dx, y = sy + dy;
                    if (x >= 0 && x < w && y >= 0 && y < h) {
                        img[y * w + x] = 50000;
                    }
                }
            }
        }
        return img;
    }

    /// <summary>
    /// Given a current frame whose stars are at (ref + drift), the
    /// resampler must land them back at the reference positions.
    /// Concretely: if curStar = refStar + (5, 0), then output(refStar)
    /// should be bright. With the old direction bug, the star moved to
    /// (refStar + 2 * drift) in the output, leaving output(refStar)
    /// dark.
    /// </summary>
    [Test]
    public void StarMatcher_Resampler_AlignsCurrentFrameOntoReferenceGrid() {
        const int W = 256, H = 256;

        // Reference stars: a spread-out non-collinear cluster so the
        // affine fit is well-conditioned. RANSAC needs at least three
        // non-collinear points to lock a unique transform.
        var refPositions = new (double x, double y)[] {
            (50, 50), (200, 60), (120, 200), (180, 150), (80, 180)
        };
        // Current frame: same stars shifted by (+5, +3).
        const int DX = 5, DY = 3;
        var curPositions = refPositions
            .Select(p => (p.x + DX, p.y + DY))
            .ToArray();

        var refStars = StarsFromPoints(refPositions);
        var curStars = StarsFromPoints(curPositions);

        var transform = StarMatcher.Match(refStars, curStars);
        Assert.That(transform, Is.Not.Null, "StarMatcher should produce a transform");

        // Build a current-frame image with stars at curPositions, then
        // resample. Expect the output to have bright pixels at the
        // reference positions (drift undone).
        var curImg = FrameWithStars(W, H,
            curPositions.Select(p => ((int)p.Item1, (int)p.Item2)).ToArray());
        var aligned = ImageResampler.ApplyTransform(curImg, W, H, transform!);

        foreach (var (rx, ry) in refPositions) {
            int idx = (int)ry * W + (int)rx;
            Assert.That(aligned[idx], Is.GreaterThan(40000),
                $"Reference position ({rx},{ry}) should be bright after alignment, " +
                $"got {aligned[idx]}. Drift was ({DX},{DY}).");
        }
    }

    /// <summary>
    /// Sanity probe on the direction convention alone. If the matcher
    /// returns "ref → cur", the resampler treats it as a forward
    /// source→output map and inverts it, which double-applies the
    /// drift in the wrong direction. This test asserts the convention
    /// the resampler expects: passing the matcher's output to the
    /// resampler should land cur-coords at ref-coords (i.e. the
    /// transform supplied to the resampler is cur→ref).
    /// </summary>
    [Test]
    public void StarMatcher_ReturnsTransformMappingCurrentToReference() {
        var refPositions = new (double x, double y)[] {
            (50, 50), (200, 60), (120, 200), (180, 150)
        };
        const int DX = 7, DY = -4;
        var curPositions = refPositions
            .Select(p => (p.x + DX, p.y + DY))
            .ToArray();

        var refStars = StarsFromPoints(refPositions);
        var curStars = StarsFromPoints(curPositions);

        var transform = StarMatcher.Match(refStars, curStars);
        Assert.That(transform, Is.Not.Null);

        // Apply the transform to a current-frame star coord; expect
        // the corresponding reference-frame coord.
        var (mappedX, mappedY) = transform!.Apply(curPositions[0].Item1,
                                                   curPositions[0].Item2);
        Assert.That(mappedX, Is.EqualTo(refPositions[0].x).Within(0.5),
            "Transform should map current-frame X back to reference-frame X");
        Assert.That(mappedY, Is.EqualTo(refPositions[0].y).Within(0.5),
            "Transform should map current-frame Y back to reference-frame Y");
    }

    /// <summary>
    /// Channel combine regression. SHO masters captured across
    /// different sessions can be offset by 100s of pixels per filter,
    /// and the star fields for narrowband filters only partially
    /// overlap (a star bright in Ha may be invisible in SII). Plus a
    /// few outlier "stars" land in random positions in one channel
    /// only. The matcher must still recover a rigid (essentially
    /// translation-only) transform.
    ///
    /// Pre-fix symptom: greedy nearest-neighbor pairing at radius 100
    /// fed RANSAC mostly false pairs, and 100 iterations were too few
    /// to find a clean inlier triple. RANSAC returned a degenerate
    /// non-isotropic affine (Y-scale ≈ 0.93 plus 250 px translation),
    /// the composed SHO cube ended up with one warped plane, and ASTAP
    /// failed to plate-solve it.
    /// </summary>
    [Test]
    public void StarMatcher_RecoversRigidTransform_UnderLargeShiftAndPartialOverlap() {
        // 12 shared stars across a wide field, spread enough that the
        // affine fit isn't dominated by a tight cluster.
        var sharedRefPositions = new (double x, double y)[] {
            (320,  410), (1200,  600), ( 800, 1800), (2400, 2500),
            (4800, 3100), ( 480, 3200), (2700,  900), (3900, 1500),
            (1500, 2400), (3200, 2800), (4200,  800), ( 950, 1700),
        };
        // Real per-filter cross-channel shift observed on test_data
        // SHO masters: ~40 px X, ~20 px Y. We exaggerate both axes so
        // the test is unambiguous and would catch a regression even
        // after star-detection noise.
        const int DX = -45, DY = 22;
        var sharedCurPositions = sharedRefPositions
            .Select(p => (p.x - DX, p.y - DY))
            .ToArray();

        // Add per-channel-only "stars" (filter-specific bright pixels
        // or hot pixels in one master). These exist in only one list
        // and must not be matched. With them around, naive nearest-
        // neighbor pairing produces a lot of accidental pairings.
        var refOnly = new (double x, double y)[] {
            (100, 2900), (4900, 200), (2600, 3500), (200, 200), (4500, 3400),
        };
        var curOnly = new (double x, double y)[] {
            (3000, 100), (4600, 2700), (300, 3000), (1800, 100), (4700, 600),
        };

        var refStars = StarsFromPoints(sharedRefPositions.Concat(refOnly).ToArray());
        var curStars = StarsFromPoints(sharedCurPositions.Concat(curOnly).ToArray());

        var transform = StarMatcher.Match(refStars, curStars,
            maxSearchRadius: 500);
        Assert.That(transform, Is.Not.Null,
            "StarMatcher should recover a transform across a large shift " +
            "with partial overlap");

        // The transform must be essentially rigid (rotation + translation
        // with no scale change). A degenerate fit fails the determinant
        // check below.
        double det = transform!.M00 * transform.M11
                   - transform.M01 * transform.M10;
        Assert.That(det, Is.EqualTo(1.0).Within(0.01),
            $"Affine determinant must be ~1 (rigid); got {det:F4}. " +
            $"M00={transform.M00:F4} M01={transform.M01:F4} " +
            $"M10={transform.M10:F4} M11={transform.M11:F4}");

        // Apply to a shared cur star; expect to land on the matching
        // ref star within sub-pixel tolerance.
        for (int i = 0; i < sharedRefPositions.Length; i++) {
            var (mx, my) = transform.Apply(sharedCurPositions[i].Item1,
                                            sharedCurPositions[i].Item2);
            Assert.That(mx, Is.EqualTo(sharedRefPositions[i].x).Within(1.0),
                $"Shared star #{i} X should map back to reference position");
            Assert.That(my, Is.EqualTo(sharedRefPositions[i].y).Within(1.0),
                $"Shared star #{i} Y should map back to reference position");
        }
    }

    /// <summary>
    /// Defense-in-depth: when the two star lists share no real pairs
    /// (e.g. one channel was clouded out, or the framing barely
    /// overlaps), the matcher must return null rather than fabricating
    /// a transform that would silently warp the output image.
    /// </summary>
    [Test]
    public void StarMatcher_ReturnsNull_WhenStarFieldsDoNotOverlap() {
        var refStars = StarsFromPoints(new (double x, double y)[] {
            (100, 100), (200, 200), (300, 300), (400, 400), (500, 500),
            (150, 350), (450, 150), (250, 450),
        });
        // Completely different positions, no consistent translation.
        var curStars = StarsFromPoints(new (double x, double y)[] {
            (1234, 678), (2000, 100), (300, 2500), (1500, 1500), (50, 50),
            (3000, 1000), (1200, 200), (400, 3300),
        });

        var transform = StarMatcher.Match(refStars, curStars,
            maxSearchRadius: 4000);
        Assert.That(transform, Is.Null,
            "StarMatcher must not return a degenerate transform when " +
            "the inputs share no real pairs");
    }
}
