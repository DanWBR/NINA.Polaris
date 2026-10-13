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
using NINA.Camera.CanonEdsdk;
using SkiaSharp;

namespace NINA.Polaris.Test;

[TestFixture]
public class CanonPreviewTests {
    private static byte[] Jpeg(int w, int h, SKColor colour) {
        using var bmp = new SKBitmap(w, h);
        bmp.Erase(colour);
        using var data = bmp.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    /// <summary>A RAW file carries a thumbnail and a full-size JPEG among its
    /// own data; the preview is the largest one.</summary>
    [Test]
    public void LargestEmbeddedJpeg_PicksTheFullSizeOne() {
        var junk = new byte[1000];
        new Random(1).NextBytes(junk);
        var raw = junk.Concat(Jpeg(160, 120, SKColors.Gray))
            .Concat(junk).Concat(Jpeg(600, 400, SKColors.Red))
            .Concat(junk).ToArray();

        using var bmp = CanonPreview.LargestEmbeddedJpeg(raw);

        Assert.That(bmp, Is.Not.Null);
        Assert.That((bmp!.Width, bmp.Height), Is.EqualTo((600, 400)));
    }

    [Test]
    public void LargestEmbeddedJpeg_NoJpeg_IsNull() {
        var junk = new byte[4096];
        new Random(2).NextBytes(junk);
        Assert.That(CanonPreview.LargestEmbeddedJpeg(junk), Is.Null);
    }

    [Test]
    public void ToRggb_KeepsTheChannelEachSiteSamples() {
        using var bmp = new SKBitmap(2, 2);
        bmp.Erase(new SKColor(200, 100, 50));

        var px = CanonPreview.ToRggb(bmp);

        Assert.That(px, Is.EqualTo(new ushort[] { 200 * 257, 100 * 257, 100 * 257, 50 * 257 }));
    }
}
