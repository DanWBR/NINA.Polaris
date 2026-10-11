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

/// <summary>
/// Steps 5 to 8 of background extraction: denormalise the model output, smooth
/// it, scale it back to the frame and correct every pixel.
///
/// This is the only part that touches the whole frame, and it is plain
/// arithmetic: a blur over 256x256, one bilinear upsample and one pass over the
/// pixels. It is the same class of work as the cosmetic correction already
/// applied to every sub, which is what makes a per-frame correction affordable
/// even when the model itself runs once every N frames.
/// </summary>
public static class BgeApply {

    /// <summary>
    /// <paramref name="modelOutput"/> is the raw NHWC model output, length
    /// <c>tile*tile*3</c>. <paramref name="stats"/> must be the statistics the
    /// correction should be centred on: the frame's own, which for a reused
    /// background is not the frame the model ran on.
    ///
    /// Correction is applied per channel, each recentred on its OWN median, to
    /// match the browser pipeline. A single global mean (what the GraXpert CLI's
    /// Subtraction does) neutralises the background to grey but shifts each
    /// channel's level, which visibly changes saturation.
    /// </summary>
    public static ushort[] Correct(ushort[] pixels, int width, int height, int channels,
                                   float[] modelOutput, BgeChannelStats[] stats, int tile,
                                   string correction, bool saveBackground,
                                   out ushort[]? background) {
        int planeLen = width * height;
        bool division = string.Equals(correction, "Division", StringComparison.OrdinalIgnoreCase);

        // 5) Denormalise each output channel with its own median and MAD.
        // 6+7) Box-blur, then upscale back to the source size.
        var bgFull = new float[channels][];
        for (int c = 0; c < channels; c++) {
            var bgSmall = new float[tile * tile];
            for (int i = 0; i < tile * tile; i++)
                bgSmall[i] = (float)(modelOutput[i * 3 + c] * stats[c].Mad / 0.04 + stats[c].Median);
            var smoothed = RknnImageMath.BoxBlurF(bgSmall, tile, tile, 3);
            bgFull[c] = RknnImageMath.BilinearResizeF(smoothed, tile, tile, width, height);
        }

        // 8) Apply the correction per channel.
        var result = new ushort[pixels.Length];
        background = saveBackground ? new ushort[pixels.Length] : null;
        for (int c = 0; c < channels; c++) {
            var bg = bgFull[c];
            double median = stats[c].Median;
            int off = c * planeLen;
            for (int i = 0; i < planeLen; i++) {
                double v = pixels[off + i] / 65535.0;
                double bgv = bg[i];
                double corrected = division
                    ? v / Math.Max(1e-6, bgv) * median
                    : v - bgv + median;
                result[off + i] = (ushort)Math.Clamp(Math.Round(corrected * 65535.0), 0, 65535);
                if (background != null) {
                    double b = division ? Math.Max(1e-6, bgv) : bgv;
                    background[off + i] = (ushort)Math.Clamp(Math.Round(b * 65535.0), 0, 65535);
                }
            }
        }
        return result;
    }
}
