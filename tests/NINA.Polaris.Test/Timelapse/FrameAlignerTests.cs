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

// The time-lapse aligner picks between centering a bounded disc and stabilizing
// a frame-filling surface, mirroring the planetary stacker's split. These test
// the pure Offset() decision (Skia-free) against synthetic luminance buffers.
[TestFixture]
public class FrameAlignerTests {

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

    // A textured "surface" (a few craters as Gaussian blobs) shifted by (sx,sy):
    // phase correlation locks onto this cleanly.
    private static ushort[] Craters(int w, int h, int sx, int sy) {
        var spots = new (double x, double y)[] {
            (30, 34), (70, 40), (95, 88), (48, 96), (18, 70),
            (84, 20), (60, 66), (108, 54), (40, 18), (24, 108),
        };
        int bg = 15000, amp = 25000; double sigma = 3.0, twoS2 = 2 * sigma * sigma;
        var a = Field(w, h, bg);
        foreach (var (px, py) in spots) {
            double cx = px + sx, cy = py + sy;
            int r = 10;
            for (int y = (int)(cy - r); y <= cy + r; y++) {
                if (y < 0 || y >= h) continue;
                for (int x = (int)(cx - r); x <= cx + r; x++) {
                    if (x < 0 || x >= w) continue;
                    double dx = x - cx, dy = y - cy;
                    double v = a[y * w + x] + amp * Math.Exp(-(dx * dx + dy * dy) / twoS2);
                    a[y * w + x] = (ushort)Math.Min(65535, v);
                }
            }
        }
        return a;
    }

    // A bright surface that fills the whole frame (no sky, no bounded disc):
    // FillFraction >= 0.6, so auto resolves to stabilize.
    private static ushort[] FilledSurface(int w, int h) {
        var a = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                a[y * w + x] = (ushort)(48000 + (x * 7 + y * 13) % 8000);
        return a;
    }

    [Test]
    public void Off_NeverShifts_AndReportsDisabled() {
        var fa = new FrameAligner("off");
        Assert.That(fa.Enabled, Is.False);
        Assert.That(fa.Offset(FilledSurface(64, 64), 64, 64, 0), Is.EqualTo((0, 0)));
    }

    [Test]
    public void Center_MovesBoundedDiscToTheMiddle() {
        int w = 140, h = 110;
        var a = Field(w, h, 1000);
        FillDisc(a, w, h, 40, 70, 22, 50000);   // off-center disc
        var fa = new FrameAligner("center");
        var (dx, dy) = fa.Offset(a, w, h, 0);
        Assert.That(dx, Is.EqualTo(30).Within(3));   // 70 - 40
        Assert.That(dy, Is.EqualTo(-15).Within(3));  // 55 - 70
    }

    [Test]
    public void Stabilize_CancelsAKnownShiftBackOntoFrameZero() {
        int w = 128, h = 128, sx = 4, sy = -3;
        var f0 = Craters(w, h, 0, 0);
        var f1 = Craters(w, h, sx, sy);   // same surface, shifted by (sx,sy)
        var fa = new FrameAligner("stabilize");

        Assert.That(fa.Offset(f0, w, h, 0), Is.EqualTo((0, 0)), "frame 0 is the reference");
        var (dx, dy) = fa.Offset(f1, w, h, 1);
        // dst = src + (dx,dy) registers f1 back onto f0, so the shift is negated.
        Assert.That(dx, Is.EqualTo(-sx).Within(1));
        Assert.That(dy, Is.EqualTo(-sy).Within(1));
    }

    [Test]
    public void Stabilize_AccumulatesGradualDriftAcrossFrames() {
        // Sequential registration: each frame drifts a small step from the last;
        // the running offset must track the TOTAL drift back to frame 0.
        int w = 128, h = 128;
        var fa = new FrameAligner("stabilize");
        Assert.That(fa.Offset(Craters(w, h, 0, 0), w, h, 0), Is.EqualTo((0, 0)));
        // frame 1 drifted (+2,+1) from 0; frame 2 drifted (+4,+2) from 0.
        var s1 = fa.Offset(Craters(w, h, 2, 1), w, h, 1);
        var s2 = fa.Offset(Craters(w, h, 4, 2), w, h, 2);
        Assert.That(s1.dx, Is.EqualTo(-2).Within(1));
        Assert.That(s1.dy, Is.EqualTo(-1).Within(1));
        Assert.That(s2.dx, Is.EqualTo(-4).Within(1));
        Assert.That(s2.dy, Is.EqualTo(-2).Within(1));
    }

    [Test]
    public void Stabilize_CoastsThroughALowContrastFrame() {
        // A near-textureless frame (a Moon near eclipse totality: no detail to
        // lock onto) gives a diffuse correlation peak. Rather than jump on that
        // noise, the aligner must HOLD the last confident offset.
        int w = 128, h = 128;
        var fa = new FrameAligner("stabilize");
        fa.Offset(Craters(w, h, 0, 0), w, h, 0);
        var s1 = fa.Offset(Craters(w, h, 3, 2), w, h, 1);   // confident: drift (-3,-2)

        // Flat frame with only faint noise -> low PSR -> coast.
        var flat = new ushort[w * h];
        for (int i = 0; i < flat.Length; i++) flat[i] = (ushort)(30000 + (i * 2654435761u % 40));
        var s2 = fa.Offset(flat, w, h, 2);
        Assert.That(s2.dx, Is.EqualTo(s1.dx), "held the last confident dx");
        Assert.That(s2.dy, Is.EqualTo(s1.dy), "held the last confident dy");

        // When texture returns, it re-locks against the last confident frame
        // (frame 1), capturing the real drift across the dim gap.
        var s3 = fa.Offset(Craters(w, h, 6, 4), w, h, 3);
        Assert.That(s3.dx, Is.EqualTo(-6).Within(1));
        Assert.That(s3.dy, Is.EqualTo(-4).Within(1));
    }

    [Test]
    public void Auto_ResolvesToStabilizeForAFilledSurface() {
        int w = 128, h = 128;
        var fa = new FrameAligner("auto");
        fa.Offset(FilledSurface(w, h), w, h, 0);
        Assert.That(fa.Resolved, Is.EqualTo(FrameAligner.Mode.Stabilize));
    }

    [Test]
    public void Auto_ResolvesToCenterForABoundedDisc() {
        int w = 140, h = 110;
        var a = Field(w, h, 1000);
        FillDisc(a, w, h, 40, 70, 22, 50000);
        var fa = new FrameAligner("auto");
        fa.Offset(a, w, h, 0);
        Assert.That(fa.Resolved, Is.EqualTo(FrameAligner.Mode.Center));
    }

    [Test]
    public void LegacyTrue_MapsToCenter() {
        Assert.That(FrameAligner.Parse("true"), Is.EqualTo(FrameAligner.Mode.Center));
        Assert.That(FrameAligner.Parse(null), Is.EqualTo(FrameAligner.Mode.Off));
    }
}
