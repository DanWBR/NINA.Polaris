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
public class PhaseCorrelationAlignerTests {

    // A broadband "surface": several sinusoids so the frame is textured
    // everywhere (like a lunar close-up full of craters), never a single spot.
    private static ushort[] Texture(int w, int h) {
        var px = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                double v = 32000
                    + 9000 * Math.Sin(0.071 * x)
                    + 7000 * Math.Cos(0.053 * y)
                    + 6000 * Math.Sin(0.101 * (x + y))
                    + 5000 * Math.Cos(0.037 * (x - y))
                    + 3000 * Math.Sin(0.191 * x + 0.13 * y);
                px[y * w + x] = (ushort)Math.Clamp(v, 0, 65535);
            }
        return px;
    }

    // cur(x,y) = ref(x - dx, y - dy): a circular roll of the whole frame, so the
    // content moves by (+dx,+dy) with no missing data.
    private static ushort[] Roll(ushort[] src, int w, int h, int dx, int dy) {
        var px = new ushort[w * h];
        for (int y = 0; y < h; y++) {
            int sy = ((y - dy) % h + h) % h;
            for (int x = 0; x < w; x++) {
                int sx = ((x - dx) % w + w) % w;
                px[y * w + x] = src[sy * w + sx];
            }
        }
        return px;
    }

    [Test]
    public void Align_RecoversKnownShift_WithStackerSign() {
        // The frame is bigger than the 256-ROI so the central crop is interior:
        // a global roll shifts the ROI content cleanly, no window/seam mismatch.
        const int w = 384, h = 384;
        var refF = Texture(w, h);
        const int Dx = 7, Dy = -5;               // cur = ref shifted by (+Dx,+Dy)
        var cur = Roll(refF, w, h, Dx, Dy);

        var pc = new PhaseCorrelationAligner(refF, w, h);
        var (dx, dy) = pc.Align(cur);

        // Stacker convention: it samples the frame at (x - dx, y - dy) to place at
        // (x, y). Reproducing ref from cur therefore needs dx = -Dx, dy = -Dy
        // (cur(x - dx) = ref(x - dx - Dx) = ref(x) iff dx = -Dx).
        Assert.That(dx, Is.EqualTo(-Dx).Within(1));
        Assert.That(dy, Is.EqualTo(-Dy).Within(1));
    }

    [Test]
    public void Align_IdenticalFrame_IsZeroShift() {
        const int w = 320, h = 320;
        var refF = Texture(w, h);
        var pc = new PhaseCorrelationAligner(refF, w, h);
        var (dx, dy) = pc.Align((ushort[])refF.Clone());
        Assert.That(dx, Is.EqualTo(0));
        Assert.That(dy, Is.EqualTo(0));
    }

    [Test]
    public void Align_StackingTwoShiftedFrames_ReconstructsInteriorSharply() {
        // End-to-end: applying the returned shift to the moved frame and
        // comparing to the reference over the interior must match closely.
        const int w = 384, h = 384;
        var refF = Texture(w, h);
        const int Dx = -9, Dy = 6;
        var cur = Roll(refF, w, h, Dx, Dy);
        var pc = new PhaseCorrelationAligner(refF, w, h);
        var (dx, dy) = pc.Align(cur);

        long err = 0; int n = 0;
        for (int y = 40; y < h - 40; y++)
            for (int x = 40; x < w - 40; x++) {
                int sx = x - dx, sy = y - dy;
                if (sx < 0 || sx >= w || sy < 0 || sy >= h) continue;
                err += Math.Abs(cur[sy * w + sx] - refF[y * w + x]);
                n++;
            }
        double mad = (double)err / Math.Max(1, n);
        Assert.That(mad, Is.LessThan(1.0), "aligned frame must reproduce the reference");
    }

    [Test]
    public void FillFraction_FilledFrame_IsHigh() {
        // ~Every pixel saturated, a sprinkling darker: the real lunar close-up.
        const int w = 128, h = 128;
        var f = new ushort[w * h];
        for (int i = 0; i < f.Length; i++) f[i] = (ushort)((i % 100 == 0) ? 40000 : 65535);
        Assert.That(CentroidAligner.FillFraction(f, w, h), Is.GreaterThan(0.9));
    }

    [Test]
    public void FillFraction_BoundedDiscOnSky_IsLow() {
        // A disc with dark sky around it (radius 40 in 200²) — centroid territory.
        const int w = 200, h = 200, r = 40;
        var f = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                int dx = x - 100, dy = y - 100;
                f[y * w + x] = (ushort)(dx * dx + dy * dy <= r * r ? 3900 : 40);
            }
        Assert.That(CentroidAligner.FillFraction(f, w, h), Is.LessThan(0.6));
    }
}
