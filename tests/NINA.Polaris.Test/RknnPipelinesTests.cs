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

using NUnit.Framework;
using NINA.Polaris.Services.Rknn;

namespace NINA.Polaris.Test;

/// <summary>
/// Tests for the RKNN host tile pipelines (RKNN-3). These exercise the
/// tiling / normalization / blending machinery with a mock
/// <see cref="IRknnTileRunner"/> so no NPU is required. Two algebraic
/// invariants pin the whole tile-loop + padding + reassembly:
///
///  * Denoise with an IDENTITY model is a perfect round-trip: every
///    normalize→infer→denormalize→blend step cancels, so output == input.
///  * BGE with a ZERO model (background modelled as 0 everywhere) yields a
///    constant background == median, and Subtraction leaves the image
///    unchanged.
///
/// Plus direct checks of the median/MAD and resize helpers.
/// </summary>
[TestFixture]
public class RknnPipelinesTests {
    /// <summary>Echoes the input tile back unchanged.</summary>
    private sealed class IdentityRunner : IRknnTileRunner {
        public int TileSize => 256;
        public int Channels => 3;
        public float[] RunTile(float[] nhwcInput) => (float[])nhwcInput.Clone();
        public void Dispose() { }
    }

    /// <summary>Returns an all-zero output tile (models a flat background).</summary>
    private sealed class ZeroRunner : IRknnTileRunner {
        public int TileSize => 256;
        public int Channels => 3;
        public float[] RunTile(float[] nhwcInput) => new float[nhwcInput.Length];
        public void Dispose() { }
    }

    private static ushort[] Gradient(int w, int h, int lo, int hi) {
        var px = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                double t = (x + y) / (double)(w + h);
                px[y * w + x] = (ushort)(lo + t * (hi - lo));
            }
        return px;
    }

    [Test]
    public void Denoise_WithIdentityModel_IsRoundTrip() {
        // Dimensions deliberately not multiples of the 128 stride so the
        // edge-clamped padding + tile trimming is exercised.
        const int w = 300, h = 220;
        var plane = Gradient(w, h, 8000, 12000);

        using var runner = new IdentityRunner();
        var outp = RknnPipelines.RunDenoiseMono(runner, plane, w, h, strength: 1.0, clip: 10.0);

        Assert.That(outp.Length, Is.EqualTo(plane.Length));
        for (int i = 0; i < plane.Length; i++)
            Assert.That(outp[i], Is.EqualTo(plane[i]).Within(1),
                $"pixel {i} ({outp[i]} vs {plane[i]})");
    }

    [Test]
    public void Bge_WithZeroModel_SubtractionLeavesImageUnchanged() {
        const int w = 320, h = 240;
        var plane = Gradient(w, h, 5000, 20000);

        using var runner = new ZeroRunner();
        var outp = RknnPipelines.RunBge(runner, plane, w, h, channels: 1,
            correction: "Subtraction", saveBackground: false, out var bg);

        Assert.That(bg, Is.Null);
        Assert.That(outp.Length, Is.EqualTo(plane.Length));
        // bg modelled as constant == median, so v - median + median == v.
        for (int i = 0; i < plane.Length; i++)
            Assert.That(outp[i], Is.EqualTo(plane[i]).Within(2),
                $"pixel {i} ({outp[i]} vs {plane[i]})");
    }

    [Test]
    public void Bge_SaveBackground_ProducesBackgroundPlane() {
        const int w = 256, h = 256;
        var plane = Gradient(w, h, 4000, 9000);

        using var runner = new ZeroRunner();
        var outp = RknnPipelines.RunBge(runner, plane, w, h, channels: 1,
            correction: "Subtraction", saveBackground: true, out var bg);

        Assert.That(outp.Length, Is.EqualTo(plane.Length));
        Assert.That(bg, Is.Not.Null);
        Assert.That(bg!.Length, Is.EqualTo(plane.Length));
    }

    [Test]
    public void MedianMad_OfKnownArray_IsCorrect() {
        var data = new float[] { 0f, 0.25f, 0.5f, 0.75f, 1.0f };
        var (median, mad) = RknnImageMath.MedianMadSampled(data);
        Assert.That(median, Is.EqualTo(0.5).Within(1e-6));
        // |x-0.5| = {0.5,0.25,0,0.25,0.5}; median = 0.25.
        Assert.That(mad, Is.EqualTo(0.25).Within(1e-6));
    }

    [Test]
    public void MedianMadU16_IsComputedInNormalizedSpace() {
        var data = new ushort[] { 0, 16383, 32767, 49151, 65535 };
        var (median, mad) = RknnImageMath.MedianMadSampledU16(data);
        Assert.That(median, Is.EqualTo(32767.0 / 65535.0).Within(1e-4));
        Assert.That(mad, Is.GreaterThan(0));
    }

    [Test]
    public void BilinearResize_OfConstantPlane_StaysConstant() {
        var src = new ushort[64 * 64];
        Array.Fill(src, (ushort)12345);
        var dst = RknnImageMath.BilinearResizeU16(src, 64, 64, 256, 256);
        Assert.That(dst.Length, Is.EqualTo(256 * 256));
        foreach (var v in dst) Assert.That(v, Is.EqualTo(12345).Within(1));
    }

    [Test]
    public void BoxBlur_OfConstantPlane_StaysConstant() {
        var src = new float[32 * 32];
        Array.Fill(src, 0.42f);
        var blurred = RknnImageMath.BoxBlurF(src, 32, 32, passes: 3);
        foreach (var v in blurred) Assert.That(v, Is.EqualTo(0.42f).Within(1e-5));
    }
}
