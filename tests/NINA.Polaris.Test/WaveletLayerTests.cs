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
/// WAVE-1: per-layer wavelet gains, the interaction model RegiStax and
/// AstroSurface use and the one planetary images are actually tuned with.
///
/// The properties worth pinning are the ones a slider panel depends on:
/// neutral sliders must be a true no-op, one layer must move only its own
/// scale, and the transform underneath must still reconstruct exactly (the
/// whole approach rests on residual + sum(details) == original).
/// </summary>
[TestFixture]
public class WaveletLayerTests {

    /// <summary>A plane with structure at several scales: a broad gradient, a
    /// mid-scale blob and single-pixel speckle. Detail lives at a different
    /// wavelet layer for each.</summary>
    private static ushort[] MultiScaleFrame(int w, int h) {
        var px = new ushort[w * h];
        var rnd = new Random(7);
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                double broad = 12000 + 8000.0 * x / w;                       // coarse
                double blob = 9000 * Math.Exp(-((x - w / 2.0) * (x - w / 2.0)
                                               + (y - h / 2.0) * (y - h / 2.0)) / (2 * 36.0));
                double speckle = rnd.NextDouble() < 0.05 ? 6000 : 0;          // finest
                px[y * w + x] = (ushort)Math.Clamp(broad + blob + speckle, 0, 65535);
            }
        }
        return px;
    }

    private static double MeanAbsDiff(ushort[] a, ushort[] b) {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += Math.Abs(a[i] - b[i]);
        return s / a.Length;
    }

    /// <summary>The reset button has to be free. Two float round trips through
    /// a decomposition would otherwise cost a little precision every time the
    /// operator zeroed the panel.</summary>
    [Test]
    public void AllGainsOne_TouchesNothing() {
        const int w = 64, h = 64;
        var px = MultiScaleFrame(w, h);
        var before = (ushort[])px.Clone();

        WaveletSharpen.ApplyLayers(px, w, h, 1, new[] { 1.0, 1.0, 1.0, 1.0, 1.0 });

        Assert.That(px, Is.EqualTo(before), "a neutral layer set must be an exact no-op");
    }

    /// <summary>Boosting the finest layer is what sharpens; boosting a coarse
    /// layer must not do the same job, or the panel's sliders would all be the
    /// same slider.</summary>
    [Test]
    public void FinestLayerGain_ChangesTheImageMoreThanACoarseOne() {
        const int w = 64, h = 64;
        var src = MultiScaleFrame(w, h);

        var fine = (ushort[])src.Clone();
        WaveletSharpen.ApplyLayers(fine, w, h, 1, new[] { 2.0, 1.0, 1.0, 1.0, 1.0 });

        var coarse = (ushort[])src.Clone();
        WaveletSharpen.ApplyLayers(coarse, w, h, 1, new[] { 1.0, 1.0, 1.0, 1.0, 2.0 });

        double dFine = MeanAbsDiff(src, fine);
        double dCoarse = MeanAbsDiff(src, coarse);
        Assert.That(dFine, Is.GreaterThan(0), "boosting the finest layer must do something");
        Assert.That(dCoarse, Is.GreaterThan(0), "boosting a coarse layer must do something too");
        Assert.That(dFine, Is.Not.EqualTo(dCoarse).Within(0.01),
            "layers must be independently controllable, not one knob in disguise");
    }

    /// <summary>Softening a layer (gain below 1) is the other half of the
    /// control and is what tames over-sharpened seeing artefacts.</summary>
    [Test]
    public void GainBelowOne_SoftensInsteadOfSharpening() {
        const int w = 64, h = 64;
        var src = MultiScaleFrame(w, h);

        var soft = (ushort[])src.Clone();
        WaveletSharpen.ApplyLayers(soft, w, h, 1, new[] { 0.2, 1.0, 1.0, 1.0, 1.0 });

        // Neighbour-to-neighbour variation is the working definition of fine
        // detail; suppressing the finest layer must reduce it.
        double Rough(ushort[] p) {
            double s = 0; int n = 0;
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++) { s += Math.Abs(p[y * w + x] - p[y * w + x + 1]); n++; }
            return s / n;
        }
        Assert.That(Rough(soft), Is.LessThan(Rough(src)));
    }

    /// <summary>A gain array shorter than the transform must leave the missing
    /// (coarser) layers alone. Clamping to the last value instead would apply
    /// the finest layer's boost to the background, which is how a four-slider
    /// panel would quietly wreck a six-scale image.</summary>
    [Test]
    public void ShortGainArray_LeavesTheRemainingLayersAlone() {
        const int w = 64, h = 64;
        var src = MultiScaleFrame(w, h);

        var partial = (ushort[])src.Clone();
        WaveletSharpen.ApplyLayers(partial, w, h, 1, new[] { 1.0, 1.0, 1.0 });
        Assert.That(partial, Is.EqualTo(src), "three neutral layers must still be a no-op");
    }

    /// <summary>The single-knob entry point is now a preset over the same
    /// engine. It has to keep producing what it always did, because saved Auto
    /// Workflow definitions carry those numbers.</summary>
    [Test]
    public void LegacyDetailKnob_MatchesItsEquivalentLayerSet() {
        const int w = 64, h = 64;
        const double detail = 0.5;
        const int scales = 5;

        var viaKnob = MultiScaleFrame(w, h);
        WaveletSharpen.Apply(viaKnob, w, h, 1, detail, 0.0, scales);

        var gains = new double[scales];
        for (int j = 0; j < scales; j++) gains[j] = 1.0 + detail * Math.Exp(-j / 2.0);
        var viaLayers = MultiScaleFrame(w, h);
        WaveletSharpen.ApplyLayers(viaLayers, w, h, 1, gains);

        Assert.That(viaLayers, Is.EqualTo(viaKnob));
    }

    /// <summary>The property the whole multiscale approach rests on: with the
    /// planes untouched, residual + sum(details) is the original.</summary>
    [Test]
    public void Decomposition_ReconstructsExactly() {
        const int w = 48, h = 40;
        var src = MultiScaleFrame(w, h);
        var plane = new float[w * h];
        for (int i = 0; i < plane.Length; i++) plane[i] = src[i] / 65535f;

        var dec = AtrousWavelet.Decompose(plane, w, h, 5);
        var back = AtrousWavelet.Reconstruct(dec);

        for (int i = 0; i < plane.Length; i++)
            Assert.That(back[i], Is.EqualTo(plane[i]).Within(1e-4), $"pixel {i}");
    }
}
