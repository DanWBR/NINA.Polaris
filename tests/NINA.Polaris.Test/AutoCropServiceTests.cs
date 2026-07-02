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

using Microsoft.Extensions.Logging.Abstractions;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins auto-crop: the largest fully-covered inner rectangle is found and the
/// black stacking borders are trimmed. Math layer (<see cref="AutoCrop"/>) +
/// the CropService FITS round-trip.
/// </summary>
[TestFixture]
public class AutoCropServiceTests {

    private string _tmpDir = "";
    private CropService _svc = null!;

    [SetUp]
    public void SetUp() {
        _tmpDir = Path.Combine(Path.GetTempPath(), "polaris-autocrop-" + Guid.NewGuid());
        Directory.CreateDirectory(_tmpDir);
        _svc = new CropService(NullLogger<CropService>.Instance);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    [Test]
    public void FindContentRect_TrimsLeftTopBorder() {
        // 20x20 flat at 1000; the top 3 rows AND left 3 cols are black
        // (uncovered). Largest valid rectangle is the 17x17 block at (3,3).
        int w = 20, h = 20;
        var data = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                data[y * w + x] = (x < 3 || y < 3) ? (ushort)0 : (ushort)1000;

        var r = AutoCrop.FindContentRect(data, w, h, 1);

        Assert.That(r.X, Is.EqualTo(3));
        Assert.That(r.Y, Is.EqualTo(3));
        Assert.That(r.Width, Is.EqualTo(17));
        Assert.That(r.Height, Is.EqualTo(17));
    }

    [Test]
    public void FindContentRect_TrimsBottomBorder() {
        int w = 20, h = 20;
        var data = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                data[y * w + x] = (y >= 17) ? (ushort)0 : (ushort)500;

        var r = AutoCrop.FindContentRect(data, w, h, 1);

        Assert.That(r.X, Is.EqualTo(0));
        Assert.That(r.Y, Is.EqualTo(0));
        Assert.That(r.Width, Is.EqualTo(20));
        Assert.That(r.Height, Is.EqualTo(17));
    }

    [Test]
    public void FindContentRect_NoBorder_ReturnsFullFrame() {
        int w = 12, h = 8;
        var data = new ushort[w * h];
        Array.Fill(data, (ushort)2000);

        var r = AutoCrop.FindContentRect(data, w, h, 1);

        Assert.That(r.X, Is.EqualTo(0));
        Assert.That(r.Y, Is.EqualTo(0));
        Assert.That(r.Width, Is.EqualTo(12));
        Assert.That(r.Height, Is.EqualTo(8));
    }

    [Test]
    public void FindContentRect_Margin_ShrinksInward() {
        int w = 20, h = 20;
        var data = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                data[y * w + x] = (x < 2) ? (ushort)0 : (ushort)1000;
        // Valid rect is (2,0,18,20); a 2px margin shrinks to (4,2,14,16).
        var r = AutoCrop.FindContentRect(data, w, h, 1, threshold: 0, margin: 2);

        Assert.That(r.X, Is.EqualTo(4));
        Assert.That(r.Y, Is.EqualTo(2));
        Assert.That(r.Width, Is.EqualTo(14));
        Assert.That(r.Height, Is.EqualTo(16));
    }

    [Test]
    public void AutoCropFits_WritesCropWithNoBlackBorder() {
        int w = 24, h = 24;
        var data = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                data[y * w + x] = (x < 4 || y < 4 || x >= 20 || y >= 20) ? (ushort)0 : (ushort)3000;
        var src = new BaseImageData(data, new ImageProperties { Width = w, Height = h, BitDepth = 16, Channels = 1 });
        var path = Path.Combine(_tmpDir, "bordered.fits");
        FITSWriter.Write(src, path);

        var r = _svc.AutoCropFits(path);

        Assert.That(r.OutputPath.EndsWith("_crop.fits"), Is.True);
        Assert.That(r.Width, Is.EqualTo(16));
        Assert.That(r.Height, Is.EqualTo(16));
        using var fs = File.OpenRead(r.OutputPath);
        var got = FITSReader.Read(fs);
        foreach (var v in got.Data)
            Assert.That(v, Is.GreaterThan((ushort)0), "no black border pixel should remain");
    }

    [Test]
    public void AutoCropFits_MissingFile_Throws() {
        Assert.Throws<FileNotFoundException>(() =>
            _svc.AutoCropFits(Path.Combine(_tmpDir, "nope.fits")));
    }
}
