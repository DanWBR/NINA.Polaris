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
/// Pure tile-pipeline math for the RKNN host path. The denoise path is ported
/// from GraXpert's own <c>denoising.py</c> (real-RGB single pass, per-channel
/// median/MAD, per-tile star-core mask + global blend); BGE mirrors the browser
/// <c>onnx-pipelines.js</c>. Everything here is deterministic and takes an
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

        // 8) Apply correction PER CHANNEL, matching the browser onnx-pipelines.js
        //    BGE (each channel recentred on its OWN median). This preserves the
        //    image's colour/saturation. (A single global mean -- GraXpert CLI's
        //    Subtraction -- neutralises the background to grey but shifts each
        //    channel's level, which visibly changes saturation; the browser/GPU
        //    path the user compares against does NOT do that.)
        var result = new ushort[pixels.Length];
        background = saveBackground ? new ushort[pixels.Length] : null;
        for (int c = 0; c < channels; c++) {
            var bg = bgFull[c];
            double median = med[c];
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

    /// <summary>
    /// Denoise on the NPU, processed PER CHANNEL with the 3 model output channels
    /// AVERAGED. Each colour plane is fed to the model replicated into all 3 input
    /// channels and the 3 outputs are averaged before denormalize. This is
    /// deliberate, not the GraXpert single-RGB pass: the RKNN-converted model is
    /// inconsistent between its output channels (rknn-vs-onnx ~0.2 per element,
    /// while the 3-channel MEAN is close), so the average cancels that and a real
    /// single-RGB pass instead surfaces it as colour banding. The browser path
    /// (fp32 ONNX, no such defect) keeps the faster single-RGB pass.
    ///
    /// Hard inner-[margin:margin+stride] tile extraction (margins discarded, like
    /// GraXpert / the browser). Per channel: robust median/MAD; tile-normalize
    /// <c>(v-median)/mad*0.04</c>, clip to +/-<paramref name="clip"/> for the model
    /// but keep the unclipped copy for GraXpert's star-core mask; denormalize; the
    /// global blend keeps bright pixels original and mixes by
    /// <paramref name="strength"/>. <paramref name="clip"/> is 1.0 for v3, 10.0 for v2.
    /// A final NPU-only star-protection halo hides the fp16 ring around bright
    /// stars. <paramref name="pixels"/> is plane-sequential; output matches.
    /// </summary>
    public static ushort[] RunDenoise(IRknnTileRunner runner, ushort[] pixels, int width, int height,
                                      int channels, double strength, double clip) {
        int tile = runner.TileSize;          // 256
        int stride = tile / 2;               // 128
        int margin = (tile - stride) / 2;    // 64
        strength = Math.Clamp(strength, 0.0, 1.0);
        int planeLen = width * height;
        const double inv = 1.0 / 65535.0;
        int nc = channels >= 3 ? 3 : 1;

        // Per-channel robust stats (GraXpert computes median/MAD per channel).
        var med = new double[nc];
        var mad = new double[nc];
        for (int c = 0; c < nc; c++)
            (med[c], mad[c]) = RknnImageMath.MedianMadSampledU16(pixels.AsSpan(c * planeLen, planeLen));

        int itw = (int)Math.Ceiling((double)width / stride);
        int ith = (int)Math.Ceiling((double)height / stride);
        int padW = itw * stride + 2 * margin;
        int padH = ith * stride + 2 * margin;
        int offsetX = (padW - width) / 2;
        int offsetY = (padH - height) / 2;

        float PaddedRead(int chan, int px, int py) {
            int x = px - offsetX; int y = py - offsetY;
            if (x < 0) x = 0; else if (x >= width) x = width - 1;
            if (y < 0) y = 0; else if (y >= height) y = height - 1;
            return (float)(pixels[chan * planeLen + y * width + x] * inv);
        }

        // Hard inner-tile extraction, IDENTICAL to GraXpert / the browser path:
        // write only the inner [margin:margin+stride] of each tile and DISCARD
        // the margins, where the model output is unreliable. (A windowed
        // overlap-add was tried and reverted: it mixes those unreliable margins
        // into the neighbours, which produced colour bands that the browser/GPU
        // path -- which discards the margins -- does not have.)
        var dst = new ushort[pixels.Length];
        var tensor = new float[tile * tile * 3];   // model input (replicated to 3ch)
        var copyN = new float[tile * tile];        // unclipped normalized (1 channel)

        // PER CHANNEL: feed each plane replicated into all 3 input channels and
        // AVERAGE the 3 output channels. The RKNN-converted model is inconsistent
        // between its output channels (rknn-vs-onnx ~0.2 per element, but the
        // 3-channel mean is close), so averaging cancels it. Feeding the real RGB
        // together (single pass) instead exposes it as colour banding -- so the
        // NPU keeps this per-channel+average path (the browser uses single RGB).
        for (int oc = 0; oc < nc; oc++) {
            int off = oc * planeLen;
            double m = med[oc], a = mad[oc];
            double madPerNorm = a / 0.04;
            double invMadScaled = 0.04 / a;
            double threshold = clip / 0.04 * a + m;

            for (int ty = 0; ty < ith; ty++) {
                for (int tx = 0; tx < itw; tx++) {
                    int sx = tx * stride;
                    int sy = ty * stride;

                    for (int y = 0; y < tile; y++) {
                        int py = sy + y;
                        int rowBase = y * tile;
                        for (int x = 0; x < tile; x++) {
                            double v = PaddedRead(oc, sx + x, py);
                            double n = (v - m) * invMadScaled;
                            int p = rowBase + x;
                            copyN[p] = (float)n;
                            float cl = (float)(n > clip ? clip : (n < -clip ? -clip : n));
                            int b = p * 3;
                            tensor[b] = cl; tensor[b + 1] = cl; tensor[b + 2] = cl;
                        }
                    }

                    var outData = runner.RunTile(tensor);

                    for (int y = 0; y < stride; y++) {
                        int rawY = sy + margin + y - offsetY;
                        if (rawY < 0 || rawY >= height) continue;
                        int tileRow = (margin + y) * tile + margin;
                        int rawRow = rawY * width;
                        for (int x = 0; x < stride; x++) {
                            int rawX = sx + margin + x - offsetX;
                            if (rawX < 0 || rawX >= width) continue;
                            int p = tileRow + x;
                            int i3 = p * 3;
                            double cn = copyN[p];                                  // unclipped input
                            double avg = (outData[i3] + outData[i3 + 1] + outData[i3 + 2]) / 3.0;
                            double mn = cn < clip ? avg : cn;                      // star-core mask
                            double denoised = mn * madPerNorm + m;
                            double orig = pixels[off + rawRow + rawX] * inv;
                            double masked = orig < threshold ? denoised : orig;
                            double blended = masked * strength + orig * (1 - strength);
                            dst[off + rawRow + rawX] =
                                (ushort)Math.Clamp(Math.Round(blended * 65535.0), 0, 65535);
                        }
                    }
                }
            }
        }

        // NPU-specific star protection (per channel). The RKNN fp16 NPU rings a
        // dark halo around bright stars where the GPU/CPU ONNX paths do not (it
        // changes with the model: v3 rings less than v2). Keep the ORIGINAL in a
        // feathered halo around bright cores so the ring is replaced by the
        // original. The threshold is a fixed ~25-sigma (mad is the robust sigma),
        // INDEPENDENT of the model's clip, so medium + bright stars are protected
        // on both v2 (clip 10) and v3 (clip 1).
        const double starSigma = 25.0;
        for (int c = 0; c < nc; c++) {
            int off = c * planeLen;
            double starThresh = med[c] + starSigma * mad[c];
            var prot = new float[planeLen];
            for (int i = 0; i < planeLen; i++)
                prot[i] = (pixels[off + i] * inv) > starThresh ? 1f : 0f;
            // Feather wide enough (~32px) to cover the NPU "ghost star" side-lobes
            // that sit at a fixed radius around bright stars, not just the core.
            prot = RknnImageMath.BoxBlurF(prot, width, height, passes: 4, radius: 8);
            for (int i = 0; i < planeLen; i++) {
                float w = prot[i];
                if (w <= 0.002f) continue;       // far from any star, leave denoised
                if (w > 1f) w = 1f;
                double blended = pixels[off + i] * w + dst[off + i] * (1 - w);
                dst[off + i] = (ushort)Math.Clamp(Math.Round(blended), 0, 65535);
            }
        }
        return dst;
    }

    /// <summary>Mono convenience wrapper over <see cref="RunDenoise"/> (tests).</summary>
    public static ushort[] RunDenoiseMono(IRknnTileRunner runner, ushort[] plane, int width, int height,
                                          double strength, double clip)
        => RunDenoise(runner, plane, width, height, 1, strength, clip);
}
