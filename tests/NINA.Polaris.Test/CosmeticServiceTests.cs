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
/// Pins cosmetic correction: an injected hot pixel in a flat field is
/// replaced by ~the local level; a cold pixel likewise; clean pixels
/// untouched. Math layer (<see cref="CosmeticCorrection"/>) + service.
/// </summary>
[TestFixture]
public class CosmeticServiceTests {

    private string _tmpDir = "";
    private CosmeticService _svc = null!;

    [SetUp]
    public void SetUp() {
        _tmpDir = Path.Combine(Path.GetTempPath(), "polaris-cc-" + Guid.NewGuid());
        Directory.CreateDirectory(_tmpDir);
        _svc = new CosmeticService(NullLogger<CosmeticService>.Instance);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    [Test]
    public void HotPixel_InFlatField_IsReplacedByLocalLevel() {
        // Flat field at 1000 with a single 60000 hot pixel in the middle.
        int w = 32, h = 32;
        var data = Flat(w, h, 1000);
        data[16 * w + 16] = 60000;

        var (cold, hot) = CosmeticCorrection.Apply(data, w, h, 1, sigmaCold: 5, sigmaHot: 3, amount: 1.0);

        Assert.That(hot, Is.EqualTo(1), "the one hot pixel must be detected");
        Assert.That(cold, Is.EqualTo(0));
        Assert.That(data[16 * w + 16], Is.EqualTo(1000).Within(2),
            "hot pixel replaced by the local average (~1000)");
    }

    [Test]
    public void ColdPixel_InFlatField_IsReplacedByLocalMedian() {
        int w = 32, h = 32;
        var data = Flat(w, h, 30000);
        data[10 * w + 20] = 0; // dead/cold pixel

        var (cold, hot) = CosmeticCorrection.Apply(data, w, h, 1, sigmaCold: 5, sigmaHot: 3, amount: 1.0);

        Assert.That(cold, Is.EqualTo(1));
        Assert.That(data[10 * w + 20], Is.EqualTo(30000).Within(2),
            "cold pixel replaced by the local median (~30000)");
    }

    [Test]
    public void CleanFlatField_IsUntouched() {
        int w = 24, h = 24;
        var data = Flat(w, h, 5000);
        var copy = (ushort[])data.Clone();

        var (cold, hot) = CosmeticCorrection.Apply(data, w, h, 1, sigmaCold: 5, sigmaHot: 3);

        Assert.That(cold + hot, Is.EqualTo(0));
        Assert.That(data, Is.EqualTo(copy), "no pixels should change in a clean flat");
    }

    [Test]
    public void Service_WritesSiblingAndCountsInHeader() {
        int w = 16, h = 16;
        var data = Flat(w, h, 2000);
        data[8 * w + 8] = 65535;
        var src = new BaseImageData(data, new ImageProperties { Width = w, Height = h, BitDepth = 16, Channels = 1 });
        var path = Path.Combine(_tmpDir, "hot.fits");
        FITSWriter.Write(src, path);

        var r = _svc.RunFits(path, sigmaCold: 5, sigmaHot: 3);

        Assert.That(r.OutputPath.EndsWith("_cc.fits"), Is.True);
        Assert.That(r.Hot, Is.EqualTo(1));
        using var fs = File.OpenRead(r.OutputPath);
        var got = FITSReader.Read(fs);
        Assert.That(got.Data[8 * w + 8], Is.EqualTo(2000).Within(2));
    }

    [Test]
    public void Service_MissingFile_Throws() {
        Assert.Throws<FileNotFoundException>(() =>
            _svc.RunFits(Path.Combine(_tmpDir, "nope.fits")));
    }

    private static ushort[] Flat(int w, int h, ushort level) {
        // A perfectly flat field: avgDev is 0 until an outlier is injected,
        // and the outlier itself sets the deviation scale that flags it. A
        // clean flat therefore flags nothing (coldVal==hotVal==level).
        var data = new ushort[w * h];
        for (int i = 0; i < data.Length; i++) data[i] = level;
        return data;
    }
}
