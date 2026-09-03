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
using NINA.Image.Editor;

namespace NINA.Polaris.Test;

/// <summary>Unit tests for the pure nightscape-stack primitives: the horizon
/// coverage map and the 16-bit masked composite.</summary>
[TestFixture]
public class NightscapeStackTests {

    // ----- HorizonMask.BuildCoverage -----

    [Test]
    public void Horizon_FlatMidline_HardEdge_SplitsSkyAndForeground() {
        int w = 8, h = 10;
        // horizon across the middle (y = 0.5 -> row 4.5 of 0..9)
        var line = new (double, double)[] { (0.0, 0.5), (1.0, 0.5) };
        var cov = HorizonMask.BuildCoverage(line, w, h, featherPx: 0);
        for (int x = 0; x < w; x++) {
            Assert.That(cov[0 * w + x], Is.EqualTo(0f), "top row is sky");
            Assert.That(cov[9 * w + x], Is.EqualTo(1f), "bottom row is foreground");
            // row 4 is above 4.5 -> sky; row 5 is below -> foreground
            Assert.That(cov[4 * w + x], Is.EqualTo(0f));
            Assert.That(cov[5 * w + x], Is.EqualTo(1f));
        }
    }

    [Test]
    public void Horizon_Feather_IsMonotonicAcrossTheBand() {
        int w = 4, h = 40;
        var line = new (double, double)[] { (0.0, 0.5), (1.0, 0.5) };
        var cov = HorizonMask.BuildCoverage(line, w, h, featherPx: 6);
        // Down a single column, coverage never decreases.
        float prev = -1;
        for (int y = 0; y < h; y++) {
            float c = cov[y * w + 0];
            Assert.That(c, Is.GreaterThanOrEqualTo(prev));
            Assert.That(c, Is.InRange(0f, 1f));
            prev = c;
        }
        Assert.That(cov[0], Is.EqualTo(0f), "well above the band is pure sky");
        Assert.That(cov[(h - 1) * w], Is.EqualTo(1f), "well below the band is pure foreground");
    }

    [Test]
    public void Horizon_SlopedLine_FollowsThePerColumnHeight() {
        int w = 10, h = 10;
        // line from top-left (y=0.1) to bottom-right (y=0.9)
        var line = new (double, double)[] { (0.0, 0.1), (1.0, 0.9) };
        var cov = HorizonMask.BuildCoverage(line, w, h, featherPx: 0);
        // Left column: horizon high (small y) -> most of the column is foreground.
        int fgLeft = CountForeground(cov, w, h, 0);
        int fgRight = CountForeground(cov, w, h, w - 1);
        Assert.That(fgLeft, Is.GreaterThan(fgRight),
            "a horizon that rises left-to-right leaves more landscape on the left");
    }

    [Test]
    public void Horizon_EmptyLine_IsAllSky() {
        var cov = HorizonMask.BuildCoverage(System.Array.Empty<(double, double)>(), 5, 5, 3);
        Assert.That(cov, Has.All.EqualTo(0f));
    }

    private static int CountForeground(float[] cov, int w, int h, int col) {
        int n = 0;
        for (int y = 0; y < h; y++) if (cov[y * w + col] >= 0.5f) n++;
        return n;
    }

    // ----- NightscapeBlend.Composite16 -----

    [Test]
    public void Blend_CoverageZero_IsPureSky() {
        int w = 2, h = 2;
        var sky = new ushort[] { 100, 200, 300, 400 };
        var fg = new ushort[] { 9, 9, 9, 9 };
        var cov = new float[4];   // all zero
        var outBuf = NightscapeBlend.Composite16(sky, fg, cov, w, h, 1);
        Assert.That(outBuf, Is.EqualTo(sky));
    }

    [Test]
    public void Blend_CoverageOne_IsPureForeground() {
        int w = 2, h = 2;
        var sky = new ushort[] { 100, 200, 300, 400 };
        var fg = new ushort[] { 9, 8, 7, 6 };
        var cov = new float[] { 1, 1, 1, 1 };
        var outBuf = NightscapeBlend.Composite16(sky, fg, cov, w, h, 1);
        Assert.That(outBuf, Is.EqualTo(fg));
    }

    [Test]
    public void Blend_CoverageHalf_IsTheAverage() {
        int w = 1, h = 1;
        var sky = new ushort[] { 1000 };
        var fg = new ushort[] { 2000 };
        var cov = new float[] { 0.5f };
        var outBuf = NightscapeBlend.Composite16(sky, fg, cov, w, h, 1);
        Assert.That(outBuf[0], Is.EqualTo(1500));
    }

    [Test]
    public void Blend_ThreeChannelsPlanar_BlendPerPlane() {
        int w = 1, h = 1;
        // planar: [R][G][B]
        var sky = new ushort[] { 0, 0, 0 };
        var fg = new ushort[] { 300, 600, 900 };
        var cov = new float[] { 0.5f };
        var outBuf = NightscapeBlend.Composite16(sky, fg, cov, w, h, 3);
        Assert.That(outBuf, Is.EqualTo(new ushort[] { 150, 300, 450 }));
    }

    [Test]
    public void Blend_LengthMismatch_Throws() {
        Assert.Throws<System.ArgumentException>(() =>
            NightscapeBlend.Composite16(new ushort[3], new ushort[4], new float[4], 2, 2, 1));
        Assert.Throws<System.ArgumentException>(() =>
            NightscapeBlend.Composite16(new ushort[4], new ushort[4], new float[3], 2, 2, 1));
    }
}
