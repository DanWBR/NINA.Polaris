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
using NINA.Polaris.Services.PostProcess;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the SCNR (green-cast removal) contract at both the math layer
/// (<see cref="Scnr"/>) and the service layer (<see cref="ScnrService"/>).
/// Synthetic FITS in a temp dir, run, read back, assert on pixel values.
/// </summary>
[TestFixture]
public class ScnrServiceTests {

    private string _tmpDir = "";
    private ScnrService _svc = null!;

    [SetUp]
    public void SetUp() {
        _tmpDir = Path.Combine(Path.GetTempPath(), "polaris-scnr-" + Guid.NewGuid());
        Directory.CreateDirectory(_tmpDir);
        _svc = new ScnrService(NullLogger<ScnrService>.Instance);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    [Test]
    public void AverageNeutral_PullsGreenToMeanOfRedBlue() {
        // Every pixel is a green-dominant grey-green: r=20000, g=50000,
        // b=24000. average-neutral clamps green to (r+b)/2 = 22000.
        var src = MakeRgb(6, 6, (_, _) => 20000, (_, _) => 50000, (_, _) => 24000);
        var path = WriteFits(src, "green.fits");

        var r = _svc.RunFits(path, "average-neutral");

        Assert.That(r.Channels, Is.EqualTo(3));
        Assert.That(r.PixelsChanged, Is.EqualTo(36));
        var got = ReadFits(r.OutputPath);
        int plane = 6 * 6;
        for (int i = 0; i < plane; i++) {
            Assert.That(got.Data[i], Is.EqualTo(20000), "R must be untouched");
            Assert.That(got.Data[2 * plane + i], Is.EqualTo(24000), "B must be untouched");
            // (20000+24000)/2 = 22000, allow ±1 for round-trip.
            Assert.That(got.Data[plane + i], Is.EqualTo(22000).Within(1),
                "G must be clamped to mean(R,B)");
        }
    }

    [Test]
    public void MaximumNeutral_PullsGreenToMaxOfRedBlue() {
        var src = MakeRgb(4, 4, (_, _) => 20000, (_, _) => 50000, (_, _) => 24000);
        var path = WriteFits(src, "green2.fits");

        var r = _svc.RunFits(path, "maximum-neutral");

        var got = ReadFits(r.OutputPath);
        int plane = 4 * 4;
        // max(20000,24000) = 24000.
        for (int i = 0; i < plane; i++)
            Assert.That(got.Data[plane + i], Is.EqualTo(24000).Within(1));
    }

    [Test]
    public void GreenNotDominant_LeavesGreenUnchanged() {
        // Green is already below mean(R,B); SCNR must be a no-op on it.
        var src = MakeRgb(4, 4, (_, _) => 40000, (_, _) => 10000, (_, _) => 40000);
        var path = WriteFits(src, "nogreen.fits");

        var r = _svc.RunFits(path, "average-neutral");

        Assert.That(r.PixelsChanged, Is.EqualTo(0));
        var got = ReadFits(r.OutputPath);
        int plane = 4 * 4;
        for (int i = 0; i < plane; i++)
            Assert.That(got.Data[plane + i], Is.EqualTo(10000));
    }

    [Test]
    public void Mono_IsNoOp() {
        var src = MakeMono(8, 8, (x, y) => (ushort)(y * 8 + x));
        var path = WriteFits(src, "mono.fits");

        var r = _svc.RunFits(path, "average-neutral");

        Assert.That(r.Channels, Is.EqualTo(1));
        Assert.That(r.PixelsChanged, Is.EqualTo(0));
        var got = ReadFits(r.OutputPath);
        for (int i = 0; i < 8 * 8; i++)
            Assert.That(got.Data[i], Is.EqualTo(src.Data[i]));
    }

    [Test]
    public void OutputPath_IsSiblingWithSuffix() {
        var src = MakeRgb(4, 4, (_, _) => 10000, (_, _) => 30000, (_, _) => 10000);
        var path = WriteFits(src, "img.fits");

        var r = _svc.RunFits(path, "average-neutral");

        Assert.That(r.OutputPath.EndsWith("_scnr.fits"), Is.True);
        Assert.That(File.Exists(r.OutputPath), Is.True);
    }

    [Test]
    public void MissingFile_Throws() {
        Assert.Throws<FileNotFoundException>(() =>
            _svc.RunFits(Path.Combine(_tmpDir, "nope.fits"), "average-neutral"));
    }

    [Test]
    public void Math_MaximumMask_BlendsByAmount() {
        // With amount = 0 the masked modes are a full no-op (green kept);
        // with amount = 1 green is pulled toward the mask. Assert monotonic.
        var lo = new ushort[] { 20000, 50000, 24000 };
        var hi = (ushort[])lo.Clone();
        Scnr.Apply(lo, 1, 1, 3, Scnr.ScnrMode.MaximumMask, amount: 0.0);
        Scnr.Apply(hi, 1, 1, 3, Scnr.ScnrMode.MaximumMask, amount: 1.0);
        // amount=0 keeps green (m<1 so the (1-a)(1-m) term is full weight).
        Assert.That(lo[1], Is.EqualTo(50000).Within(2));
        Assert.That(hi[1], Is.LessThan(lo[1]), "amount=1 must dim green more than amount=0");
    }

    // ---- helpers ----------------------------------------------------

    private static BaseImageData MakeMono(int w, int h, Func<int, int, ushort> pixel) {
        var data = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                data[y * w + x] = pixel(x, y);
        var props = new ImageProperties { Width = w, Height = h, BitDepth = 16, Channels = 1 };
        return new BaseImageData(data, props);
    }

    private static BaseImageData MakeRgb(int w, int h,
            Func<int, int, ushort> r, Func<int, int, ushort> g, Func<int, int, ushort> b) {
        var data = new ushort[w * h * 3];
        int plane = w * h;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                data[y * w + x] = r(x, y);
                data[plane + y * w + x] = g(x, y);
                data[2 * plane + y * w + x] = b(x, y);
            }
        var props = new ImageProperties { Width = w, Height = h, BitDepth = 16, Channels = 3 };
        return new BaseImageData(data, props);
    }

    private string WriteFits(BaseImageData img, string name) {
        var path = Path.Combine(_tmpDir, name);
        FITSWriter.Write(img, path);
        return path;
    }

    private static BaseImageData ReadFits(string path) {
        using var fs = File.OpenRead(path);
        return FITSReader.Read(fs);
    }
}
