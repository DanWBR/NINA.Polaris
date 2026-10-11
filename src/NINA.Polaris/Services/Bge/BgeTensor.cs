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

using NINA.Polaris.Services.Rknn;

namespace NINA.Polaris.Services.Bge;

/// <summary>The per-channel robust statistics a background model is normalised
/// against. Kept beside the model output, because denormalising with a different
/// frame's numbers is what makes a reused background drift.</summary>
public readonly record struct BgeChannelStats(double Median, double Mad);

/// <summary>
/// Steps 1 to 3 of background extraction: downsample to the model window and
/// normalise it.
///
/// Lifted out of <see cref="RknnPipelines.RunBge"/> unchanged, so that the part
/// that needs an accelerator and the parts that are plain arithmetic can be
/// driven separately. That split is what lets the model run anywhere (host NPU,
/// host GPU, or the operator's browser) while the frame-sized work stays on the
/// host, and what lets one background be reused across several subs.
///
/// The model window is fixed, 256 for every GraXpert background model, so this
/// is the same cost for a guide camera and a 61 MP sensor.
/// </summary>
public static class BgeTensor {

    /// <summary>
    /// Build the NHWC <c>[1, tile, tile, 3]</c> input and the per-channel
    /// statistics it was normalised with.
    ///
    /// <paramref name="pixels"/> is plane-sequential (mono: one plane; RGB: R
    /// then G then B). A mono frame is replicated into all three tensor
    /// channels, which is what the model was trained to see.
    ///
    /// Each channel gets its own median and MAD because R, G and B in an OSC
    /// frame sit at wildly different background levels (the green weighting of
    /// the Bayer mosaic, plus the colour of the sky glow); normalising red by
    /// green's median would crush it or blow it out.
    /// </summary>
    public static (float[] Tensor, BgeChannelStats[] Stats) Build(
            ushort[] pixels, int width, int height, int channels, int tile) {
        int planeLen = width * height;
        var planesF = new float[channels][];
        var stats = new BgeChannelStats[channels];

        for (int c = 0; c < channels; c++) {
            var small = RknnImageMath.BilinearResizeU16(
                pixels.AsSpan(c * planeLen, planeLen), width, height, tile, tile);
            var pf = new float[tile * tile];
            for (int i = 0; i < pf.Length; i++) pf[i] = small[i] / 65535f;
            planesF[c] = pf;
            var (med, mad) = RknnImageMath.MedianMadSampled(pf);
            stats[c] = new BgeChannelStats(med, mad);
        }

        var tensor = new float[tile * tile * 3];
        for (int i = 0; i < tile * tile; i++) {
            for (int c = 0; c < 3; c++) {
                int srcC = channels == 3 ? c : 0;
                double v = ((planesF[srcC][i] - stats[srcC].Median) / stats[srcC].Mad) * 0.04;
                tensor[i * 3 + c] = (float)Math.Clamp(v, -1.0, 1.0);
            }
        }
        return (tensor, stats);
    }

    /// <summary>The statistics alone, without building a tensor. Used per frame
    /// when a cached background is reused: the shape of the background keeps,
    /// but the level it is denormalised against has to be the current frame's,
    /// or the correction drifts as the sky brightens.</summary>
    public static BgeChannelStats[] StatsOnly(
            ushort[] pixels, int width, int height, int channels, int tile) {
        int planeLen = width * height;
        var stats = new BgeChannelStats[channels];
        for (int c = 0; c < channels; c++) {
            var small = RknnImageMath.BilinearResizeU16(
                pixels.AsSpan(c * planeLen, planeLen), width, height, tile, tile);
            var pf = new float[tile * tile];
            for (int i = 0; i < pf.Length; i++) pf[i] = small[i] / 65535f;
            var (med, mad) = RknnImageMath.MedianMadSampled(pf);
            stats[c] = new BgeChannelStats(med, mad);
        }
        return stats;
    }
}
