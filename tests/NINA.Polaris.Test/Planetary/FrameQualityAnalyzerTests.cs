using NUnit.Framework;
using NINA.Polaris.Services.Planetary;

namespace NINA.Polaris.Test.Planetary;

[TestFixture]
public class FrameQualityAnalyzerTests {

    [Test]
    public void LaplacianVariance_UniformFrame_IsZero() {
        // Constant pixel value → Laplacian = 0 everywhere → variance 0
        var pixels = Enumerable.Repeat<ushort>(1234, 100 * 100).ToArray();
        var v = FrameQualityAnalyzer.LaplacianVariance(pixels, 100, 100);
        Assert.That(v, Is.EqualTo(0).Within(1e-9));
    }

    [Test]
    public void LaplacianVariance_HighFrequencyPattern_ScoresHigher() {
        // Checkerboard: huge per-pixel oscillation
        var checker = new ushort[64 * 64];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                checker[y * 64 + x] = (ushort)((x + y) % 2 == 0 ? 0 : 60000);

        // Slow gradient: low frequency, small Laplacian everywhere
        var gradient = new ushort[64 * 64];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                gradient[y * 64 + x] = (ushort)(x * 10);

        var sharp = FrameQualityAnalyzer.LaplacianVariance(checker, 64, 64);
        var blurry = FrameQualityAnalyzer.LaplacianVariance(gradient, 64, 64);
        Assert.That(sharp, Is.GreaterThan(blurry),
            "High-frequency checkerboard must score higher than smooth gradient");
        Assert.That(sharp, Is.GreaterThan(1e6),
            "Checkerboard has Laplacian magnitudes ~240000; variance should be huge");
    }

    [Test]
    public void LaplacianVariance_BlurredVsSharp_ScoresHigherForSharp() {
        // Simulate a "sharp" edge vs the same edge after a box-blur.
        // Score over a centred ROI to avoid border artefacts from
        // box-blurred-into-zero-borders.
        const int w = 64, h = 64;
        var sharp = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                sharp[y * w + x] = (ushort)(x < w / 2 ? 0 : 60000);

        // Initialise blurred = sharp so borders match, then box-blur the
        // interior in place.
        var blurred = (ushort[])sharp.Clone();
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++) {
                int sum = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        sum += sharp[(y + dy) * w + (x + dx)];
                blurred[y * w + x] = (ushort)(sum / 9);
            }

        // 32-wide ROI centred on the frame, entirely inside the
        // interior so the box-blur's reach is fully sampled and the
        // sharp/blurred contrast on the edge dominates.
        var sharpScore = FrameQualityAnalyzer.LaplacianVariance(sharp, w, h, roiSize: 32);
        var blurredScore = FrameQualityAnalyzer.LaplacianVariance(blurred, w, h, roiSize: 32);
        Assert.That(sharpScore, Is.GreaterThan(blurredScore),
            "Hard edge → high variance; 3x3 box-blurred edge → lower variance");
    }

    [Test]
    public void LaplacianVariance_RoiSize_OnlyCountsCentralRegion() {
        const int w = 100, h = 100;
        var pixels = new ushort[w * h];
        // Noise everywhere
        var rng = new Random(42);
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (ushort)rng.Next(65535);
        // Flat plateau in the centre 20×20
        for (int y = 40; y < 60; y++)
            for (int x = 40; x < 60; x++)
                pixels[y * w + x] = 30000;

        var fullScore = FrameQualityAnalyzer.LaplacianVariance(pixels, w, h);
        var centreScore = FrameQualityAnalyzer.LaplacianVariance(pixels, w, h, roiSize: 16);
        Assert.That(centreScore, Is.LessThan(fullScore),
            "Centre ROI lands inside the flat plateau → near-zero variance, full frame includes noisy border");
    }

    [TestCase(1, 1)]
    [TestCase(2, 2)]
    [TestCase(0, 100)]
    [TestCase(100, 0)]
    public void LaplacianVariance_DegenerateDimensions_ReturnsZero(int w, int h) {
        var pixels = new ushort[Math.Max(1, w * h)];
        Assert.That(FrameQualityAnalyzer.LaplacianVariance(pixels, w, h), Is.EqualTo(0));
    }

    [Test]
    public void LaplacianVariance_NullPixels_ReturnsZero() {
        Assert.That(FrameQualityAnalyzer.LaplacianVariance(null!, 10, 10), Is.EqualTo(0));
    }
}
