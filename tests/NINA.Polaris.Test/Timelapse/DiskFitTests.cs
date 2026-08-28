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

using NINA.Polaris.Services.Timelapse;
using NUnit.Framework;

namespace NINA.Polaris.Test.Timelapse;

// The limb (circle) fit is what gives eclipse-grade centering: unlike the
// centroid, it recovers the true disc center from a partial-eclipse crescent.
[TestFixture]
public class DiskFitTests {

    private static ushort[] Field(int w, int h, int bg) {
        var a = new ushort[w * h];
        for (int i = 0; i < a.Length; i++) a[i] = (ushort)bg;
        return a;
    }

    private static void FillDisc(ushort[] a, int w, int h, double cx, double cy, double r, int val) {
        int r2 = (int)Math.Ceiling(r);
        for (int y = (int)(cy - r2); y <= cy + r2; y++) {
            if (y < 0 || y >= h) continue;
            for (int x = (int)(cx - r2); x <= cx + r2; x++) {
                if (x < 0 || x >= w) continue;
                double dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy <= r * r) a[y * w + x] = (ushort)val;
            }
        }
    }

    [Test]
    public void FullDisc_CenterFoundToSubPixel() {
        int w = 140, h = 110;
        var a = Field(w, h, 1000);
        FillDisc(a, w, h, 40, 70, 22, 50000);      // off-center full disc
        Assert.That(DiskFit.TryFindCenter(a, w, h, out var cx, out var cy), Is.True);
        Assert.That(cx, Is.EqualTo(40).Within(1.5));
        Assert.That(cy, Is.EqualTo(70).Within(1.5));
    }

    [Test]
    public void PartialEclipseCrescent_LimbFitRecoversTheTrueCenter() {
        // Sun disc, then the Moon takes a bite out of the right side. The bright
        // region is a crescent-ish shape whose CENTROID sits left of the true
        // center; the limb fit must still return the Sun's real center.
        int w = 160, h = 120;
        double sunX = 70, sunY = 60, sunR = 30;
        var a = Field(w, h, 1000);
        FillDisc(a, w, h, sunX, sunY, sunR, 50000);
        FillDisc(a, w, h, 92, sunY, 18, 1000);      // Moon bite (back to background)

        Assert.That(DiskFit.TryFindCenter(a, w, h, out var cx, out var cy), Is.True);
        Assert.That(cx, Is.EqualTo(sunX).Within(2.5), "limb fit finds the Sun's center");
        Assert.That(cy, Is.EqualTo(sunY).Within(2.5));

        // The intensity centroid is dragged toward the bright side (away from the
        // bite), so the limb fit is meaningfully closer to the true center.
        var c = NINA.Polaris.Services.Planetary.CentroidAligner.Find(a, w, h);
        double limbErr = Math.Abs(cx - sunX);
        double centroidErr = Math.Abs(c.X - sunX);
        Assert.That(limbErr, Is.LessThan(centroidErr),
            $"limb error {limbErr:F1} should beat centroid error {centroidErr:F1}");
    }

    [Test]
    public void EmptyField_ReturnsFalse() {
        int w = 64, h = 64;
        Assert.That(DiskFit.TryFindCenter(Field(w, h, 1000), w, h, out _, out _), Is.False);
    }

    [Test]
    public void FrameFillingBright_ReturnsFalse() {
        int w = 64, h = 64;
        var a = Field(w, h, 50000);   // all bright, no limb on-frame
        Assert.That(DiskFit.TryFindCenter(a, w, h, out _, out _), Is.False);
    }

    [Test]
    public void TimelapseAlign_UsesTheLimbFit_ToCenterACrescent() {
        int w = 160, h = 120;
        var a = Field(w, h, 1000);
        FillDisc(a, w, h, 70, 60, 30, 50000);
        FillDisc(a, w, h, 92, 60, 18, 1000);        // bite
        var (dx, dy) = TimelapseAlign.CenterOffset(a, w, h);
        // Sun center (70,60) -> frame center (80,60): shift ~ (+10, 0).
        Assert.That(dx, Is.EqualTo(10).Within(3));
        Assert.That(dy, Is.EqualTo(0).Within(3));
    }
}
