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
/// Pins morphological star reduction: a synthetic Gaussian star is dimmed
/// and its footprint pulled toward background, while the background away
/// from the star is preserved. Math layer + service.
/// </summary>
[TestFixture]
public class StarReductionServiceTests {

    private string _tmpDir = "";
    private StarReductionService _svc = null!;

    [SetUp]
    public void SetUp() {
        _tmpDir = Path.Combine(Path.GetTempPath(), "polaris-starred-" + Guid.NewGuid());
        Directory.CreateDirectory(_tmpDir);
        _svc = new StarReductionService(NullLogger<StarReductionService>.Instance);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    [Test]
    public void Apply_DimsStarPeak_AndPreservesBackground() {
        int w = 96, h = 96;
        var data = StarField(w, h, out int cx, out int cy);
        ushort peakBefore = data[cy * w + cx];
        ushort bgBefore = data[5 * w + 5]; // far corner

        int n = StarReduction.Apply(data, w, h, 1, amount: 0.9, size: 3, protectCore: false);

        Assert.That(n, Is.GreaterThanOrEqualTo(1), "the star must be detected");
        Assert.That(data[cy * w + cx], Is.LessThan(peakBefore), "star peak must be dimmed");
        Assert.That(data[5 * w + 5], Is.EqualTo(bgBefore).Within(1),
            "background away from the star must be preserved");
    }

    [Test]
    public void Apply_ReducesTotalFlux() {
        int w = 96, h = 96;
        var before = StarField(w, h, out _, out _);
        var after = (ushort[])before.Clone();
        StarReduction.Apply(after, w, h, 1, amount: 0.8, size: 3);

        long sumBefore = 0, sumAfter = 0;
        for (int i = 0; i < before.Length; i++) { sumBefore += before[i]; sumAfter += after[i]; }
        Assert.That(sumAfter, Is.LessThan(sumBefore), "reducing stars must lower total flux");
    }

    [Test]
    public void Apply_ZeroAmount_IsNoOp() {
        int w = 64, h = 64;
        var data = StarField(w, h, out _, out _);
        var copy = (ushort[])data.Clone();
        StarReduction.Apply(data, w, h, 1, amount: 0.0);
        Assert.That(data, Is.EqualTo(copy));
    }

    [Test]
    public void Service_WritesSiblingAndCounts() {
        int w = 96, h = 96;
        var data = StarField(w, h, out _, out _);
        var src = new BaseImageData(data, new ImageProperties { Width = w, Height = h, BitDepth = 16, Channels = 1 });
        var path = Path.Combine(_tmpDir, "stars.fits");
        FITSWriter.Write(src, path);

        var r = _svc.RunFits(path, amount: 0.7, size: 3);

        Assert.That(r.OutputPath.EndsWith("_starred.fits"), Is.True);
        Assert.That(r.StarsReduced, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void Service_MissingFile_Throws() {
        Assert.Throws<FileNotFoundException>(() =>
            _svc.RunFits(Path.Combine(_tmpDir, "nope.fits")));
    }

    // Flat background + one bright Gaussian star near the centre.
    private static ushort[] StarField(int w, int h, out int cx, out int cy) {
        cx = w / 2; cy = h / 2;
        const double bg = 800, peak = 55000, sigma = 2.2;
        var data = new ushort[w * h];
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                // Tiny deterministic ripple so stats are well-defined.
                double v = bg + ((x + y) % 3);
                double dx = x - cx, dy = y - cy;
                double g = peak * Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
                data[y * w + x] = (ushort)Math.Clamp(Math.Round(v + g), 0, 65535);
            }
        }
        return data;
    }
}
