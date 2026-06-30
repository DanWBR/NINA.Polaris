// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

using System;
using System.Threading.Tasks;

namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Richardson-Lucy deconvolution (Richardson 1972; Lucy 1974) on linear,
/// non-negative image data, driven by a <see cref="PsfModel"/> measured from
/// the frame's own stars. Because the PSF is the real instrument+seeing
/// response — not a single guessed FWHM — the inverse problem is well posed.
///
/// Differentiators baked in:
///   • Total-variation regularization (Dey et al. 2006) damps the noise
///     amplification / ringing that plagues vanilla RL, while keeping edges;
///   • an optional support mask blends the result back to the original in the
///     faint background (apply sharpening only where there is real signal);
///   • an optional global flux-conservation rescale, so the deconvolution is
///     photometrically safe (total counts preserved).
///
/// CPU implementation (parallel spatial convolution). A GPU/FFT path is a
/// later phase; the math here is the reference the GPU must match.
/// </summary>
public class RichardsonLucyDeconvolution {
    /// <summary>Number of RL iterations. More = sharper but noisier/ringier;
    /// the regularizer and the support mask keep this in check.</summary>
    public int Iterations { get; set; } = 15;

    /// <summary>Total-variation weight λ (0 disables TV). Small values
    /// (~1e-3..5e-3) suppress noise growth without softening real edges.</summary>
    public double TvLambda { get; set; } = 0.002;

    /// <summary>Rescale the result so its total flux matches the input
    /// (photometric safety). RL is already near flux-conserving; this removes
    /// the small residual drift from boundary handling.</summary>
    public bool ConserveFlux { get; set; } = true;

    /// <summary>
    /// Use FFT-based convolution instead of the spatial path. Cost becomes
    /// O(N log N) independent of the PSF stamp size — essential for full-res
    /// frames with large measured PSFs on a low-power SBC. Numerically
    /// equivalent to the spatial path in the interior (zero-padded linear
    /// convolution with edge replication).
    /// </summary>
    public bool UseFft { get; set; } = false;

    /// <summary>
    /// Damping threshold T (in units of the measured noise σ) for damped
    /// Richardson-Lucy (White 1994). When &gt; 0 AND a per-pixel σ map is
    /// supplied, the RL correction is suppressed wherever the re-blurred model
    /// already matches the data to within ~T·σ — this is what kills the
    /// noise amplification and the dark over/under-shoot rings around stars
    /// that plague vanilla RL. 0 disables damping (classic RL).
    /// </summary>
    public double DampingThreshold { get; set; } = 0;

    private const float Eps = 1e-6f;

    /// <summary>Map a 0..1 UI strength to an iteration count (0 = no-op).</summary>
    public static int IterationsFromStrength(double strength, int maxIterations = 30) {
        strength = Math.Clamp(strength, 0, 1);
        return (int)Math.Round(strength * maxIterations);
    }

    public float[] Deconvolve(ushort[] image, int width, int height, PsfModel psf,
                              float[] supportMask = null, float[] noiseSigma = null) {
        if (image == null) throw new ArgumentNullException(nameof(image));
        var f = new float[image.Length];
        for (int i = 0; i < image.Length; i++) f[i] = image[i];
        return Deconvolve(f, width, height, psf, supportMask, noiseSigma);
    }

