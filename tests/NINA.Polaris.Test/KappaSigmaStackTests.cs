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

using NINA.Image.ImageAnalysis;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the incremental kappa-sigma rejection used by the live stack:
/// inliers fold in, an outlier past kappa*sigma is dropped, bright noisy
/// pixels aren't over-rejected, and the first few frames always seed.
/// </summary>
[TestFixture]
public class KappaSigmaStackTests {

    private const int MinFrames = 5;
    private const double Kappa = 3.0;

    // One-pixel arrays make the intent obvious.
    private static (float[] sum, int[] count, float[] m2) Pixel() =>
        (new float[1], new int[1], new float[1]);

    private static bool Feed((float[] sum, int[] count, float[] m2) p, double x) =>
        KappaSigmaStack.Accumulate(p.sum, p.count, p.m2, 0, x, MinFrames, Kappa);

    [Test]
    public void FirstMinFramesAlwaysAccepted() {
        var p = Pixel();
        // Even wildly varying early samples seed the statistics.
        double[] seed = { 100, 5000, 200, 8000, 300 };
        foreach (var v in seed) Assert.That(Feed(p, v), Is.True);
        Assert.That(p.count[0], Is.EqualTo(MinFrames));
    }

    [Test]
    public void HotPixelOutlierIsRejected() {
        var p = Pixel();
        // Tight inlier cluster around 1000.
        foreach (var v in new double[] { 1000, 1001, 999, 1000, 1002, 998, 1001 })
            Assert.That(Feed(p, v), Is.True);
        int before = p.count[0];
        float sumBefore = p.sum[0];

        // A cosmic-ray / hot pixel far above the cluster is rejected...
        Assert.That(Feed(p, 60000), Is.False, "outlier should be rejected");
        Assert.That(p.count[0], Is.EqualTo(before), "rejected sample must not raise count");
        Assert.That(p.sum[0], Is.EqualTo(sumBefore), "rejected sample must not add to sum");

        // ...and the next inlier still folds in.
        Assert.That(Feed(p, 1000), Is.True);
        Assert.That(p.count[0], Is.EqualTo(before + 1));

        double mean = p.sum[0] / p.count[0];
        Assert.That(mean, Is.EqualTo(1000).Within(3), "mean stays clean, unpolluted by 60000");
    }

    [Test]
    public void BrightNoisyPixelNotOverRejected() {
        var p = Pixel();
        var rng = new System.Random(42);
        // Star-core-like: bright with genuine photon-noise spread (~300 ADU).
        int accepted = 0;
        for (int f = 0; f < 40; f++) {
            double v = 30000 + (rng.NextDouble() - 0.5) * 600; // +/-300
            if (Feed(p, v)) accepted++;
        }
        // Legitimate noise must not be clipped away; nearly all fold in.
        Assert.That(accepted, Is.GreaterThanOrEqualTo(38));
    }

    [Test]
    public void IdenticalSamplesNeverThrowAndKeepAccepting() {
        var p = Pixel();
        for (int f = 0; f < 10; f++) Assert.That(Feed(p, 500), Is.True);
        // std == 0 (no spread) -> accept rather than reject everything.
        Assert.That(Feed(p, 500), Is.True);
        Assert.That(p.count[0], Is.EqualTo(11));
    }

    [Test]
    public void RejectionDisabledEquivalentWhenKappaHuge() {
        var p = Pixel();
        foreach (var v in new double[] { 1000, 1001, 999, 1000, 1002, 998 }) Feed(p, v);
        // A very large kappa accepts even a far sample (parity with rejection off).
        Assert.That(KappaSigmaStack.Accumulate(p.sum, p.count, p.m2, 0, 60000, MinFrames, 1e9),
            Is.True);
    }
}
