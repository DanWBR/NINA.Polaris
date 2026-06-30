// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

using System;
using NINA.Image.ImageAnalysis;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class NoiseMapTests {

    // Flat field made of horizontal bands at increasing signal levels, each
    // with Gaussian noise sd = sqrt(a·S + b) -> the photon-transfer law with
    // known a, b. (Gaussian ≈ Poisson at these counts; what matters is the
    // variance-vs-signal slope/intercept the estimator must recover.)
    private static ushort[] PhotonTransferField(int w, int h, double a, double b, int seed = 17) {
        var rng = new Random(seed);
        var img = new ushort[w * h];
        int bands = 10;
        for (int y = 0; y < h; y++) {
            int band = y * bands / h;
            double signal = 400 + band * 1500;          // 400 .. ~14000 ADU
            double sd = Math.Sqrt(a * signal + b);
            for (int x = 0; x < w; x++) {
                double v = signal + Gaussian(rng) * sd;
                img[y * w + x] = (ushort)Math.Clamp((int)Math.Round(v), 0, 65535);
            }
        }
        return img;
    }

    private static double Gaussian(Random r) {
        double u1 = 1.0 - r.NextDouble(), u2 = 1.0 - r.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    [Test]
    public void RecoversPhotonTransferCoefficients() {
        const double a = 0.8, b = 100.0;   // slope (1/gain) + read-floor variance
        var img = PhotonTransferField(512, 512, a, b);
        var m = NoiseMap.EstimateModel(img, 512, 512);

        TestContext.WriteLine($"fitted a={m.A:F3} (true {a})  b={m.B:F1} (true {b})");
        Assert.That(m.A, Is.EqualTo(a).Within(0.20), "shot-noise slope");
        Assert.That(m.B, Is.EqualTo(b).Within(Math.Max(80, 0.6 * b)), "read-noise floor");

        // σ grows with signal: σ(10000) clearly larger than σ(500)
        Assert.That(m.Sigma(10000), Is.GreaterThan(m.Sigma(500) * 1.5));
    }

    [Test]
    public void SigmaMapTracksSignal() {
        var img = PhotonTransferField(256, 256, 0.8, 100.0);
        var sigma = NoiseMap.Estimate(img, 256, 256, out _);
        Assert.That(sigma.Length, Is.EqualTo(img.Length));
        // brighter pixels carry more noise
        int bright = 0, dim = 0; double sBright = 0, sDim = 0;
        for (int i = 0; i < img.Length; i++) {
            if (img[i] > 10000) { sBright += sigma[i]; bright++; }
            else if (img[i] < 1500 && img[i] > 100) { sDim += sigma[i]; dim++; }
        }
        Assert.That(bright, Is.GreaterThan(0)); Assert.That(dim, Is.GreaterThan(0));
        Assert.That(sBright / bright, Is.GreaterThan(sDim / dim));
    }

    [Test]
    public void FlatImageGivesFlatNoise() {
        // constant signal + uniform noise -> a≈0, b≈variance
        var rng = new Random(3);
        const int W = 256, H = 256; double sd = 12;
        var img = new ushort[W * H];
        for (int i = 0; i < img.Length; i++)
            img[i] = (ushort)Math.Clamp((int)Math.Round(2000 + Gaussian(rng) * sd), 0, 65535);
        var m = NoiseMap.EstimateModel(img, W, H);
        TestContext.WriteLine($"flat: a={m.A:F4} b={m.B:F1} (σ≈{Math.Sqrt(m.B):F1}, true {sd})");
        Assert.That(m.A, Is.LessThan(0.2), "no signal-dependent slope on a flat field");
        Assert.That(Math.Sqrt(m.B), Is.EqualTo(sd).Within(5));
    }
}