    /// <summary>
    /// Deconvolve a single-channel linear image. Returns a new buffer; the
    /// input is left untouched. <paramref name="supportMask"/> (0..1, same
    /// size) is optional — where 0 the original is kept, where 1 the full
    /// deconvolution is used.
    /// </summary>
    public float[] Deconvolve(float[] image, int width, int height, PsfModel psf,
                              float[] supportMask = null, float[] noiseSigma = null) {
        if (image == null) throw new ArgumentNullException(nameof(image));
        if (psf == null) throw new ArgumentNullException(nameof(psf));
        if (image.Length != (long)width * height)
            throw new ArgumentException("image length != width*height", nameof(image));
        if (supportMask != null && supportMask.Length != image.Length)
            throw new ArgumentException("mask length != image length", nameof(supportMask));
        if (noiseSigma != null && noiseSigma.Length != image.Length)
            throw new ArgumentException("sigma length != image length", nameof(noiseSigma));

        bool damp = DampingThreshold > 0 && noiseSigma != null;

        int n = image.Length;
        int ks = psf.Size, kr = ks / 2;
        var h = psf.Kernel;
        var flipH = new float[h.Length];           // 180°-rotated PSF = adjoint
        for (int i = 0; i < h.Length; i++) flipH[i] = h[h.Length - 1 - i];

        if (Iterations <= 0) return (float[])image.Clone();

        // Observed image (non-negative) and the running estimate.
        var obs = new float[n];
        for (int i = 0; i < n; i++) obs[i] = image[i] > 0 ? image[i] : 0;
        var est = (float[])obs.Clone();

        var blur = new float[n];
        var ratio = new float[n];
        var corr = new float[n];
        float[] tv = TvLambda > 0 ? new float[n] : null;

        // FFT engine (built once from the PSF) when enabled — convolution cost
        // then no longer scales with the kernel size.
        var fft = UseFft ? new FftConvolver(h, ks, width, height) : null;

        float dampT = (float)DampingThreshold;
        for (int it = 0; it < Iterations; it++) {
            if (fft != null) { var b = fft.Convolve(est); Array.Copy(b, blur, n); }  // H·e
            else Correlate(est, width, height, flipH, ks, kr, blur);                 // H·e
            if (damp) {
                // Damped RL (White 1994): suppress the correction where the
                // re-blurred model already fits the data to within ~T·σ. The
                // raw ratio r = d/(H·e) is pulled toward 1 by a factor U that
                // ramps 0→1 as the residual |d − H·e| grows from 0 to T·σ, so
                // sub-noise corrections (noise speckle, over/under-shoot rings)
                // are not amplified, while genuine under-fit structure still
                // gets the full RL push.
                for (int i = 0; i < n; i++) {
                    float r = obs[i] / (blur[i] + Eps);
                    float sig = noiseSigma[i] > Eps ? noiseSigma[i] : Eps;
                    float z = Math.Abs(obs[i] - blur[i]) / (dampT * sig);
                    if (z > 1f) z = 1f;
                    float u = z * z * (3f - 2f * z);                 // smoothstep
                    ratio[i] = 1f + u * (r - 1f);
                }
            } else {
                for (int i = 0; i < n; i++) ratio[i] = obs[i] / (blur[i] + Eps);
            }
            Correlate(ratio, width, height, h, ks, kr, corr);        // Hᵀ·(d / H·e)

            if (tv != null) {
                TvFactor(est, width, height, TvLambda, tv);          // 1 − λ·div(∇e/|∇e|)
                for (int i = 0; i < n; i++) {
                    float v = est[i] * corr[i] / tv[i];
                    est[i] = v > 0 ? v : 0;
                }
            } else {
                for (int i = 0; i < n; i++) {
                    float v = est[i] * corr[i];
                    est[i] = v > 0 ? v : 0;
                }
            }
        }

        // Photometric safety: match total flux to the input.
        if (ConserveFlux) {
            double so = 0, se = 0;
            for (int i = 0; i < n; i++) { so += obs[i]; se += est[i]; }
            if (se > 0) {
                float k = (float)(so / se);
                for (int i = 0; i < n; i++) est[i] *= k;
            }
        }

        // Apply only where there is signal (support mask), else keep original.
        if (supportMask != null) {
            for (int i = 0; i < n; i++) {
                float m = supportMask[i];
                est[i] = image[i] + m * (est[i] - image[i]);
            }
        }
        return est;
    }

    /// <summary>
    /// Build a feathered support mask (0..1): ~0 in the faint, noise-dominated
    /// background and ~1 over real signal, from a robust background+noise
    /// estimate. Lets the caller restrict sharpening to where it helps.
    /// </summary>
    public static float[] BuildSupportMask(float[] image, int width, int height,
                                           double background, double noise,
                                           double lowSigma = 2.0, double highSigma = 8.0) {
        int n = image.Length;
        var mask = new float[n];
        double lo = background + lowSigma * noise;
        double hi = background + highSigma * noise;
        double span = Math.Max(1e-6, hi - lo);
        for (int i = 0; i < n; i++) {
            double t = (image[i] - lo) / span;      // smoothstep ramp
            t = Math.Clamp(t, 0, 1);
            mask[i] = (float)(t * t * (3 - 2 * t));
        }
        return mask;
    }

    /// <summary>
    /// Build a support mask that ramps on the local <em>signal-to-noise ratio</em>
    /// using a measured per-pixel σ map (<see cref="NoiseMap"/>) instead of a
    /// single flat background noise. Because astronomical noise grows with signal
    /// (shot noise), a flat threshold either over-sharpens noisy bright
    /// nebulosity or holds back faint signal where the read floor is low. Here
    /// SNRᵢ = (imageᵢ − background)/σᵢ ramps from <paramref name="lowSnr"/> (mask 0)
    /// to <paramref name="highSnr"/> (mask 1), so sharpening tracks real SNR per
    /// pixel.
    /// </summary>
    public static float[] BuildNoiseAdaptiveSupportMask(float[] image, float[] sigmaMap,
                                                        double background,
                                                        double lowSnr = 2.0,
                                                        double highSnr = 8.0) {
        int n = image.Length;
        var mask = new float[n];
        double span = Math.Max(1e-6, highSnr - lowSnr);
        for (int i = 0; i < n; i++) {
            double sigma = (sigmaMap != null && i < sigmaMap.Length) ? sigmaMap[i] : 0;
            if (sigma < 1e-6) sigma = 1e-6;
            double snr = (image[i] - background) / sigma;
            double t = Math.Clamp((snr - lowSnr) / span, 0, 1);   // smoothstep ramp
            mask[i] = (float)(t * t * (3 - 2 * t));
        }
        return mask;
    }

