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
/// Pins the à-trous wavelet transform + wavelet sharpen + multiscale HDR:
/// exact reconstruction, sharpening raises local contrast, HDR pulls a bright
/// core down relative to the background. Math layer + service round-trip.
/// </summary>
[TestFixture]
public class WaveletServiceTests {

    private string _tmpDir = "";
    private WaveletService _svc = null!;

    [SetUp]
    public void SetUp() {
        _tmpDir = Path.Combine(Path.GetTempPath(), "polaris-wave-" + Guid.NewGuid());
        Directory.CreateDirectory(_tmpDir);
        _svc = new WaveletService(NullLogger<WaveletService>.Instance);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    // ---- math: à-trous ----------------------------------------------

    [Test]
    public void Atrous_ReconstructsExactly() {
        int w = 48, h = 40;
        var plane = new float[w * h];
        var rng = new Random(1);
        for (int i = 0; i < plane.Length; i++) plane[i] = (float)rng.NextDouble();

        var dec = AtrousWavelet.Decompose(plane, w, h, 5);
        var rec = AtrousWavelet.Reconstruct(dec);

        for (int i = 0; i < plane.Length; i++)
            Assert.That(rec[i], Is.EqualTo(plane[i]).Within(1e-4),
                $"residual + Σdetail must equal the original at {i}");
    }

    [Test]
    public void Atrous_FlatPlane_HasZeroDetail() {
        int w = 32, h = 32;
        var plane = new float[w * h];
        Array.Fill(plane, 0.3f);
        var dec = AtrousWavelet.Decompose(plane, w, h, 4);
        foreach (var d in dec.Detail)
            foreach (var v in d)
                Assert.That(v, Is.EqualTo(0f).Within(1e-5), "flat plane has no detail");
    }

    // ---- wavelet sharpen --------------------------------------------

    [Test]
    public void Sharpen_ZeroParams_IsNoOp() {
        int w = 32, h = 32;
        var data = Ramp(w, h);
        var copy = (ushort[])data.Clone();
        WaveletSharpen.Apply(data, w, h, 1, detail: 0.0, denoise: 0.0);
        Assert.That(data, Is.EqualTo(copy));
    }

    [Test]
    public void Sharpen_RaisesLocalContrastOnAnEdge() {
        // A step edge: sharpening must increase the gradient across it.
        int w = 64, h = 8;
        var data = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                data[y * w + x] = (ushort)(x < w / 2 ? 12000 : 24000);
        int lo = 3 * w + (w / 2 - 1), hi = 3 * w + (w / 2);
        int gradBefore = data[hi] - data[lo];

        WaveletSharpen.Apply(data, w, h, 1, detail: 0.8, denoise: 0.0);

        int gradAfter = data[hi] - data[lo];
        Assert.That(gradAfter, Is.GreaterThan(gradBefore),
            "sharpen must steepen the edge (overshoot)");
    }

    // ---- multiscale HDR ---------------------------------------------

    [Test]
    public void Hdr_PullsBrightCoreDownRelativeToBackground() {
        int w = 128, h = 128;
        var data = CoreField(w, h, out int cx, out int cy);
        double coreBefore = data[cy * w + cx];
        double bgBefore = data[5 * w + 5];
        double ratioBefore = coreBefore / bgBefore;

        var r = _svc.Hdr(WriteFits(data, w, h, "core.fits"), amount: 0.8, scales: 6);
        var got = ReadFits(r.OutputPath);
        double ratioAfter = (double)got.Data[cy * w + cx] / got.Data[5 * w + 5];

        Assert.That(r.OutputPath.EndsWith("_wshdr.fits"), Is.True);
        Assert.That(ratioAfter, Is.LessThan(ratioBefore),
            "HDR must reduce the core/background ratio");
    }

    [Test]
    public void Service_Sharpen_WritesSibling() {
        var r = _svc.Sharpen(WriteFits(Ramp(32, 32), 32, 32, "r.fits"), 0.5, 0.0, 5);
        Assert.That(r.OutputPath.EndsWith("_wsharp.fits"), Is.True);
        Assert.That(File.Exists(r.OutputPath), Is.True);
    }

    [Test]
    public void Service_MissingFile_Throws() {
        Assert.Throws<FileNotFoundException>(() =>
            _svc.Sharpen(Path.Combine(_tmpDir, "nope.fits"), 0.5, 0.0, 5));
    }

    // ---- helpers ----------------------------------------------------

    private static ushort[] Ramp(int w, int h) {
        var data = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                data[y * w + x] = (ushort)((x + y) * 200 + 1000);
        return data;
    }

    private static ushort[] CoreField(int w, int h, out int cx, out int cy) {
        cx = w / 2; cy = h / 2;
        const double bg = 3000, peak = 45000, sigma = 8.0;
        var data = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                double dx = x - cx, dy = y - cy;
                double g = peak * Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
                data[y * w + x] = (ushort)Math.Clamp(Math.Round(bg + g), 0, 65535);
            }
        return data;
    }

    private string WriteFits(ushort[] data, int w, int h, string name) {
        var img = new BaseImageData(data, new ImageProperties { Width = w, Height = h, BitDepth = 16, Channels = 1 });
        var path = Path.Combine(_tmpDir, name);
        FITSWriter.Write(img, path);
        return path;
    }

    private static BaseImageData ReadFits(string path) {
        using var fs = File.OpenRead(path);
        return FITSReader.Read(fs);
    }
}
