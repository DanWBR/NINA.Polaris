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
/// BGE (single forward pass), Denoise (256/128/64 tiling) and Deconvolution
/// (512 NCHW, two-input image+params via <see cref="RunDecon"/>) are implemented.
/// The QNN/Hexagon lane runs all three on the NPU; the Rockchip <c>RknnSession</c>
/// only wires the single-input models, so decon there still falls back to the CLI.
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

        // NPU-specific star protection (per channel). The RKNN fp16 NPU draws a
        // dark ring/moat around bright stars where the GPU/CPU ONNX paths do not.
        // We composite the ORIGINAL back over the star core AND its ring.
        //
        // The mask must be SOLID (w==1) across the whole core+ring, feathering
        // only at the outer rim. A previous version seeded only the bright cores
        // and then box-blurred them: a few-pixel core averaged over a wide kernel
        // collapses to w~0.01-0.1, so the blend (orig*w + dst*(1-w)) returned only
        // a few percent of the original and the ring survived -- and widening the
        // feather made it WEAKER (more spread = smaller values). The fix is to
        // DILATE the seed to cover the ring (a solid plateau) and feather only the
        // edge. starSigma is moderate so medium+bright (ring-producing) stars are
        // caught on both v2 (clip 10) and v3 (clip 1); faint stars don't ring.
        const double starSigma = 10.0;
        const int ringRadius = 14;   // dilation: solid protection out past the ring
        const int featherRadius = 8; // soft rim so the composite has no hard edge
        for (int c = 0; c < nc; c++) {
            int off = c * planeLen;
            double starThresh = med[c] + starSigma * mad[c];
            var seed = new float[planeLen];
            for (int i = 0; i < planeLen; i++)
                seed[i] = (pixels[off + i] * inv) > starThresh ? 1f : 0f;
            // Dilate: one box pass then binarize (any seed inside the window =>
            // protected), so the solid region grows by ~ringRadius around each
            // star and fully covers the dark ring.
            var dil = RknnImageMath.BoxBlurF(seed, width, height, passes: 1, radius: ringRadius);
            for (int i = 0; i < planeLen; i++) dil[i] = dil[i] > 0f ? 1f : 0f;
            // Feather only the rim of the dilated plateau.
            var prot = RknnImageMath.BoxBlurF(dil, width, height, passes: 2, radius: featherRadius);
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

    /// <summary>
    /// StarNet v1 star removal on the NPU. Single inference per tile over a
    /// <c>[1,256,256,3]</c> NHWC tensor — the SAME single-input/single-output
    /// contract as <see cref="RunDenoise"/>/<see cref="RunBge"/>, so it runs on
    /// both the RKNN and QNN lanes (record/replay) unchanged. Faithful port of the
    /// browser <c>StarRemovalPipeline</c> (starnet profile):
    /// <list type="bullet">
    /// <item>MTF-autostretch each channel into the network's trained (non-linear)
    /// domain ("15% bg, 3σ"), run, then INVERSE-stretch the output back to the
    /// source's linear range — without this, linear stacks come out with black
    /// holes where bright stars were.</item>
    /// <item>The model output IS the starless image (the graph emits
    /// <c>input − ReLU(decoder)</c>); hard inner-tile extraction at stride 96
    /// (80px context margin per edge), margins discarded.</item>
    /// <item>Mono replicates the plane to 3 input channels and averages the 3
    /// outputs; RGB runs all three together in one inference.</item>
    /// </list>
    /// Optional multi-pass (≤3) feeds the starless back through the net to clean
    /// residual halos; each pass recomputes its own autostretch. Returns
    /// <c>(starless, stars)</c> where <c>stars = clamp(original − starless, 0)</c>,
    /// both in the same channel layout as the input — ready for the Image Blend
    /// tool (starless = base, stars = blend).
    /// </summary>
    public static (ushort[] starless, ushort[] stars) RunStarRemoval(
            IRknnTileRunner runner, ushort[] pixels, int width, int height, int channels,
            int passes = 1, double stretchTarget = 0.15, bool autoStretch = true) {
        int tile = runner.TileSize;                        // 256
        int stride = Math.Clamp(tile * 3 / 8, 32, tile);   // 96 for tile 256
        int margin = (tile - stride) / 2;                  // 80
        int nc = channels >= 3 ? 3 : 1;
        int planeLen = width * height;
        const double inv = 1.0 / 65535.0;
        passes = Math.Clamp(passes, 1, 3);
        double target = Math.Clamp(stretchTarget, 0.02, 0.6);

        // MTF (midtones transfer function); its inverse is MTF with midtone 1−m.
        static double Mtf(double x, double m) {
            if (x <= 0) return 0; if (x >= 1) return 1;
            if (m <= 0) return 1; if (m >= 1) return 0;
            return ((m - 1) * x) / ((2 * m - 1) * x - m);
        }

        int itw = (int)Math.Ceiling((double)width / stride);
        int ith = (int)Math.Ceiling((double)height / stride);
        int padW = itw * stride + 2 * margin;
        int padH = ith * stride + 2 * margin;
        int offsetX = (padW - width) / 2;
        int offsetY = (padH - height) / 2;

        var cur = (ushort[])pixels.Clone();   // this pass's (linear) input
        var sh = new double[nc]; var scl = new double[nc]; var mid = new double[nc];
        var tensor = new float[tile * tile * 3];

        for (int pass = 0; pass < passes; pass++) {
            // Per-channel stretch params from THIS pass's input (GraXpert 15% bg, 3σ).
            for (int c = 0; c < nc; c++) {
                if (!autoStretch) { sh[c] = 0; scl[c] = 1; mid[c] = 0.5; continue; }
                var (median, mad) = RknnImageMath.MedianMadSampledU16(cur.AsSpan(c * planeLen, planeLen));
                double s = Math.Max(0, median - 3.0 * mad);
                double denom = Math.Max(1e-6, 1 - s);
                double xMed = Math.Clamp((median - s) / denom, 0, 1);
                double m = Math.Clamp(Mtf(xMed, target), 0.001, 0.999);
                sh[c] = s; scl[c] = 1.0 / denom; mid[c] = m;
            }
            double StretchVal(double v, int c) {
                if (!autoStretch) return v;
                double x = (v - sh[c]) * scl[c];
                if (x < 0) x = 0; else if (x > 1) x = 1;
                return Mtf(x, mid[c]);
            }
            double UnstretchVal(double s, int c) {
                if (!autoStretch) return s;
                if (s < 0) s = 0; else if (s > 1) s = 1;
                double x = Mtf(s, 1 - mid[c]);
                double v = sh[c] + x * (1 - sh[c]);
                return v < 0 ? 0 : (v > 1 ? 1 : v);
            }
            float PaddedRead(int chan, int px, int py) {
                int x = px - offsetX; int y = py - offsetY;
                if (x < 0) x = 0; else if (x >= width) x = width - 1;
                if (y < 0) y = 0; else if (y >= height) y = height - 1;
                return (float)(cur[chan * planeLen + y * width + x] * inv);
            }

            var starless = new ushort[cur.Length];
            for (int ty = 0; ty < ith; ty++) {
                for (int tx = 0; tx < itw; tx++) {
                    int sx = tx * stride, sy = ty * stride;
                    for (int y = 0; y < tile; y++) {
                        int py = sy + y;
                        int rowBase = y * tile;
                        for (int x = 0; x < tile; x++) {
                            int b = (rowBase + x) * 3;
                            if (nc == 3) {
                                tensor[b]     = (float)StretchVal(PaddedRead(0, sx + x, py), 0);
                                tensor[b + 1] = (float)StretchVal(PaddedRead(1, sx + x, py), 1);
                                tensor[b + 2] = (float)StretchVal(PaddedRead(2, sx + x, py), 2);
                            } else {
                                float v = (float)StretchVal(PaddedRead(0, sx + x, py), 0);
                                tensor[b] = v; tensor[b + 1] = v; tensor[b + 2] = v;
                            }
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
                            int p = (tileRow + x) * 3;
                            if (nc == 3) {
                                for (int c = 0; c < 3; c++) {
                                    double sv = UnstretchVal(outData[p + c], c);
                                    starless[c * planeLen + rawRow + rawX] =
                                        (ushort)Math.Clamp(Math.Round(sv * 65535.0), 0, 65535);
                                }
                            } else {
                                double avg = (outData[p] + outData[p + 1] + outData[p + 2]) / 3.0;
                                double sv = UnstretchVal(avg, 0);
                                starless[rawRow + rawX] =
                                    (ushort)Math.Clamp(Math.Round(sv * 65535.0), 0, 65535);
                            }
                        }
                    }
                }
            }
            cur = starless;   // feed forward for the next pass
        }

        // stars = clamp(original − final starless, 0).
        var stars = new ushort[pixels.Length];
        for (int i = 0; i < pixels.Length; i++) {
            int d = pixels[i] - cur[i];
            stars[i] = d > 0 ? (ushort)d : (ushort)0;
        }
        return (cur, stars);
    }

    /// <summary>Mono convenience wrapper over <see cref="RunStarRemoval"/> (tests).</summary>
    public static (ushort[] starless, ushort[] stars) RunStarRemovalMono(
            IRknnTileRunner runner, ushort[] plane, int width, int height, int passes = 1)
        => RunStarRemoval(runner, plane, width, height, 1, passes);

    // ─── Deconvolution (GraXpert stars/objects, 512 NCHW, 2-input) ────────
    private const int DeconTile = 512;
    private const int DeconStride = 448;
    private const int DeconMargin = (DeconTile - DeconStride) / 2;   // 32
    private const float DeconEps = 1e-5f;

    /// <summary>
    /// Map a PSF size (FWHM in pixels) to the model's normalized sigma condition,
    /// using GraXpert's model-specific formulas (from <c>deconvolution.py</c>):
    /// FWHM → σ = fwhm/2.355, then a per-target/version linear map, clamped to
    /// [0.05, 0.95]. Feeding the wrong sigma produces tile-shaped seams, so this
    /// mirrors <c>DeconPipeline</c> exactly.
    /// </summary>
    public static float DeconSigmaNormalized(string target, string version, double psfPixels) {
        psfPixels = Math.Clamp(psfPixels, 0.05, 15.0);
        double sigma = psfPixels / 2.355;
        bool objects = string.Equals(target, "objects", StringComparison.OrdinalIgnoreCase);
        double v = objects
            ? (version == "1.0.0" ? (sigma - 1.0) / 5.0 : (sigma - 0.5) / 5.5)
            : (sigma - 1.5) / 3.0;                                   // stellar (v1.0.0)
        return (float)Math.Clamp(v, 0.05, 0.95);
    }

    /// <summary>
    /// Deconvolution (stars / objects). Ported from <c>DeconPipeline._runMono</c>:
    /// per plane, pad to a 448-stride grid, tile 512×512 with a 32-px context
    /// margin, per-tile log-mean-std normalize <c>(log(v−min+ε)−mean)·0.1/std</c>,
    /// run the model (image tile + <c>[sigmaNorm, effStrength]</c> params), subtract
    /// the returned residual in the normalized log domain, inverse-log, and keep
    /// the inner stride×stride region (tiles abut seamlessly). RGB runs the plane
    /// pipeline three times. Deterministic given the runner, so unit-testable
    /// without an NPU. <paramref name="strength"/> gets GraXpert's 0.95 cap.
    /// </summary>
    public static ushort[] RunDecon(IRknnDeconTileRunner runner, ushort[] pixels,
                                    int width, int height, int channels,
                                    string target, string version,
                                    double psfPixels, double strength) {
        float sigmaNorm = DeconSigmaNormalized(target, version, psfPixels);
        float effStrength = (float)(Math.Clamp(strength, 0.0, 1.0) * 0.95);
        var pars = new[] { sigmaNorm, effStrength };

        int planeLen = width * height;
        var outPixels = new ushort[pixels.Length];
        for (int c = 0; c < channels; c++) {
            var plane = RunDeconPlane(runner, pixels.AsSpan(c * planeLen, planeLen),
                                      width, height, pars);
            Array.Copy(plane, 0, outPixels, c * planeLen, planeLen);
        }
        return outPixels;
    }

    private static ushort[] RunDeconPlane(IRknnDeconTileRunner runner, ReadOnlySpan<ushort> plane,
                                          int width, int height, float[] pars) {
        const int T = DeconTile, S = DeconStride, M = DeconMargin;
        const float eps = DeconEps;
        int itw = (width + S - 1) / S;                  // ceil(width / stride)
        int ith = (height + S - 1) / S;
        int padW = itw * S + 2 * M;
        int padH = ith * S + 2 * M;
        int offX = (padW - width) / 2;
        int offY = (padH - height) / 2;

        // Edge-replicate pad, then normalize to [0,1].
        var planeF = new float[padW * padH];
        for (int y = 0; y < padH; y++) {
            int sy = Math.Clamp(y - offY, 0, height - 1);
            for (int x = 0; x < padW; x++) {
                int sx = Math.Clamp(x - offX, 0, width - 1);
                planeF[y * padW + x] = plane[sy * width + sx] / 65535f;
            }
        }

        var outF = new float[padW * padH];
        var tile = new float[T * T];
        var tensor = new float[T * T];
        for (int ty = 0; ty < ith; ty++) {
            for (int tx = 0; tx < itw; tx++) {
                int sx = tx * S, sy = ty * S;

                // Gather the tile + its min.
                float minV = float.PositiveInfinity;
                for (int y = 0; y < T; y++) {
                    int srcRow = (sy + y) * padW + sx, dstRow = y * T;
                    for (int x = 0; x < T; x++) {
                        float v = planeF[srcRow + x];
                        tile[dstRow + x] = v;
                        if (v < minV) minV = v;
                    }
                }

                // log(v − min + ε), then mean/std.
                double mean = 0;
                for (int i = 0; i < tile.Length; i++) {
                    tile[i] = (float)Math.Log(tile[i] - minV + eps);
                    mean += tile[i];
                }
                mean /= tile.Length;
                double varSum = 0;
                for (int i = 0; i < tile.Length; i++) {
                    double d = tile[i] - mean;
                    varSum += d * d;
                }
                double std = Math.Max(1e-6, Math.Sqrt(varSum / tile.Length));

                // (v − mean) · 0.1/std  (GraXpert convention; NOT / (std·0.1)).
                double invStd10 = 0.1 / std;
                for (int i = 0; i < tile.Length; i++)
                    tensor[i] = (float)((tile[i] - mean) * invStd10);

                var residual = runner.RunTile(tensor, pars);

                // Keep the inner stride×stride: out = inverse-log(normIn − residual).
                for (int y = 0; y < S; y++) {
                    int tileRow = (M + y) * T + M;
                    int outRow = (sy + M + y) * padW + (sx + M);
                    for (int x = 0; x < S; x++) {
                        double normOut = tensor[tileRow + x] - residual[tileRow + x];
                        double logVal = normOut * std / 0.1 + mean;
                        outF[outRow + x] = (float)(Math.Exp(logVal) + minV - eps);
                    }
                }
            }
        }

        // Trim padding + denormalize.
        var dst = new ushort[width * height];
        for (int y = 0; y < height; y++) {
            int srcRow = (offY + y) * padW + offX, dstRow = y * width;
            for (int x = 0; x < width; x++) {
                int v = (int)Math.Round(outF[srcRow + x] * 65535f);
                dst[dstRow + x] = (ushort)Math.Clamp(v, 0, 65535);
            }
        }
        return dst;
    }

    // ─── Super-resolution / upscaling (Polaris UpscaleNet) ────────────────
    private const float UpscaleClip = 10.0f;

    /// <summary>
    /// Super-resolution. Ported from <c>UpscalePipeline</c>: pre-upsampling SR
    /// where each 128² low-res tile (16-px margin, 96 stride) runs through the
    /// NHWC model (LR → HR ×scale) and the inner region is stitched into a
    /// scale×-larger canvas. Per-channel MAD-normalize (×0.04, clip ±10) from the
    /// LR input; RGB or mono (mono replicates the plane to 3 in, averages the 3
    /// out). Deterministic given the runner, so unit-testable without an NPU.
    /// Returns the enlarged image plus its new width/height.
    /// </summary>
    public static (ushort[] pixels, int width, int height) RunUpscale(
            IRknnUpscaleTileRunner runner, ushort[] pixels, int width, int height, int channels) {
        int tile = runner.TileSize;                 // 128 LR
        int scale = runner.Scale;                   // 2
        int margin = tile / 8;                      // 16
        int stride = tile - 2 * margin;             // 96
        int planeLen = width * height;
        channels = channels >= 3 ? 3 : 1;

        // Per-channel robust stats (median/MAD) from the LR input, in [0,1].
        var med = new float[3];
        var mad = new float[3];
        for (int c = 0; c < 3; c++) {
            int baseOff = channels == 3 ? c * planeLen : 0;
            var pf = new float[planeLen];
            for (int i = 0; i < planeLen; i++) pf[i] = pixels[baseOff + i] / 65535f;
            var (m, a) = RknnImageMath.MedianMadSampled(pf);
            med[c] = (float)m;
            mad[c] = (float)Math.Max(a, 1e-6);       // avoid /0 on a flat plane
        }

        int outW = width * scale, outH = height * scale;
        var dst = new ushort[outW * outH * channels];
        int itw = (width + stride - 1) / stride, ith = (height + stride - 1) / stride;
        int ht = tile * scale, hm = margin * scale, hs = stride * scale;
        var tensor = new float[tile * tile * 3];

        float Rd(int c, int px, int py) {            // edge-clamped, normalized [0,1]
            int x = px < 0 ? 0 : (px >= width ? width - 1 : px);
            int y = py < 0 ? 0 : (py >= height ? height - 1 : py);
            int baseOff = channels == 3 ? c * planeLen : 0;
            return pixels[baseOff + y * width + x] / 65535f;
        }

        for (int ty = 0; ty < ith; ty++) {
            for (int tx = 0; tx < itw; tx++) {
                int sx = tx * stride - margin, sy = ty * stride - margin;
                for (int y = 0; y < tile; y++) {
                    for (int x = 0; x < tile; x++) {
                        int b = (y * tile + x) * 3;
                        for (int c = 0; c < 3; c++) {
                            float v = (Rd(c, sx + x, sy + y) - med[c]) / mad[c] * 0.04f;
                            tensor[b + c] = v > UpscaleClip ? UpscaleClip
                                : (v < -UpscaleClip ? -UpscaleClip : v);
                        }
                    }
                }

                var od = runner.RunTile(tensor);     // [1, ht, ht, 3] NHWC
                for (int y = 0; y < hs; y++) {
                    int oy = ty * hs + y;
                    if (oy >= outH) continue;
                    int row = (hm + y) * ht;
                    for (int x = 0; x < hs; x++) {
                        int ox = tx * hs + x;
                        if (ox >= outW) continue;
                        int i3 = (row + hm + x) * 3;
                        for (int c = 0; c < channels; c++) {
                            int cc = channels == 3 ? c : 0;
                            float on = channels == 3
                                ? od[i3 + c] : (od[i3] + od[i3 + 1] + od[i3 + 2]) / 3f;
                            float dn = on / 0.04f * mad[cc] + med[cc];
                            int u = (int)(dn * 65535f + 0.5f);
                            dst[c * outW * outH + oy * outW + ox] = (ushort)Math.Clamp(u, 0, 65535);
                        }
                    }
                }
            }
        }
        return (dst, outW, outH);
    }
}
