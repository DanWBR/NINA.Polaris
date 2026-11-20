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

using System.Linq;
using NUnit.Framework;
using NINA.Core.Enum;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageAnalysis;

namespace NINA.Polaris.Test;

/// <summary>
/// FitsPreview is the pixel half of the FILES viewer's FITS preview, shared by
/// the host and the browser-wasm offline viewer; FitsHeaderGroups builds the
/// header panel for both. These pin that the shared code produces what the
/// host produced before it was shared.
/// </summary>
[TestFixture]
public class FitsPreviewTests {

    private static ushort[] Gradient(int w, int h, int planes = 1) {
        var px = new ushort[w * h * planes];
        for (int p = 0; p < planes; p++)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[p * w * h + y * w + x] = (ushort)(1000 + 40 * x + 25 * y + 3000 * p);
        return px;
    }

    [Test]
    public void Mono_IsTheAutoStretchOfTheDownsample_InGrayRgba() {
        int w = 400, h = 300;
        var px = Gradient(w, h);
        var (rgba, ow, oh) = FitsPreview.RenderRgba(px, w, h, 1, 16, BayerPatternEnum.None, null, maxDim: 100);

        var (dp, dw, dh) = FitsPreview.Downsample(px, w, h, 1, 100);
        var expected = AutoStretch.Apply(dp, dw, dh, 16);
        Assert.That((ow, oh), Is.EqualTo((dw, dh)));
        Assert.That(rgba.Length, Is.EqualTo(dw * dh * 4));
        for (int i = 0; i < expected.Length; i++) {
            Assert.That(rgba[i * 4], Is.EqualTo(expected[i]));
            Assert.That(rgba[i * 4 + 1], Is.EqualTo(expected[i]));
            Assert.That(rgba[i * 4 + 2], Is.EqualTo(expected[i]));
            Assert.That(rgba[i * 4 + 3], Is.EqualTo(255));
        }
    }

    [Test]
    public void Downsample_NeverDropsBelowTheTarget() {
        var (_, w, h) = FitsPreview.Downsample(new ushort[6000 * 4000], 6000, 4000, 1, 2400);
        Assert.That(System.Math.Max(w, h), Is.GreaterThanOrEqualTo(2400));
        var (same, sw, sh) = FitsPreview.Downsample(new ushort[100 * 50], 100, 50, 1, 0);
        Assert.That((sw, sh), Is.EqualTo((100, 50)));
        Assert.That(same.Length, Is.EqualTo(100 * 50));
    }

    [Test]
    public void BayerOverride_TurnsAMonoBufferIntoColour() {
        int w = 64, h = 48;
        var px = Gradient(w, h);
        // Make the R sites of an RGGB mosaic much brighter, so that the
        // debayered red channel stands out from green and blue.
        for (int y = 0; y < h; y += 2) for (int x = 0; x < w; x += 2) px[y * w + x] += 20000;

        var (mono, _, _) = FitsPreview.RenderRgba(px, w, h, 1, 16, BayerPatternEnum.None, null, 0);
        var (color, _, _) = FitsPreview.RenderRgba(px, w, h, 1, 16, BayerPatternEnum.None, BayerPatternEnum.RGGB, 0);

        bool monoIsGray = Enumerable.Range(0, w * h).All(i => mono[i * 4] == mono[i * 4 + 1] && mono[i * 4 + 1] == mono[i * 4 + 2]);
        bool colorIsGray = Enumerable.Range(0, w * h).All(i => color[i * 4] == color[i * 4 + 1] && color[i * 4 + 1] == color[i * 4 + 2]);
        Assert.That(monoIsGray, Is.True);
        Assert.That(colorIsGray, Is.False);
    }

    [Test]
    public void Rgb_IsEachPlaneBinnedAndStretchedOnItsOwn() {
        int w = 300, h = 200;
        var px = Gradient(w, h, 3);
        var (rgba, ow, oh) = FitsPreview.RenderRgba(px, w, h, 3, 16, BayerPatternEnum.None, null, maxDim: 100);

        var (dp, dw, dh) = FitsPreview.Downsample(px, w, h, 3, 100);
        Assert.That((ow, oh), Is.EqualTo((dw, dh)));
        int n = dw * dh;
        for (int c = 0; c < 3; c++) {
            var plane = dp.Skip(c * n).Take(n).ToArray();
            var p = AutoStretch.ComputeAutoStretchParams(plane, dw, dh, 16);
            var expected = AutoStretch.ApplyManual(plane, dw, dh, p.Black, p.Mid, p.White, 16);
            for (int i = 0; i < n; i++) Assert.That(rgba[i * 4 + c], Is.EqualTo(expected[i]));
        }
    }

