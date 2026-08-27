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

using NINA.Core.Enum;
using NINA.Image.ImageAnalysis;
using NINA.Polaris.Services.StarTrail;
using NUnit.Framework;

namespace NINA.Polaris.Test.StarTrail;

// The one genuinely new bit of star-trail maths is the per-pixel MAX (lighten)
// blend + the mono/OSC materialize. Both are Skia-free, so they run on any host.
[TestFixture]
public class StarTrailMathTests {

    [Test]
    public void MaxInto_KeepsTheBrightestPerPixel() {
        var acc = new ushort[] { 0, 100, 5000, 200 };
        StarTrailService.MaxInto(acc, new ushort[] { 10, 50, 4000, 250 });
        Assert.That(acc, Is.EqualTo(new ushort[] { 10, 100, 5000, 250 }));
    }

    [Test]
    public void MaxInto_AcrossFrames_BuildsTheTrail() {
        // A star at a different pixel each frame: the composite lights up every
        // position it ever occupied (that IS the trail), and never dims one.
        int n = 6;
        var acc = new ushort[n];
        var frames = new[] {
            new ushort[] { 60000, 0, 0, 0, 0, 0 },
            new ushort[] { 0, 60000, 0, 0, 0, 0 },
            new ushort[] { 0, 0, 60000, 0, 0, 0 },
        };
        foreach (var f in frames) StarTrailService.MaxInto(acc, f);
        Assert.That(acc, Is.EqualTo(new ushort[] { 60000, 60000, 60000, 0, 0, 0 }));
    }

    [Test]
    public void BuildComposite_Mono_PassesThroughAsSingleChannel() {
        var max = new ushort[] { 1, 2, 3, 4 };
        var img = StarTrailService.BuildComposite(max, 2, 2, BayerPatternEnum.None, null);
        Assert.That(img.Properties.Channels, Is.EqualTo(1));
        Assert.That(img.Properties.Width, Is.EqualTo(2));
        Assert.That(img.Properties.Height, Is.EqualTo(2));
        Assert.That(img.Properties.IsBayered, Is.False);
        Assert.That(img.Data, Is.EqualTo(max));
    }

    [Test]
    public void BuildComposite_Osc_DebayersToPlanarRgb() {
        // 4x4 RGGB mosaic -> a 3-plane RGB cube (w*h per plane).
        int w = 4, h = 4;
        var mosaic = new ushort[w * h];
        for (int i = 0; i < mosaic.Length; i++) mosaic[i] = (ushort)(1000 + i);
        var img = StarTrailService.BuildComposite(mosaic, w, h, BayerPatternEnum.RGGB, null);
        Assert.That(img.Properties.Channels, Is.EqualTo(3));
        Assert.That(img.Properties.IsBayered, Is.False);
        Assert.That(img.Properties.BayerPattern, Is.EqualTo(BayerPatternEnum.None));
        Assert.That(img.Data.Length, Is.EqualTo(w * h * 3));
    }

    // Max-blend is brutal on fixed hot pixels (a fixed camera means a hot pixel
    // never trails, so it would paint a permanent bright dot). This is why each
    // sub is run through CosmeticCorrection before it is blended: a lone spike in
    // an otherwise flat field is knocked back down toward its neighbours, so it
    // does not dominate the composite.
    [Test]
    public void CosmeticCorrection_KnocksDownAFixedHotPixel_BeforeBlending() {
        int w = 16, h = 16;
        var frame = new ushort[w * h];
        for (int i = 0; i < frame.Length; i++) frame[i] = 1000;   // flat field
        int spike = 8 * w + 8;
        frame[spike] = 60000;                                     // one hot pixel

        var (cold, hot) = CosmeticCorrection.Apply(frame, w, h, 1,
            sigmaCold: 5.0, sigmaHot: 3.0, amount: 1.0, cfa: false);

        Assert.That(hot, Is.GreaterThanOrEqualTo(1), "the hot pixel should be detected");
        Assert.That(frame[spike], Is.LessThan(60000), "and pulled back toward the background");

        // Now the guard pays off: blending the corrected frame doesn't leave a
        // 60000 dot burned into the composite.
        var acc = new ushort[w * h];
        StarTrailService.MaxInto(acc, frame);
        Assert.That(acc[spike], Is.LessThan(60000));
    }
}
