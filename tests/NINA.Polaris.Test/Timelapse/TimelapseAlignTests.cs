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

// The centering decision for the time-lapse builder is a pure luminance->offset
// function (the Skia pixel shift is glue), so it runs on any host.
[TestFixture]
public class TimelapseAlignTests {

    // A bright filled disc of radius r at (cx,cy) on a dim background.
    private static ushort[] Disc(int w, int h, int bg, int cx, int cy, int r, int fg) {
        var a = new ushort[w * h];
        for (int i = 0; i < a.Length; i++) a[i] = (ushort)bg;
        for (int y = cy - r; y <= cy + r; y++) {
            if (y < 0 || y >= h) continue;
            for (int x = cx - r; x <= cx + r; x++) {
                if (x < 0 || x >= w) continue;
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r) a[y * w + x] = (ushort)fg;
            }
        }
        return a;
    }

    [Test]
    public void OffCenterDisc_ShiftsItToTheCenter() {
        int w = 100, h = 80;
        var lum = Disc(w, h, bg: 1000, cx: 30, cy: 20, r: 8, fg: 50000);
        var (dx, dy) = TimelapseAlign.CenterOffset(lum, w, h);
        // Target center is (50, 40); disc is at (30, 20) -> shift ~ (+20, +20).
        Assert.That(dx, Is.EqualTo(20).Within(1));
        Assert.That(dy, Is.EqualTo(20).Within(1));
        // And applying the shift lands the disc centroid on the center.
        Assert.That(30 + dx, Is.EqualTo(50).Within(1));
        Assert.That(20 + dy, Is.EqualTo(40).Within(1));
    }

    [Test]
    public void CenteredDisc_NeedsNoShift() {
        int w = 100, h = 80;
        var lum = Disc(w, h, bg: 1000, cx: 50, cy: 40, r: 10, fg: 50000);
        var (dx, dy) = TimelapseAlign.CenterOffset(lum, w, h);
        Assert.That(dx, Is.EqualTo(0).Within(1));
        Assert.That(dy, Is.EqualTo(0).Within(1));
    }

    [Test]
    public void FrameFillingSubject_IsLeftAlone() {
        int w = 64, h = 64;
        var lum = new ushort[w * h];
        for (int i = 0; i < lum.Length; i++) lum[i] = 50000;   // fills the frame
        Assert.That(TimelapseAlign.CenterOffset(lum, w, h), Is.EqualTo((0, 0)));
    }

    [Test]
    public void EmptyFrame_IsLeftAlone() {
        int w = 64, h = 64;
        var lum = new ushort[w * h];
        for (int i = 0; i < lum.Length; i++) lum[i] = 1000;    // uniform, no subject
        Assert.That(TimelapseAlign.CenterOffset(lum, w, h), Is.EqualTo((0, 0)));
    }

    [Test]
    public void ShortBufferOrBadSize_ReturnsZero() {
        Assert.That(TimelapseAlign.CenterOffset(new ushort[10], 100, 80), Is.EqualTo((0, 0)));
        Assert.That(TimelapseAlign.CenterOffset(null!, 100, 80), Is.EqualTo((0, 0)));
    }
}