    [TestCase(BayerPatternEnum.RGGB, 0, 3)]
    [TestCase(BayerPatternEnum.BGGR, 3, 0)]
    [TestCase(BayerPatternEnum.GRBG, 1, 2)]
    [TestCase(BayerPatternEnum.GBRG, 2, 1)]
    public void Superpixel_TakesRAndBFromTheirSitesAndAveragesTheGreens(
            BayerPatternEnum pattern, int rSite, int bSite) {
        // One 2x2 cell per output pixel; sites are 0 = (0,0), 1 = (1,0), 2 = (0,1), 3 = (1,1).
        int w = 5, h = 3;   // odd sizes: the last column and row are dropped
        var cfa = new ushort[w * h];
        var site = new[] { 0, 1, w, w + 1 };
        for (int k = 0; k < 4; k++) cfa[site[k]] = (ushort)(100 * (k + 1));

        var (ch, ow, oh) = BayerDebayer.Superpixel(cfa, w, h, pattern);
        Assert.That((ow, oh), Is.EqualTo((2, 1)));
        int greens = Enumerable.Range(0, 4).Where(k => k != rSite && k != bSite).Sum(k => 100 * (k + 1));
        Assert.That(ch.R[0], Is.EqualTo(100 * (rSite + 1)));
        Assert.That(ch.B[0], Is.EqualTo(100 * (bSite + 1)));
        Assert.That(ch.G[0], Is.EqualTo(greens / 2));
    }

    [Test]
    public void LargeCfa_IsRenderedFromSuperpixelsAndStillMeetsTheTarget() {
        int w = 1000, h = 600;
        var px = Gradient(w, h);
        var (rgba, ow, oh) = FitsPreview.RenderRgba(px, w, h, 1, 16, BayerPatternEnum.RGGB, null, maxDim: 240);
        // 1000x600 -> 500x300 superpixels -> binned by 2 -> 250x150, still >= 240.
        Assert.That((ow, oh), Is.EqualTo((250, 150)));
        Assert.That(rgba.Length, Is.EqualTo(250 * 150 * 4));
    }

    [TestCase("rggb", BayerPatternEnum.RGGB)]
    [TestCase(" GRBG ", BayerPatternEnum.GRBG)]
    public void ParseBayer_ReadsTheDropdownValues(string value, BayerPatternEnum expected)
        => Assert.That(FitsPreview.ParseBayer(value), Is.EqualTo(expected));

    [TestCase("")]
    [TestCase(null)]
    [TestCase("XYZ")]
    public void ParseBayer_EmptyOrUnknownMeansUseTheFile(string? value)
        => Assert.That(FitsPreview.ParseBayer(value), Is.Null);

    [Test]
    public void HeaderGroups_SortCardsIntoThePanelGroups() {
        var cards = new[] {
            new FITSHeaderCard("SIMPLE", "T"),
            new FITSHeaderCard("OBJECT", "'M 42    '", "target"),
            new FITSHeaderCard("EXPTIME", "120"),
            new FITSHeaderCard("GAIN", "100"),
            new FITSHeaderCard("SITELAT", "-22.9"),
            new FITSHeaderCard("SWCREATE", "'Polaris'"),
            new FITSHeaderCard("MYKEY", "1"),
            new FITSHeaderCard("END", ""),
        };
        var groups = FitsHeaderGroups.Build(cards);

        Assert.That(groups.Select(g => g.Name), Is.EqualTo(new[] {
            "Observation", "Instrument", "Image", "Site & Weather", "Processing", "Other" }));
        string[] Keys(string name) => groups.Single(g => g.Name == name).Cards.Select(c => c.Keyword).ToArray();
        Assert.That(Keys("Observation"), Is.EqualTo(new[] { "EXPTIME", "OBJECT" }));
        Assert.That(Keys("Instrument"), Is.EqualTo(new[] { "GAIN" }));
        Assert.That(Keys("Image"), Is.EqualTo(new[] { "SIMPLE" }));
        Assert.That(Keys("Site & Weather"), Is.EqualTo(new[] { "SITELAT" }));
        Assert.That(Keys("Processing"), Is.EqualTo(new[] { "SWCREATE" }));
        Assert.That(Keys("Other"), Is.EqualTo(new[] { "MYKEY" }));
        var obj = groups.Single(g => g.Name == "Observation").Cards.Single(c => c.Keyword == "OBJECT");
        Assert.That(obj.Comment, Is.EqualTo("target"));
    }
}
