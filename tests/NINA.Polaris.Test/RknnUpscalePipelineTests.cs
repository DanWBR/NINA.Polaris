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
/// Tests for the super-resolution tile pipeline (RknnPipelines.RunUpscale). A mock
/// <see cref="IRknnUpscaleTileRunner"/> stands in for the NPU. Core invariant: with
/// a model that just NEAREST-NEIGHBOUR upsamples its (normalized) input, the whole
/// pipeline's per-channel MAD-normalize → model → denormalize → stitch reduces to a
/// plain 2× nearest upsample of the input — <c>out[oy,ox] == in[oy/2, ox/2]</c>.
/// That pins the tiling, normalization round-trip, and the scale×-larger stitch.
/// </summary>
[TestFixture]
public class RknnUpscalePipelineTests {
    /// <summary>A "model" that nearest-neighbour upsamples its NHWC input by Scale.
    /// normalize→this→denormalize cancels, so the pipeline output is the 2× NN
    /// upsample of the source.</summary>
    private sealed class NearestUpscaleRunner : IRknnUpscaleTileRunner {
        public int TileSize => 128;
        public int Scale => 2;
        public float[] RunTile(float[] nhwc) {
            int t = TileSize, s = Scale, ht = t * s;
            var o = new float[ht * ht * 3];
            for (int y = 0; y < ht; y++)
                for (int x = 0; x < ht; x++) {
                    int src = ((y / s) * t + (x / s)) * 3;
                    int dst = (y * ht + x) * 3;
                    o[dst] = nhwc[src];
                    o[dst + 1] = nhwc[src + 1];
                    o[dst + 2] = nhwc[src + 2];
                }
            return o;
        }
        public void Dispose() { }
    }

    private static ushort[] Gradient(int w, int h) {
        var px = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = (ushort)(2000 + (x * 7 + y * 3) % 20000);
        return px;
    }

    [Test]
    public void RunUpscale_NearestModel_Doubles_Mono() {
        int w = 200, h = 150;
        var src = Gradient(w, h);
        using var r = new NearestUpscaleRunner();

        var (outPx, ow, oh) = RknnPipelines.RunUpscale(r, src, w, h, channels: 1);

        Assert.That(ow, Is.EqualTo(w * 2));
        Assert.That(oh, Is.EqualTo(h * 2));
        Assert.That(outPx.Length, Is.EqualTo(ow * oh));
        for (int oy = 0; oy < oh; oy += 11)
            for (int ox = 0; ox < ow; ox += 13) {
                ushort expected = src[(oy / 2) * w + (ox / 2)];
                Assert.That(outPx[oy * ow + ox], Is.EqualTo(expected).Within(2),
                    $"out[{oy},{ox}] should be the NN-upsample of in[{oy / 2},{ox / 2}]");
            }
    }

    [Test]
    public void RunUpscale_NearestModel_Doubles_Rgb() {
        int w = 260, h = 210;   // > one stride → multi-tile per axis
        var plane = Gradient(w, h);
        var src = new ushort[w * h * 3];
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < plane.Length; i++)
                src[c * plane.Length + i] = (ushort)Math.Min(65535, plane[i] + c * 900);
        using var r = new NearestUpscaleRunner();

        var (outPx, ow, oh) = RknnPipelines.RunUpscale(r, src, w, h, channels: 3);

        Assert.That((ow, oh), Is.EqualTo((w * 2, h * 2)));
        int oplane = ow * oh;
        for (int c = 0; c < 3; c++)
            for (int oy = 0; oy < oh; oy += 17)
                for (int ox = 0; ox < ow; ox += 19) {
                    ushort expected = src[c * plane.Length + (oy / 2) * w + (ox / 2)];
                    Assert.That(outPx[c * oplane + oy * ow + ox], Is.EqualTo(expected).Within(2));
                }
    }
}