    /// <summary>
    /// Zero the support <paramref name="mask"/> within <paramref name="dilate"/>
    /// pixels of any saturated pixel (≥ <paramref name="satLevel"/>). The cores
    /// of saturated stars are clipped — the PSF can't reconcile a flat-topped
    /// peak, so RL drives the surrounding wing pixels down and rings them with a
    /// dark halo. Keeping the original over those stars (and a halo the size of
    /// the PSF) removes that artifact; they can't be honestly deconvolved
    /// anyway. No-op when nothing is saturated.
    /// </summary>
    public static void ApplySaturationGuard(float[] mask, float[] image, int width, int height,
                                            double satLevel, int dilate) {
        if (mask == null || image == null || dilate < 0) return;
        int n = image.Length;
        var sat = new bool[n];
        bool any = false;
        for (int i = 0; i < n; i++) if (image[i] >= satLevel) { sat[i] = true; any = true; }
        if (!any) return;

        // Separable box dilation of the saturated set by `dilate` px each axis.
        var rowD = new bool[n];
        Parallel.For(0, height, y => {
            int row = y * width;
            for (int x = 0; x < width; x++) {
                bool hit = false;
                int x0 = Math.Max(0, x - dilate), x1 = Math.Min(width - 1, x + dilate);
                for (int xx = x0; xx <= x1 && !hit; xx++) if (sat[row + xx]) hit = true;
                rowD[row + x] = hit;
            }
        });
        Parallel.For(0, width, x => {
            for (int y = 0; y < height; y++) {
                bool hit = false;
                int y0 = Math.Max(0, y - dilate), y1 = Math.Min(height - 1, y + dilate);
                for (int yy = y0; yy <= y1 && !hit; yy++) if (rowD[yy * width + x]) hit = true;
                if (hit) mask[y * width + x] = 0f;
            }
        });
    }

    // ── spatial cross-correlation with reflect borders (parallel over rows) ──
    private static void Correlate(float[] src, int w, int h, float[] ker, int ks, int kr,
                                  float[] dst) {
        Parallel.For(0, h, y => {
            for (int x = 0; x < w; x++) {
                float acc = 0;
                int kidx = 0;
                for (int ky = -kr; ky <= kr; ky++) {
                    int sy = Reflect(y + ky, h);
                    int row = sy * w;
                    for (int kx = -kr; kx <= kr; kx++) {
                        int sx = Reflect(x + kx, w);
                        acc += ker[kidx++] * src[row + sx];
                    }
                }
                dst[y * w + x] = acc;
            }
        });
    }

    private static int Reflect(int i, int n) {
        if (i < 0) i = -i - 1;
        if (i >= n) i = 2 * n - i - 1;
        if (i < 0) i = 0; else if (i >= n) i = n - 1;   // safety for tiny n
        return i;
    }

    // TV regularizer factor f = 1 − λ·div(∇u/|∇u|), clamped to a safe band so
    // the RL update can't blow up or change sign (Dey et al. 2006).
    private static void TvFactor(float[] u, int w, int h, double lambda, float[] outFactor) {
        const float tvEps = 1e-4f;
        Parallel.For(0, h, y => {
            for (int x = 0; x < w; x++) {
                int i = y * w + x;
                double div = NormGradComponent(u, w, h, x, y, true)
                           - NormGradComponent(u, w, h, x - 1, y, true)
                           + NormGradComponent(u, w, h, x, y, false)
                           - NormGradComponent(u, w, h, x, y - 1, false);
                float f = (float)(1.0 - lambda * div);
                outFactor[i] = Math.Clamp(f, 0.5f, 1.5f);
            }
        });

        static double NormGradComponent(float[] u, int w, int h, int x, int y, bool horizontal) {
            if (x < 0 || y < 0 || x >= w || y >= h) return 0;
            int i = y * w + x;
            double gx = (x + 1 < w) ? u[i + 1] - u[i] : 0;
            double gy = (y + 1 < h) ? u[i + w] - u[i] : 0;
            double mag = Math.Sqrt(gx * gx + gy * gy) + tvEps;
            return (horizontal ? gx : gy) / mag;
        }
    }
}
