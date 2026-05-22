using NUnit.Framework;
using NINA.Headless.Services.Planetary;

namespace NINA.Headless.Test.Planetary;

[TestFixture]
public class CentroidAlignerTests {

    private static ushort[] MakeFrameWithSpot(int w, int h, int cx, int cy, int radius, ushort peak) {
        var px = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                double d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                double v = peak * Math.Exp(-d2 / (2.0 * radius * radius));
                px[y * w + x] = (ushort)Math.Min(65535, v);
            }
        return px;
    }

    [Test]
    public void Find_CenteredGaussianSpot_LocatesCenter() {
        var pixels = MakeFrameWithSpot(100, 100, cx: 50, cy: 50, radius: 4, peak: 60000);
        var c = CentroidAligner.Find(pixels, 100, 100);
        Assert.That(c.X, Is.EqualTo(50.0).Within(0.5));
        Assert.That(c.Y, Is.EqualTo(50.0).Within(0.5));
    }

    [Test]
    public void Find_OffsetSpot_LocatesAtNewOrigin() {
        var pixels = MakeFrameWithSpot(100, 100, cx: 30, cy: 65, radius: 3, peak: 50000);
        var c = CentroidAligner.Find(pixels, 100, 100);
        Assert.That(c.X, Is.EqualTo(30.0).Within(1.0));
        Assert.That(c.Y, Is.EqualTo(65.0).Within(1.0));
    }

    [Test]
    public void Find_NullPixels_ReturnsFrameCenter() {
        var c = CentroidAligner.Find(null!, 100, 100);
        Assert.That(c.X, Is.EqualTo(50));
        Assert.That(c.Y, Is.EqualTo(50));
    }

    [Test]
    public void Find_TinyFrame_ReturnsFrameCenter() {
        // Below the 3x3 minimum
        var c = CentroidAligner.Find(new ushort[4], 2, 2);
        Assert.That(c.X, Is.EqualTo(1));
        Assert.That(c.Y, Is.EqualTo(1));
    }
}
