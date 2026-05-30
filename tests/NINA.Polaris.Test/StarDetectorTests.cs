using NINA.Image.ImageAnalysis;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class StarDetectorTests {

    private static ushort[] MakeFrame(int width, int height, ushort background = 200) {
        var data = new ushort[width * height];
        if (background == 0) return data;
        for (int i = 0; i < data.Length; i++) data[i] = background;
        return data;
    }

    private static void PaintStar(ushort[] data, int width, int cx, int cy,
                                   int radius, ushort peak, ushort background) {
        // Crude Gaussian-ish blob, peak at centre, falls off to
        // background at radius. Good enough for the detector to
        // pick up but not so wide it triggers the MaxStarSize cap.
        for (int dy = -radius; dy <= radius; dy++) {
            for (int dx = -radius; dx <= radius; dx++) {
                int x = cx + dx, y = cy + dy;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > radius) continue;
                double t = 1 - dist / radius;
                ushort v = (ushort)(background + (peak - background) * t * t);
                int idx = y * width + x;
                if (data[idx] < v) data[idx] = v;
            }
        }
    }

    [Test]
    public void Detect_FindsObviousStarsOnFlatBackground() {
        const int w = 256, h = 256;
        var data = MakeFrame(w, h, background: 500);
        PaintStar(data, w, 50,  50,  4, 30000, 500);
        PaintStar(data, w, 200, 50,  4, 25000, 500);
        PaintStar(data, w, 50,  200, 4, 20000, 500);
        PaintStar(data, w, 200, 200, 4, 30000, 500);
        PaintStar(data, w, 128, 128, 5, 35000, 500);

        var det = new StarDetector();
        var stars = det.Detect(data, w, h);

        Assert.That(stars.Count, Is.GreaterThanOrEqualTo(5),
            "Five painted stars should all be found on a flat 500-ADU background.");
    }

    /// <summary>
    /// Reproduces the regression where a frame with a large zero
    /// border (live-stack accumulator regions that never got
    /// written, subframe black bars) yields median = 0, MAD = 0,
    /// threshold = 0, and the flood-fill consumes the entire image
    /// into one over-sized blob — returning 0 stars even though
    /// the picture is full of obvious ones.
    /// </summary>
    [Test]
    public void Detect_StillFindsStars_WhenMostOfFrameIsZero() {
        const int w = 512, h = 512;
        var data = new ushort[w * h];        // all zeros
        // Active region: ~25% of the frame, centred. Mimics a
        // subframe sitting inside a zero-padded sensor buffer.
        const int activeX = 128, activeY = 128, activeW = 256, activeH = 256;
        for (int y = activeY; y < activeY + activeH; y++) {
            for (int x = activeX; x < activeX + activeW; x++) {
                data[y * w + x] = 600;     // realistic sky background
            }
        }
        // A handful of stars inside the active region.
        PaintStar(data, w, activeX + 30,  activeY + 30,  3, 28000, 600);
        PaintStar(data, w, activeX + 220, activeY + 30,  3, 32000, 600);
        PaintStar(data, w, activeX + 30,  activeY + 220, 4, 26000, 600);
        PaintStar(data, w, activeX + 128, activeY + 128, 5, 35000, 600);

        var det = new StarDetector();
        var stars = det.Detect(data, w, h);

        Assert.That(stars.Count, Is.GreaterThanOrEqualTo(3),
            "Detector must still find the painted stars when the zero " +
            "border dominates the histogram (regression: it used to " +
            "collapse threshold to 0 and return zero stars).");
    }

    /// <summary>
    /// A perfectly flat frame (uniform background, no stars) must
    /// not crash and must return an empty list — not invent stars
    /// from random noise interpreted as signal.
    /// </summary>
    [Test]
    public void Detect_ReturnsEmpty_OnPerfectlyFlatFrame() {
        const int w = 256, h = 256;
        var data = MakeFrame(w, h, background: 1000);

        var det = new StarDetector();
        var stars = det.Detect(data, w, h);

        Assert.That(stars, Is.Empty);
    }

    [Test]
    public void Detect_HandlesAllZeroFrame() {
        const int w = 256, h = 256;
        var data = new ushort[w * h];        // all zeros

        var det = new StarDetector();
        var stars = det.Detect(data, w, h);

        // Detector must not throw and must return an empty list.
        Assert.That(stars, Is.Empty);
    }
}
