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

namespace NINA.Polaris.Services.Rknn;

/// <summary>
/// Pure tile-pipeline math for the RKNN host path, ported tile-for-tile from
/// the browser <c>onnx-pipelines.js</c> so the NPU output matches the in-browser
/// ONNX output. Everything here is deterministic and takes an
/// <see cref="IRknnTileRunner"/>, so the normalization / tiling / blending can
/// be unit-tested with a mock runner (no NPU required).
///
/// Only BGE (single forward pass) and Denoise (256/128/64 tiling) are
/// implemented; deconvolution uses a different tile size (512), layout (NCHW)
/// and multi-input contract, so it stays on the GraXpert CLI path for now.
/// </summary>
internal static class RknnPipelines {
    /// <summary>
    /// Background extraction. Mirrors <c>BgePipeline</c>: downsample each plane
    /// to the model window, per-channel MAD-normalize (* 0.04, clip +/-1), one
    /// forward pass, denormalize, box-blur, upscale, then subtract/divide.
    /// <paramref name="pixels"/> is plane-sequential (mono: one plane; RGB:
    /// R then G then B). Returns the corrected image and, when requested, the
    /// modelled background in source brightness space.
    /// </summary>
    public static ushort[] RunBge(IRknnTileRunner runner, ushort[] pixels, int width, int height,
                                  int channels, string correction, bool saveBackground,
                                  out ushort[]? background) {
        int tile = runner.TileSize;
        int planeLen = width * height;
        bool division = string.Equals(correction, "Division", StringComparison.OrdinalIgnoreCase);

        // 1) Downsample each plane to tile x tile + per-channel stats.
        var planesF = new float[channels][];
        var med = new double[channels];
        var mad = new double[channels];
        for (int c = 0; c < channels; c++) {
            var small = RknnImageMath.BilinearResizeU16(
                pixels.AsSpan(c * planeLen, planeLen), width, height, tile, tile);
            var pf = new float[tile * tile];
            for (int i = 0; i < pf.Length; i++) pf[i] = small[i] / 65535f;
            planesF[c] = pf;
            (med[c], mad[c]) = RknnImageMath.MedianMadSampled(pf);
        }

        // 2-3) Build [1, tile, tile, 3] NHWC. Mono replicates plane 0.
        var tensor = new float[tile * tile * 3];
        for (int i = 0; i < tile * tile; i++) {
            for (int c = 0; c < 3; c++) {
                int srcC = channels == 3 ? c : 0;
                double v = ((planesF[srcC][i] - med[srcC]) / mad[srcC]) * 0.04;
                tensor[i * 3 + c] = (float)Math.Clamp(v, -1.0, 1.0);
            }
        }

        // 4) Inference.
        var outData = runner.RunTile(tensor);

        // 5) Denormalize each output channel with its own median+MAD.
        // 6+7) Box-blur then upscale back to source size.
        var bgFull = new float[channels][];
        for (int c = 0; c < channels; c++) {
            var bgSmall = new float[tile * tile];
            for (int i = 0; i < tile * tile; i++)
                bgSmall[i] = (float)(outData[i * 3 + c] * mad[c] / 0.04 + med[c]);
            var smoothed = RknnImageMath.BoxBlurF(bgSmall, tile, tile, 3);
            bgFull[c] = RknnImageMath.BilinearResizeF(smoothed, tile, tile, width, height);
        }

        // 8) Apply correction per channel (+ optional background plane).
        var result = new ushort[pixels.Length];
        background = saveBackground ? new ushort[pixels.Length] : null;
        for (int c = 0; c < channels; c++) {
            var bg = bgFull[c];
            double median = med[c];
            int off = c * planeLen;
            for (int i = 0; i < planeLen; i++) {
                double v = pixels[off + i] / 65535.0;
                double corrected;
                double bgv = bg[i];
                if (division) {
                    double b = Math.Max(1e-6, bgv);
                    corrected = v / b * median;
                } else {
                    corrected = v - bgv + median;
                }
                result[off + i] = (ushort)Math.Clamp(Math.Round(corrected * 65535.0), 0, 65535);
                if (background != null) {
                    double b = division ? Math.Max(1e-6, bgv) : bgv;
                    background[off + i] = (ushort)Math.Clamp(Math.Round(b * 65535.0), 0, 65535);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Denoise a single mono plane. Mirrors <c>DenoisePipeline._runMono</c>:
    /// 256/128/64 tiling over a virtual edge-clamped padded plane, global
    /// median/MAD normalize (clip +/-<paramref name="clip"/>), per-tile
    /// inference, then denormalize + blend-mask + strength blend back into the
    /// trimmed output. <paramref name="clip"/> is 1.0 for v3 models, 10.0 for v2.
    /// </summary>
    public static ushort[] RunDenoiseMono(IRknnTileRunner runner, ushort[] plane, int width, int height,
                                          double strength, double clip) {
        int tile = runner.TileSize;          // 256
        int stride = tile / 2;               // 128
        int margin = (tile - stride) / 2;    // 64
        strength = Math.Clamp(strength, 0.0, 1.0);

        int itw = (int)Math.Ceiling((double)width / stride);
        int ith = (int)Math.Ceiling((double)height / stride);
        int padW = itw * stride + 2 * margin;
        int padH = ith * stride + 2 * margin;
        int offsetX = (padW - width) / 2;
        int offsetY = (padH - height) / 2;
        const double inv = 1.0 / 65535.0;

        var (median, mad) = RknnImageMath.MedianMadSampledU16(plane);
        double invMadScaled = 0.04 / mad;
        double madPerNorm = mad / 0.04;
        double thresholdNorm = clip / 0.04 * mad + median;

        float PaddedRead(int px, int py) {
            int x = px - offsetX; int y = py - offsetY;
            if (x < 0) x = 0; else if (x >= width) x = width - 1;
            if (y < 0) y = 0; else if (y >= height) y = height - 1;
            return (float)(plane[y * width + x] * inv);
        }

        var dst = new ushort[width * height];
        var tensor = new float[tile * tile * 3];

        for (int ty = 0; ty < ith; ty++) {
            for (int tx = 0; tx < itw; tx++) {
                int sx = tx * stride;
                int sy = ty * stride;

                for (int y = 0; y < tile; y++) {
                    int py = sy + y;
                    int rowBase = y * tile;
                    for (int x = 0; x < tile; x++) {
                        double v = PaddedRead(sx + x, py);
                        double n = (v - median) * invMadScaled;
                        if (n > clip) n = clip; else if (n < -clip) n = -clip;
                        int b = (rowBase + x) * 3;
                        float nf = (float)n;
                        tensor[b] = nf; tensor[b + 1] = nf; tensor[b + 2] = nf;
                    }
                }

                var outData = runner.RunTile(tensor);

                for (int y = 0; y < stride; y++) {
                    int padY = sy + margin + y;
                    int rawY = padY - offsetY;
                    if (rawY < 0 || rawY >= height) continue;
                    int tileRow = (margin + y) * tile + margin;
                    int rawRow = rawY * width;
                    for (int x = 0; x < stride; x++) {
                        int padX = sx + margin + x;
                        int rawX = padX - offsetX;
                        if (rawX < 0 || rawX >= width) continue;
                        int i3 = (tileRow + x) * 3;
                        double denoised = ((outData[i3] + outData[i3 + 1] + outData[i3 + 2]) / 3.0)
                                          * madPerNorm + median;
                        double orig = plane[rawRow + rawX] * inv;
                        double masked = orig < thresholdNorm ? denoised : orig;
                        double blended = masked * strength + orig * (1 - strength);
                        dst[rawRow + rawX] = (ushort)Math.Clamp(Math.Round(blended * 65535.0), 0, 65535);
                    }
                }
            }
        }

        // Star protection (NPU-specific). The RKNN fp16 NPU execution rings
        // around bright stars (a dark halo) where the GPU/CPU ONNX paths do
        // not — it comes from the runtime's Resize/precision, not our tiling
        // (it changes with the model: v3 rings less than v2). Mitigate it the
        // way denoise tools do: keep the ORIGINAL pixels in a feathered halo
        // around bright cores, so the ring is replaced by the (bright, low-
        // visible-noise) original. Cores are pixels the blend-mask already
        // protects (orig > thresholdNorm); a few box-blur passes feather the
        // mask out ~12 px to cover the ring.
        int hw = width * height;
        var prot = new float[hw];
        for (int i = 0; i < hw; i++)
            prot[i] = (plane[i] * inv) > thresholdNorm ? 1f : 0f;
        prot = RknnImageMath.BoxBlurF(prot, width, height, passes: 3, radius: 4);
        for (int i = 0; i < hw; i++) {
            float w = prot[i];
            if (w <= 0.002f) continue;          // far from any star, leave denoised
            if (w > 1f) w = 1f;
            double blended = plane[i] * w + dst[i] * (1 - w);
            dst[i] = (ushort)Math.Clamp(Math.Round(blended), 0, 65535);
        }
        return dst;
    }
}
