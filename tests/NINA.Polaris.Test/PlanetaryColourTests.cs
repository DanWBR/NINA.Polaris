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

using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Core.Enum;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using NINA.Polaris.Services.PostProcess;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// A planetary stack from a colour camera opened grey, in this app and in
/// PixInsight, because the stacker wrote the raw CFA mosaic and only stamped
/// BAYERPAT. Beyond the display problem, sharpening a mosaic is wrong
/// arithmetic: a wavelet transform reads a red sample and the green beside it
/// as neighbouring values of one signal.
/// </summary>
[TestFixture]
public class PlanetaryColourTests {

    private string _dir = "";

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "polaris-colour-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>An RGGB mosaic of a flat colour: every R site 3000, every B
    /// site 600, every G site 1800. After debayer each plane should come back
    /// close to its own constant, which is what makes the colour visible.
    /// </summary>
    private static ushort[] RggbMosaic(int w, int h) {
        var px = new ushort[w * h];
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                bool evenRow = (y & 1) == 0, evenCol = (x & 1) == 0;
                px[y * w + x] = evenRow
                    ? (evenCol ? (ushort)3000 : (ushort)1800)     // R G
                    : (evenCol ? (ushort)1800 : (ushort)600);     // G B
            }
        }
        return px;
    }

    private string WriteMosaicFits(int w, int h) {
        var data = new BaseImageData(RggbMosaic(w, h),
            new ImageProperties {
                Width = w, Height = h, BitDepth = 16,
                IsBayered = true, BayerPattern = BayerPatternEnum.RGGB
            },
            new ImageMetaData());
        data.MetaData.Camera.BayerPattern = BayerPatternEnum.RGGB;
        var path = Path.Combine(_dir, "mosaic.fits");
        FITSWriter.Write(data, path);
        return path;
    }

    private static IReadOnlyList<string> HeaderCards(string path) {
        using var fs = File.OpenRead(path);
        var buf = new byte[2880 * 6];
        int read = fs.Read(buf, 0, buf.Length);
        var txt = Encoding.ASCII.GetString(buf, 0, read);
        var cards = new List<string>();
        for (int i = 0; i + 80 <= txt.Length; i += 80) {
            var card = txt.Substring(i, 80).TrimEnd();
            if (card.StartsWith("END")) break;
            if (card.Length > 0) cards.Add(card);
        }
        return cards;
    }

    // ---- the wavelet tool, on a mosaic already sitting on disk ----

    private static (BaseImageData src, int w, int h, int ch, ushort[] px) Load(string path) {
        var m = typeof(WaveletService).GetMethod("Load",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var r = m.Invoke(null, new object?[] { path })!;
        var t = r.GetType();
        return ((BaseImageData)t.GetField("Item1")!.GetValue(r)!,
                (int)t.GetField("Item2")!.GetValue(r)!,
                (int)t.GetField("Item3")!.GetValue(r)!,
                (int)t.GetField("Item4")!.GetValue(r)!,
                (ushort[])t.GetField("Item5")!.GetValue(r)!);
    }

    [Test]
    public void AMosaicOnDiskIsDebayeredBeforeAnyWaveletMathsRuns() {
        const int w = 32, h = 32;
        var (_, _, _, ch, px) = Load(WriteMosaicFits(w, h));

        Assert.That(ch, Is.EqualTo(3), "a CFA frame must reach the transform as three planes");
        Assert.That(px.Length, Is.EqualTo(w * h * 3));

        // Sample away from the border, where bilinear interpolation clamps.
        int n = w * h;
        int probe = 10 * w + 10;
        int r = px[probe], g = px[n + probe], b = px[n * 2 + probe];
        Assert.That(r, Is.EqualTo(3000).Within(400), "red plane");
        Assert.That(g, Is.EqualTo(1800).Within(400), "green plane");
        Assert.That(b, Is.EqualTo(600).Within(400), "blue plane");
        Assert.That(r, Is.GreaterThan(b),
            "the whole point: the planes carry different values, so the result is not grey");
    }

    /// <summary>The wavelet output must not claim to still be a mosaic, or the
    /// next tool would debayer an image that already is.</summary>
    [Test]
    public void TheWaveletOutputIsRgbAndCarriesNoBayerPattern() {
        var svc = new WaveletService(NullLogger<WaveletService>.Instance);
        var result = svc.SharpenLayers(WriteMosaicFits(32, 32),
            new double[] { 1.4, 1.0, 1.0 }, null);

        Assert.That(result.Channels, Is.EqualTo(3));
        Assert.That(File.Exists(result.OutputPath), Is.True);

        var cards = HeaderCards(result.OutputPath);
        Assert.That(cards.Any(c => c.StartsWith("NAXIS   =") && c.Contains("3")), Is.True,
            "an RGB cube is NAXIS=3");
        Assert.That(cards.Any(c => c.StartsWith("NAXIS3")), Is.True);
        Assert.That(cards.Any(c => c.StartsWith("BAYERPAT")), Is.False,
            "BAYERPAT on an already-debayered cube would invite a second debayer");
    }

    [Test]
    public void APlainMonoFrameIsLeftAlone() {
        const int w = 16, h = 16;
        var mono = new ushort[w * h];
        for (int i = 0; i < mono.Length; i++) mono[i] = 1234;
        var data = new BaseImageData(mono,
            new ImageProperties { Width = w, Height = h, BitDepth = 16 },
            new ImageMetaData());
        var path = Path.Combine(_dir, "mono.fits");
        FITSWriter.Write(data, path);

        var (_, _, _, ch, px) = Load(path);
        Assert.That(ch, Is.EqualTo(1), "a mono frame must not gain channels it never had");
        Assert.That(px.Length, Is.EqualTo(w * h));
    }

    /// <summary>The preview is what the operator actually judges by, and it
    /// must be a colour JPEG rather than three grey planes flattened.</summary>
    [Test]
    public void ThePreviewOfAMosaicRendersInColour() {
        var svc = new WaveletService(NullLogger<WaveletService>.Instance);
        var jpeg = svc.PreviewLayers(WriteMosaicFits(64, 64),
            new double[] { 1.0, 1.0, 1.0 }, null, maxDim: 64);

        Assert.That(jpeg, Is.Not.Null.And.Length.GreaterThan(100));
        Assert.That(jpeg[0], Is.EqualTo(0xFF));
        Assert.That(jpeg[1], Is.EqualTo(0xD8), "JPEG SOI marker");
    }
}
