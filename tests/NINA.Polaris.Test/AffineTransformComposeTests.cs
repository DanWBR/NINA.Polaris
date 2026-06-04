using NINA.Image.ImageAnalysis;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins AffineTransform.Compose (added for the live-stack meridian-flip
/// path). Compose(after, first) must equal applying first then after.
/// </summary>
[TestFixture]
public class AffineTransformComposeTests {

    [Test]
    public void Compose_AppliesFirstThenAfter() {
        // first: translate by (10, 5). after: scale x2 about origin.
        var first = new AffineTransform { Tx = 10, Ty = 5 };
        var after = new AffineTransform { M00 = 2, M11 = 2 };

        var composed = AffineTransform.Compose(after, first);

        // Compose(after, first).Apply(p) == after.Apply(first.Apply(p)).
        var (cx, cy) = composed.Apply(3, 4);
        var (fx, fy) = first.Apply(3, 4);
        var (ex, ey) = after.Apply(fx, fy);
        Assert.That(cx, Is.EqualTo(ex).Within(1e-9));
        Assert.That(cy, Is.EqualTo(ey).Within(1e-9));
        // Explicit: ((3+10)*2, (4+5)*2) = (26, 18).
        Assert.That(cx, Is.EqualTo(26).Within(1e-9));
        Assert.That(cy, Is.EqualTo(18).Within(1e-9));
    }

    [Test]
    public void Compose_WithIdentity_IsNoOp() {
        var t = new AffineTransform { M00 = 0.5, M01 = 0.1, M10 = -0.2, M11 = 1.3, Tx = 7, Ty = -3 };
        var id = AffineTransform.Identity;

        var left = AffineTransform.Compose(id, t);
        var right = AffineTransform.Compose(t, id);

        foreach (var c in new[] { left, right }) {
            Assert.That(c.M00, Is.EqualTo(t.M00).Within(1e-9));
            Assert.That(c.M01, Is.EqualTo(t.M01).Within(1e-9));
            Assert.That(c.M10, Is.EqualTo(t.M10).Within(1e-9));
            Assert.That(c.M11, Is.EqualTo(t.M11).Within(1e-9));
            Assert.That(c.Tx, Is.EqualTo(t.Tx).Within(1e-9));
            Assert.That(c.Ty, Is.EqualTo(t.Ty).Within(1e-9));
        }
    }

    [Test]
    public void Compose_Rot180ThenMatch_LandsOnReferenceGrid() {
        // The exact composition the live stacker uses: a 180-deg rotation
        // about the image, then a residual translation match. A star at
        // current-frame coord c, rotated then matched, must land at its
        // reference coord.
        const int W = 200, H = 160;
        var rot180 = new AffineTransform { M00 = -1, M11 = -1, Tx = W - 1, Ty = H - 1 };
        // After rot180, the star is still offset by (8,-5) from its ref
        // position, so the residual translation that lands it on ref is
        // exactly (8,-5) -- this is what StarMatcher recovers from the
        // rotated star list.
        var residual = new AffineTransform { Tx = 8, Ty = -5 };
        var composed = AffineTransform.Compose(residual, rot180);

        // A reference star at (60, 40); after a 180 flip + (8,-5) offset it
        // appears in the current frame at:
        double refX = 60, refY = 40;
        double curX = (W - 1) - refX + 8;
        double curY = (H - 1) - refY - 5;

        var (mx, my) = composed.Apply(curX, curY);
        Assert.That(mx, Is.EqualTo(refX).Within(1e-6));
        Assert.That(my, Is.EqualTo(refY).Within(1e-6));
    }
}
