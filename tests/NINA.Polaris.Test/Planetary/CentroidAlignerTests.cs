// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details. You should have received a copy of the license along with
// this program. If not, see <https://www.gnu.org/licenses/>.

using NUnit.Framework;
using NINA.Polaris.Services.Planetary;

namespace NINA.Polaris.Test.Planetary;

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

    /// <summary>Extended bright disc (the Moon) with two near-equal hot spots
    /// far apart, which is what a real lunar frame looks like to a peak
    /// finder: thousands of pixels within noise of the maximum.</summary>
    private static ushort[] MoonFrame(int w, int h, int cx, int cy, int radius,
                                      int hotX, int hotY, ushort hotValue) {
        var px = new ushort[w * h];
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                int dx = x - cx, dy = y - cy;
                px[y * w + x] = (ushort)(dx * dx + dy * dy <= radius * radius ? 3900 : 40);
            }
        }
        px[hotY * w + hotX] = hotValue;
        return px;
    }

    [Test]
    public void Find_ExtendedDisc_IgnoresWhichHotSpotHappensToWin() {
        // The field failure, in miniature: two frames of the same disc, and the
        // brightest single pixel is on opposite sides of it. A peak tracker
        // reported centres ~200 px apart and the stacker shifted every frame by
        // that difference, which is how a 652-frame lunar SER stacked into two
        // Moons with a hard seam. The disc has not moved, so the centroid must
        // not either.
        const int w = 300, h = 300;
        var a = MoonFrame(w, h, 150, 150, 90, hotX: 90,  hotY: 110, hotValue: 4006);
        var b = MoonFrame(w, h, 150, 150, 90, hotX: 210, hotY: 190, hotValue: 4235);

        var ca = CentroidAligner.Find(a, w, h);
        var cb = CentroidAligner.Find(b, w, h);

        Assert.That(ca.X, Is.EqualTo(150).Within(2), "centroid of the disc, not of a hot pixel");
        Assert.That(ca.Y, Is.EqualTo(150).Within(2));
        var drift = Math.Sqrt(Math.Pow(ca.X - cb.X, 2) + Math.Pow(ca.Y - cb.Y, 2));
        Assert.That(drift, Is.LessThan(2.0),
            $"the two frames hold the same disc, so the alignment reference must not move ({drift:F1} px)");
    }

    [Test]
    public void Find_ExtendedDisc_TracksTheDiscWhenItActuallyMoves() {
        // The other half of the contract: real drift has to be reported, or the
        // stack smears instead of aligning.
        const int w = 300, h = 300;
        var a = MoonFrame(w, h, 150, 150, 90, 90, 110, 4006);
        var b = MoonFrame(w, h, 170, 140, 90, 90, 110, 4006);

        var ca = CentroidAligner.Find(a, w, h);
        var cb = CentroidAligner.Find(b, w, h);

        Assert.That(cb.X - ca.X, Is.EqualTo(20).Within(2));
        Assert.That(cb.Y - ca.Y, Is.EqualTo(-10).Within(2));
    }
}