// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. See <https://www.gnu.org/licenses/>.

using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class DeconvolutionServiceTests {

    private static ushort[] BlurredField(int w, int h, double sharpSigma, double blurSigma,
                                         double amp, int seed = 31, double bg = 800) {
        // sharp signal (no noise) -> blur with a Gaussian PSF -> add read noise.
        var rng = new Random(seed);
        var sig = new double[w * h];
        for (int i = 0; i < sig.Length; i++) sig[i] = bg;
        int margin = 40, step = 50, r = (int)Math.Ceiling(5 * sharpSigma);
        for (int cy = margin; cy < h - margin; cy += step)
            for (int cx = margin; cx < w - margin; cx += step)
                for (int y = -r; y <= r; y++)
                    for (int x = -r; x <= r; x++) {
                        int ix = cx + x, iy = cy + y;
                        if (ix < 0 || iy < 0 || ix >= w || iy >= h) continue;
                        sig[iy * w + ix] += amp * Math.Exp(-(x * x + y * y) / (2 * sharpSigma * sharpSigma));
                    }
        // Gaussian blur (separable would be faster; small frame so direct is fine).
        int kr = (int)Math.Ceiling(4 * blurSigma);
        var ker = new double[2 * kr + 1]; double ks = 0;
        for (int i = -kr; i <= kr; i++) { ker[i + kr] = Math.Exp(-(i * i) / (2 * blurSigma * blurSigma)); ks += ker[i + kr]; }
        for (int i = 0; i < ker.Length; i++) ker[i] /= ks;
        var tmp = new double[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { double a = 0; for (int k = -kr; k <= kr; k++) { int sx = Math.Clamp(x + k, 0, w - 1); a += ker[k + kr] * sig[y * w + sx]; } tmp[y * w + x] = a; }
        var outd = new double[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { double a = 0; for (int k = -kr; k <= kr; k++) { int sy = Math.Clamp(y + k, 0, h - 1); a += ker[k + kr] * tmp[sy * w + x]; } outd[y * w + x] = a; }
        var u = new ushort[w * h];
        for (int i = 0; i < u.Length; i++) {
            double v = outd[i] + Gaussian(rng) * 8;
            u[i] = (ushort)Math.Clamp((int)Math.Round(v), 0, 65535);
        }
        return u;
    }

    private static double Gaussian(Random r) {
        double u1 = 1.0 - r.NextDouble(), u2 = 1.0 - r.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static double Fwhm(ushort[] img, int w, int x0, int y0, int r, double bg) {
        double sum = 0, mxx = 0, myy = 0, cx = 0, cy = 0;
        for (int y = -r; y <= r; y++) for (int x = -r; x <= r; x++) { double v = img[(y0 + y) * w + (x0 + x)] - bg; if (v < 0) v = 0; sum += v; cx += v * x; cy += v * y; }
        if (sum <= 0) return double.NaN; cx /= sum; cy /= sum;
        for (int y = -r; y <= r; y++) for (int x = -r; x <= r; x++) { double v = img[(y0 + y) * w + (x0 + x)] - bg; if (v < 0) v = 0; mxx += v * (x - cx) * (x - cx); myy += v * (y - cy) * (y - cy); }
        return 2.3548200450309493 * Math.Sqrt((mxx / sum + myy / sum) / 2.0);
    }

    [Test]
    public void RichardsonLucy_WritesSiblingAndSharpens() {
        const int W = 420, H = 420;
        var u = BlurredField(W, H, 1.0, 2.0, 40000);

        var dir = Path.Combine(Path.GetTempPath(), "polaris_rl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var srcPath = Path.Combine(dir, "blurred.fits");
        try {
            var props = new ImageProperties { Width = W, Height = H, BitDepth = 16, Channels = 1 };
            FITSWriter.Write(new BaseImageData(u, props), srcPath);

            var svc = new DeconvolutionService(NullLogger<DeconvolutionService>.Instance);
            var res = svc.RichardsonLucy(srcPath, strength: 0.7);

            Assert.That(File.Exists(res.OutputPath), Is.True, "should write _rl.fits sibling");
            Assert.That(res.OutputPath, Does.EndWith("_rl.fits"));
            Assert.That(res.StarsUsed, Is.GreaterThanOrEqualTo(8));
            Assert.That(res.FwhmPx, Is.InRange(3.0, 8.0));
            Assert.That(res.Iterations, Is.GreaterThan(0));

            ushort[] outU;
            using (var fs = File.OpenRead(res.OutputPath)) outU = FITSReader.Read(fs).Data;

            double before = Fwhm(u, W, 240, 240, 10, 800);
            double after = Fwhm(outU, W, 240, 240, 10, 800);
            TestContext.WriteLine($"FWHM {before:F2} → {after:F2} | PSF {res.FwhmPx:F2} stars {res.StarsUsed} iters {res.Iterations}");
            Assert.That(after, Is.LessThan(before * 0.9), "RL should sharpen the written frame");
        } finally {
            try { Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }

    [Test]
    public void RichardsonLucy_NoiseAdaptive_WritesAndSharpens() {
        // NM-2: the measured-σ support mask path must run end-to-end (build the
        // photon-transfer σ map, ramp the mask on local SNR) and still sharpen.
        const int W = 420, H = 420;
        var u = BlurredField(W, H, 1.0, 2.0, 40000);

        var dir = Path.Combine(Path.GetTempPath(), "polaris_rlna_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var srcPath = Path.Combine(dir, "blurred.fits");
        try {
            var props = new ImageProperties { Width = W, Height = H, BitDepth = 16, Channels = 1 };
            FITSWriter.Write(new BaseImageData(u, props), srcPath);

            var svc = new DeconvolutionService(NullLogger<DeconvolutionService>.Instance);
            var res = svc.RichardsonLucy(srcPath, strength: 0.7, noiseAdaptive: true);

            Assert.That(File.Exists(res.OutputPath), Is.True);
            Assert.That(res.StarsUsed, Is.GreaterThanOrEqualTo(8));

            ushort[] outU;
            using (var fs = File.OpenRead(res.OutputPath)) outU = FITSReader.Read(fs).Data;

            double before = Fwhm(u, W, 240, 240, 10, 800);
            double after = Fwhm(outU, W, 240, 240, 10, 800);
            TestContext.WriteLine($"noise-adaptive FWHM {before:F2} → {after:F2}");
            Assert.That(after, Is.LessThan(before * 0.9),
                "noise-adaptive RL should still sharpen over real signal");
        } finally {
            try { Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }
}
