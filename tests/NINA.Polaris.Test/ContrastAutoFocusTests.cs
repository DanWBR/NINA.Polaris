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

using System;
using NINA.Image.ImageAnalysis.AutoFocus;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Contrast-detection auto-focus: the metric must rise as a scene gets
/// sharper, read zero on a flat frame, and slot into the V-curve machinery
/// through a fit-space value that is a bowl with its minimum at focus.
/// </summary>
[TestFixture]
public class ContrastAutoFocusTests {

    /// <summary>A checkerboard of 16 px squares blurred by a box filter of the
    /// given radius: radius 0 is the sharp scene, larger is more defocused.</summary>
    private static ushort[] Scene(int w, int h, int blurRadius, int seed = 1, int square = 16) {
        var sharp = new double[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                sharp[y * w + x] = (((x / square) + (y / square)) % 2 == 0) ? 40000 : 8000;
        var img = sharp;
        if (blurRadius > 0) {
            img = new double[w * h];
            int n = 0;
            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    double sum = 0; n = 0;
                    for (int j = -blurRadius; j <= blurRadius; j++) {
                        int yy = Math.Clamp(y + j, 0, h - 1);
                        for (int i = -blurRadius; i <= blurRadius; i++) {
                            int xx = Math.Clamp(x + i, 0, w - 1);
                            sum += sharp[yy * w + xx]; n++;
                        }
                    }
                    img[y * w + x] = sum / n;
                }
            }
        }
        var rng = new Random(seed);
        var data = new ushort[w * h];
        for (int i = 0; i < data.Length; i++)
            data[i] = (ushort)Math.Clamp(img[i] + rng.NextDouble() * 200 - 100, 0, 65535);
        return data;
    }

    [TestCase(ContrastMethod.Laplace)]
    [TestCase(ContrastMethod.Sobel)]
    public void Contrast_FallsMonotonicallyAsTheSceneDefocuses(ContrastMethod method) {
        const int w = 256, h = 192;
        double prev = double.MaxValue;
        foreach (var blur in new[] { 0, 1, 2, 4, 8 }) {
            double c = ContrastMeasure.Measure(Scene(w, h, blur), w, h, method);
            Assert.That(c, Is.GreaterThan(0), $"blur {blur} reads zero");
            Assert.That(c, Is.LessThan(prev), $"blur {blur} did not read lower than the sharper scene ({c} vs {prev})");
            prev = c;
        }
    }

    [Test]
    public void Contrast_IsZeroOnAFlatFrame() {
        const int w = 128, h = 128;
        var flat = new ushort[w * h];
        Array.Fill(flat, (ushort)20000);
        Assert.That(ContrastMeasure.Measure(flat, w, h, ContrastMethod.Laplace), Is.EqualTo(0));
        Assert.That(ContrastMeasure.Measure(flat, w, h, ContrastMethod.Sobel), Is.EqualTo(0));
    }

    [Test]
    public void Contrast_DownsamplesLargeFramesToTheSameAnswerScale() {
        // The same scene rendered at 2048 px (64 px squares) and at 512 px
        // (16 px squares) must measure alike once the big one is reduced 4x:
        // the reduction must not move the metric off its scale, or a rig's
        // threshold would depend on sensor size.
        const int big = 2048, small = 512;
        var bigScene = Scene(big, big, 0, square: 64);
        var smallScene = Scene(small, small, 0, square: 16);
        double cb = ContrastMeasure.Measure(bigScene, big, big, ContrastMethod.Sobel, maxWidth: 512);
        double cs = ContrastMeasure.Measure(smallScene, small, small, ContrastMethod.Sobel, maxWidth: 512);
        Assert.That(cb, Is.GreaterThan(0));
        Assert.That(cs, Is.GreaterThan(0));
        Assert.That(cb / cs, Is.InRange(0.5, 2.0), $"big {cb} vs small {cs}");
    }

    [Test]
    public void FitSpace_IsABowl_AndRoundTrips() {
        double sharp = ContrastMeasure.ToFitSpace(5000), soft = ContrastMeasure.ToFitSpace(500);
        Assert.That(sharp, Is.LessThan(soft), "sharper (higher contrast) must read lower, like HFR");
        Assert.That(sharp, Is.GreaterThan(0));
        Assert.That(ContrastMeasure.FromFitSpace(ContrastMeasure.ToFitSpace(1234)), Is.EqualTo(1234).Within(1e-6));
        Assert.That(ContrastMeasure.ToFitSpace(0), Is.GreaterThan(0), "a zero contrast still maps to a positive, finite value");
    }

    [Test]
    public void Resolve_AContrastMetricAlwaysFitsAParabola() {
        var o = AutoFocusRunOptions.Resolve(new AutoFocusRequest { Metric = "CONTRAST_LAPLACE", Method = "TRENDHYPERBOLIC" }, null);
        Assert.That(o.Metric, Is.EqualTo(AutoFocusMetric.ContrastLaplace));
        Assert.That(o.Method, Is.EqualTo(AFCurveFittingMethod.Parabolic));

        var h = AutoFocusRunOptions.Resolve(new AutoFocusRequest { Method = "TRENDHYPERBOLIC" }, null);
        Assert.That(h.Metric, Is.EqualTo(AutoFocusMetric.StarHfr));
        Assert.That(h.Method, Is.EqualTo(AFCurveFittingMethod.TrendHyperbolic));

        var p = AutoFocusRunOptions.Resolve(null, new AutoFocusSettings { Metric = "CONTRAST_SOBEL" });
        Assert.That(p.Metric, Is.EqualTo(AutoFocusMetric.ContrastSobel), "the rig profile's metric applies without a request override");
    }

    [Test]
    public void WorseThanStart_ComparesInTheMetricsOwnTerms() {
        // HFR: 15% larger is worse.
        Assert.That(AutoFocusService.WorseThanStart(AutoFocusMetric.StarHfr, 2.0, 2.4, 1.15), Is.True);
        Assert.That(AutoFocusService.WorseThanStart(AutoFocusMetric.StarHfr, 2.0, 2.2, 1.15), Is.False);
        // Contrast in fit space: a contrast that dropped by more than the ratio is worse.
        double start = ContrastMeasure.ToFitSpace(3000);
        double worse = ContrastMeasure.ToFitSpace(3000 / 1.3);
        double fine = ContrastMeasure.ToFitSpace(3000 / 1.05);
        Assert.That(AutoFocusService.WorseThanStart(AutoFocusMetric.ContrastLaplace, start, worse, 1.15), Is.True);
        Assert.That(AutoFocusService.WorseThanStart(AutoFocusMetric.ContrastLaplace, start, fine, 1.15), Is.False);
        Assert.That(AutoFocusService.WorseThanStart(AutoFocusMetric.ContrastLaplace, start, worse, 0), Is.False, "0 disables the gate");
    }
}
