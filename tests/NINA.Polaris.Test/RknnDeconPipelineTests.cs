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
using NUnit.Framework;
using NINA.Polaris.Services.Rknn;

namespace NINA.Polaris.Test;

/// <summary>
/// Tests for the deconvolution tile pipeline (RknnPipelines.RunDecon), the
/// two-input (image [1,1,512,512] + params [sigmaNorm, effStrength]) counterpart
/// to the single-tensor BGE/Denoise pipelines. A mock IRknnDeconTileRunner stands
/// in for the NPU so the log-mean-std normalize → residual-subtract → inverse-log
/// tiling math is verified with no hardware.
///
/// Core invariant: a ZERO residual is a perfect round-trip — normOut = normIn, and
/// the inverse-log exactly cancels the forward log-mean-std normalize, so the
/// output image == the input (within u16 rounding). This pins the padding, tiling,
/// per-tile stats, inner-crop reassembly, and denormalize end to end.
/// </summary>
[TestFixture]
public class RknnDeconPipelineTests {
    /// <summary>Returns an all-zero residual: the model "does nothing", so the
    /// pipeline must reconstruct the input.</summary>
    private sealed class ZeroResidualRunner : IRknnDeconTileRunner {
        public int TileSize => 512;
        public float[]? LastParams;
        public int LastTensorLength;
        public float[] RunTile(float[] chwInput, float[] pars) {
            LastParams = (float[])pars.Clone();
            LastTensorLength = chwInput.Length;
            return new float[chwInput.Length];   // zero residual
        }
        public void Dispose() { }
    }

    private static ushort[] Gradient(int w, int h) {
        var px = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = (ushort)(1000 + (x + y) % 5000);
        return px;
    }

    [Test]
    public void RunDecon_ZeroResidual_IsRoundTrip_Mono() {
        int w = 300, h = 220;   // smaller than one 512 tile → exercises padding
        var src = Gradient(w, h);
        using var runner = new ZeroResidualRunner();

        var outPx = RknnPipelines.RunDecon(runner, src, w, h, channels: 1,
            target: "stars", version: "1.0.0", psfPixels: 4.0, strength: 0.5);

        Assert.That(outPx.Length, Is.EqualTo(src.Length));
        for (int i = 0; i < src.Length; i++)
            Assert.That(outPx[i], Is.EqualTo(src[i]).Within(2),
                $"pixel {i}: {outPx[i]} vs {src[i]}");
    }

    [Test]
    public void RunDecon_ZeroResidual_IsRoundTrip_Rgb_MultiTile() {
        // 600x520 forces a 2x2 tile grid per plane (stride 448) and 3 planes.
        int w = 600, h = 520;
        var src = new ushort[w * h * 3];
        var plane = Gradient(w, h);
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < plane.Length; i++)
                src[c * plane.Length + i] = (ushort)Math.Min(65535, plane[i] + c * 700);
        using var runner = new ZeroResidualRunner();

        var outPx = RknnPipelines.RunDecon(runner, src, w, h, channels: 3,
            target: "objects", version: "1.0.1", psfPixels: 3.0, strength: 0.8);

        for (int i = 0; i < src.Length; i++)
            Assert.That(outPx[i], Is.EqualTo(src[i]).Within(2));
    }

    [Test]
    public void RunDecon_FeedsParamsAndFullTile() {
        using var runner = new ZeroResidualRunner();
        RknnPipelines.RunDecon(runner, Gradient(128, 128), 128, 128, channels: 1,
            target: "stars", version: "1.0.0", psfPixels: 4.0, strength: 0.6);

        Assert.That(runner.LastTensorLength, Is.EqualTo(512 * 512));
        Assert.That(runner.LastParams, Is.Not.Null);
        Assert.That(runner.LastParams!.Length, Is.EqualTo(2));
        // effStrength = strength * 0.95
        Assert.That(runner.LastParams[1], Is.EqualTo(0.6f * 0.95f).Within(1e-6));
        // sigmaNorm for stars, psf 4.0: (4/2.355/... - 1.5)/3 = ((1.6985)-1.5)/3
        Assert.That(runner.LastParams[0],
            Is.EqualTo(RknnPipelines.DeconSigmaNormalized("stars", "1.0.0", 4.0)).Within(1e-6));
    }

    [Test]
    public void DeconSigmaNormalized_MatchesGraXpertFormulas() {
        // FWHM → σ = psf/2.355.
        // Stars v1.0.0: (σ - 1.5) / 3
        Assert.That(RknnPipelines.DeconSigmaNormalized("stars", "1.0.0", 6.0),
            Is.EqualTo(Math.Clamp((6.0 / 2.355 - 1.5) / 3.0, 0.05, 0.95)).Within(1e-6));
        // Objects v1.0.1: (σ - 0.5) / 5.5
        Assert.That(RknnPipelines.DeconSigmaNormalized("objects", "1.0.1", 6.0),
            Is.EqualTo(Math.Clamp((6.0 / 2.355 - 0.5) / 5.5, 0.05, 0.95)).Within(1e-6));
        // Objects v1.0.0: (σ - 1.0) / 5
        Assert.That(RknnPipelines.DeconSigmaNormalized("objects", "1.0.0", 6.0),
            Is.EqualTo(Math.Clamp((6.0 / 2.355 - 1.0) / 5.0, 0.05, 0.95)).Within(1e-6));
        // Clamp: tiny PSF floors at 0.05, huge PSF ceils at 0.95.
        Assert.That(RknnPipelines.DeconSigmaNormalized("stars", "1.0.0", 0.05), Is.EqualTo(0.05f).Within(1e-6));
        Assert.That(RknnPipelines.DeconSigmaNormalized("stars", "1.0.0", 15.0), Is.EqualTo(0.95f).Within(1e-6));
    }
}
