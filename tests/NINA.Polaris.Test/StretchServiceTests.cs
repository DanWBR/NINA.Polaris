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
/// Pins the GHS / asinh stretch: math-layer curve properties
/// (<see cref="HyperbolicStretch"/>) + the service FITS round-trip
/// (<see cref="StretchService"/>) including the auto-D estimate.
/// </summary>
[TestFixture]
public class StretchServiceTests {

    private string _tmpDir = "";
    private StretchService _svc = null!;

    [SetUp]
    public void SetUp() {
        _tmpDir = Path.Combine(Path.GetTempPath(), "polaris-stretch-" + Guid.NewGuid());
        Directory.CreateDirectory(_tmpDir);
        _svc = new StretchService(NullLogger<StretchService>.Instance);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    // ---- math: curve properties -------------------------------------

    [TestCase("ghs")]
    [TestCase("asinh")]
    public void Curve_EndpointsFixed_AndMonotonic(string mode) {
        var type = HyperbolicStretch.ParseType(mode);
        var lut = HyperbolicStretch.BuildLut(1024, type, B: 0.0, D: 2.0, LP: 0.0, SP: 0.0, HP: 1.0, BP: 0.0);

        Assert.That(lut[0], Is.EqualTo(0.0).Within(1e-3), "f(0) must be 0");
        Assert.That(lut[^1], Is.EqualTo(1.0).Within(1e-3), "f(1) must be 1");
        for (int i = 1; i < lut.Length; i++)
            Assert.That(lut[i], Is.GreaterThanOrEqualTo(lut[i - 1] - 1e-6),
                $"curve must be monotonic non-decreasing (broke at {i})");
    }

    [TestCase("ghs")]
    [TestCase("asinh")]
    public void Curve_LiftsMidtones(string mode) {
        var type = HyperbolicStretch.ParseType(mode);
        // A shadow-region input (0.1) must be lifted upward by a positive stretch.
        var lut = HyperbolicStretch.BuildLut(1001, type, B: 0.0, D: 3.0, LP: 0.0, SP: 0.0, HP: 1.0, BP: 0.0);
        int idx = 100; // 0.1
        Assert.That(lut[idx], Is.GreaterThan(0.1),
            "a faint value must be brightened by the stretch");
    }

    [Test]
    public void Curve_ZeroD_IsIdentity() {
        var lut = HyperbolicStretch.BuildLut(256, HyperbolicStretch.StretchType.Ghs,
            B: 0.0, D: 0.0, LP: 0.0, SP: 0.0, HP: 1.0, BP: 0.0);
        for (int i = 0; i < 256; i++)
            Assert.That(lut[i], Is.EqualTo(i / 255.0).Within(1e-6));
    }

    // ---- service: FITS round-trip -----------------------------------

    [Test]
    public void RunFits_Ghs_BrightensAndWritesSibling() {
        // Uniform faint frame at ~0.1. GHS with D>0 must brighten it.
        var src = MakeMono(16, 16, (_, _) => 6554); // ~0.1
        var path = WriteFits(src, "faint.fits");

        var r = _svc.RunFits(path, "ghs", d: 3.0);

        Assert.That(r.OutputPath.EndsWith("_ghs.fits"), Is.True);
        Assert.That(File.Exists(r.OutputPath), Is.True);
        var got = ReadFits(r.OutputPath);
        Assert.That(got.Data[0], Is.GreaterThan((ushort)6554),
            "GHS must brighten a faint uniform frame");
    }

    [Test]
    public void RunFits_Asinh_WritesAsinhSibling() {
        var src = MakeMono(8, 8, (_, _) => 8000);
        var path = WriteFits(src, "a.fits");
        var r = _svc.RunFits(path, "asinh", d: 2.0);
        Assert.That(r.OutputPath.EndsWith("_asinh.fits"), Is.True);
    }

    [Test]
    public void RunFits_Auto_LiftsMedianTowardTarget() {
        // Uniform frame at 0.05 (median = 0.05). Auto toward target 0.25 must
        // pick a D that maps 0.05 -> ~0.25.
        ushort v = (ushort)Math.Round(0.05 * 65535);
        var src = MakeMono(32, 32, (_, _) => v);
        var path = WriteFits(src, "auto.fits");

        var r = _svc.RunFits(path, "ghs", auto: true, targetBackground: 0.25);

        Assert.That(r.AppliedD, Is.GreaterThan(0.0), "auto must choose a non-zero stretch");
        var got = ReadFits(r.OutputPath);
        double outFrac = got.Data[0] / 65535.0;
        Assert.That(outFrac, Is.EqualTo(0.25).Within(0.03),
            "auto stretch should land the median near the target background");
    }

    [Test]
    public void RunFits_Rgb_PreservesChannelBalanceOrdering() {
        // Linked stretch: applied identically to each channel, so ordering
        // R<G<B at a pixel is preserved after the stretch.
        var src = MakeRgb(8, 8, (_, _) => 5000, (_, _) => 10000, (_, _) => 15000);
        var path = WriteFits(src, "rgb.fits");

        var r = _svc.RunFits(path, "ghs", d: 2.5);

        var got = ReadFits(r.OutputPath);
        int plane = 8 * 8;
        Assert.That(got.Data[0], Is.LessThan(got.Data[plane]));
        Assert.That(got.Data[plane], Is.LessThan(got.Data[2 * plane]));
    }

    [Test]
    public void RunFits_MissingFile_Throws() {
        Assert.Throws<FileNotFoundException>(() =>
            _svc.RunFits(Path.Combine(_tmpDir, "nope.fits"), "ghs"));
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
