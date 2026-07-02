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
/// Pins CLAHE (local contrast) + highlight recovery (soft-knee). Math layer +
/// service round-trip.
/// </summary>
[TestFixture]
public class TonalServiceTests {

    private string _tmpDir = "";
    private TonalService _svc = null!;

    [SetUp]
    public void SetUp() {
        _tmpDir = Path.Combine(Path.GetTempPath(), "polaris-tonal-" + Guid.NewGuid());
        Directory.CreateDirectory(_tmpDir);
        _svc = new TonalService(NullLogger<TonalService>.Instance);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    // ---- highlight recovery -----------------------------------------

    [Test]
    public void HighlightRecovery_ZeroStrength_IsNoOp() {
        var data = Ramp(24, 24);
        var copy = (ushort[])data.Clone();
        HighlightRecovery.Apply(data, 24, 24, 1, knee: 0.6, strength: 0.0);
        Assert.That(data, Is.EqualTo(copy));
    }

    [Test]
    public void HighlightRecovery_LowersHighlights_KeepsShadows() {
        int w = 16, h = 16;
        var data = new ushort[w * h];
        for (int i = 0; i < data.Length; i++) data[i] = 10000; // ~0.15, below knee
        data[0] = 60000; // ~0.92, above knee

        HighlightRecovery.Apply(data, w, h, 1, knee: 0.6, strength: 0.8);

        Assert.That(data[0], Is.LessThan((ushort)60000), "highlight must be pulled down");
        Assert.That(data[1], Is.EqualTo((ushort)10000), "shadow below the knee is untouched");
    }

    // ---- CLAHE -------------------------------------------------------

    [Test]
    public void Clahe_FlatField_StaysUniform_NoSeams() {
        // Equalization maps a single-valued tile to the top of its CDF, so a
        // perfectly flat field becomes uniformly bright (not identity) — but it
        // must stay UNIFORM: no tile-boundary seams, no NaN.
        int w = 32, h = 32;
        var data = new ushort[w * h];
        Array.Fill(data, (ushort)20000);
        Clahe.Apply(data, w, h, 1, clipLimit: 1.0, tiles: 4);
        ushort first = data[0];
        foreach (var v in data)
            Assert.That(v, Is.EqualTo(first), "flat field must remain seam-free / uniform");
    }

    [Test]
    public void Clahe_IncreasesLocalContrast() {
        // A low-contrast gradient block: CLAHE should widen the value spread.
        int w = 64, h = 64;
        var data = new ushort[w * h];
        var rng = new Random(3);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                data[y * w + x] = (ushort)(20000 + (x % 8) * 60 + rng.Next(0, 40));
        double stdBefore = Std(data);

        Clahe.Apply(data, w, h, 1, clipLimit: 4.0, tiles: 4);

        Assert.That(Std(data), Is.GreaterThan(stdBefore), "CLAHE should raise local contrast");
    }

    [Test]
    public void Service_WritesSiblings() {
        var p = WriteFits(Ramp(32, 32), 32, 32, "t.fits");
        Assert.That(_svc.Clahe(p, 2.0, 8).OutputPath.EndsWith("_clahe.fits"), Is.True);
        Assert.That(_svc.HighlightRecovery(p, 0.6, 0.5).OutputPath.EndsWith("_hlrec.fits"), Is.True);
    }

    // ---- helpers ----------------------------------------------------

    private static ushort[] Ramp(int w, int h) {
        var data = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                data[y * w + x] = (ushort)((x + y) * 200 + 1000);
        return data;
    }

    private static double Std(ushort[] d) {
        double mean = 0; foreach (var v in d) mean += v; mean /= d.Length;
        double s = 0; foreach (var v in d) s += (v - mean) * (v - mean);
        return Math.Sqrt(s / d.Length);
    }

    private string WriteFits(ushort[] data, int w, int h, string name) {
        var img = new BaseImageData(data, new ImageProperties { Width = w, Height = h, BitDepth = 16, Channels = 1 });
        var path = Path.Combine(_tmpDir, name);
        FITSWriter.Write(img, path);
        return path;
    }
}
