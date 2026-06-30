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
using System.IO;
using System.Linq;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageAnalysis;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class RichardsonLucyTests {

    // Sharp star field (known narrow PSF) on flat bg + read noise.
    private static double[] MakeSharpField(int w, int h, double sigma, double amp,
                                           int seed = 99, double bg = 800, double noiseSd = 8) {
        var rng = new Random(seed);
        var img = new double[w * h];
        for (int i = 0; i < img.Length; i++) img[i] = bg + Gaussian(rng) * noiseSd;
        int margin = 40, step = 50, r = (int)Math.Ceiling(5 * sigma);
        for (int cy = margin; cy < h - margin; cy += step)
            for (int cx = margin; cx < w - margin; cx += step)
                for (int y = -r; y <= r; y++)
                    for (int x = -r; x <= r; x++) {
                        int ix = cx + x, iy = cy + y;
                        if (ix < 0 || iy < 0 || ix >= w || iy >= h) continue;
                        img[iy * w + ix] += amp * Math.Exp(-(x * x + y * y) / (2 * sigma * sigma));
                    }
        return img;
    }

    private static double Gaussian(Random r) {
        double u1 = 1.0 - r.NextDouble(), u2 = 1.0 - r.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    // Blur with a (symmetric) PSF kernel — reflect borders.
    private static float[] Convolve(double[] src, int w, int h, PsfModel psf) {
        int ks = psf.Size, kr = ks / 2; var k = psf.Kernel;
        var dst = new float[src.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                double acc = 0; int ki = 0;
                for (int ky = -kr; ky <= kr; ky++) {
                    int sy = Refl(y + ky, h);
                    for (int kx = -kr; kx <= kr; kx++) {
                        int sx = Refl(x + kx, w);
                        acc += k[ki++] * src[sy * w + sx];
                    }
                }
                dst[y * w + x] = (float)acc;
            }
        return dst;
    }
    private static int Refl(int i, int n) { if (i < 0) i = -i - 1; if (i >= n) i = 2 * n - i - 1; return Math.Clamp(i, 0, n - 1); }

    // Sensor read noise is added AFTER the PSF (it's introduced at the
    // detector), so blur the clean signal first, then call this.
    private static void AddNoise(float[] f, double sd, int seed) {
        var rng = new Random(seed);
        for (int i = 0; i < f.Length; i++) f[i] += (float)(Gaussian(rng) * sd);
    }

    private static ushort[] ToU16(float[] f) {
        var u = new ushort[f.Length];
        for (int i = 0; i < f.Length; i++) u[i] = (ushort)Math.Clamp((int)Math.Round(f[i]), 0, 65535);
        return u;
    }

    // FWHM at a KNOWN star centre via background-subtracted second moments —
    // deterministic, independent of star detection (which we don't need here
    // since we planted the stars ourselves).
    private static double MeasureFwhm(float[] img, int w, int x0, int y0, int r, double bg) {
        double sum = 0, cx = 0, cy = 0;
        for (int y = -r; y <= r; y++)
            for (int x = -r; x <= r; x++) {
                double v = img[(y0 + y) * w + (x0 + x)] - bg; if (v < 0) v = 0;
                sum += v; cx += v * x; cy += v * y;
            }
        if (sum <= 0) return double.NaN;
        cx /= sum; cy /= sum;
        double mxx = 0, myy = 0;
        for (int y = -r; y <= r; y++)
            for (int x = -r; x <= r; x++) {
                double v = img[(y0 + y) * w + (x0 + x)] - bg; if (v < 0) v = 0;
                mxx += v * (x - cx) * (x - cx); myy += v * (y - cy) * (y - cy);
            }
        return 2.3548200450309493 * Math.Sqrt((mxx / sum + myy / sum) / 2.0);
    }

    // ── recovers sharpness: blurred FWHM shrinks back toward the truth ──────
    [Test]
    public void RecoversSharpnessFromKnownBlur() {
        const int W = 440, H = 440;
        var sharp = MakeSharpField(W, H, sigma: 1.0, amp: 40000, noiseSd: 0);
        var psf = PsfModel.Gaussian(21, 2.0);                 // the known blur
        var blurred = Convolve(sharp, W, H, psf);
        AddNoise(blurred, 8, seed: 1);                        // sensor noise after the PSF

        var rl = new RichardsonLucyDeconvolution { Iterations = 25, TvLambda = 0.001 };
        var deconv = rl.Deconvolve(blurred, W, H, psf);

        // Measure a planted star (grid: margin 40, step 50 -> one at 240,240).
        const int sx = 240, sy = 240, win = 10;
        double before = MeasureFwhm(blurred, W, sx, sy, win, 800);
        double after = MeasureFwhm(deconv, W, sx, sy, win, 800);
        TestContext.WriteLine($"FWHM before={before:F2}  after={after:F2}  " +
                              $"(blur σ_eff≈2.24 → FWHM 5.26; sharp σ=1.0 → FWHM 2.35)");
        Assert.That(after, Is.LessThan(before * 0.85),
            "deconvolution should measurably sharpen the star");
        Assert.That(after, Is.GreaterThan(2.0), "but not collapse below the true sharp width");
    }

    // ── FFT convolution path matches the spatial path in the interior ───────
    [Test]
    public void FftMatchesSpatial() {
        const int W = 256, H = 256;
        var sharp = MakeSharpField(W, H, 1.0, 30000, seed: 4);
        var psf = PsfModel.Gaussian(21, 2.0);
        var blurred = Convolve(sharp, W, H, psf);
        AddNoise(blurred, 8, seed: 2);

        var spatial = new RichardsonLucyDeconvolution {
            Iterations = 15, TvLambda = 0.001, UseFft = false
        }.Deconvolve((float[])blurred.Clone(), W, H, psf);
        var fft = new RichardsonLucyDeconvolution {
            Iterations = 15, TvLambda = 0.001, UseFft = true
        }.Deconvolve((float[])blurred.Clone(), W, H, psf);

        // Compare the interior (avoid the few-px border where padding differs).
        double maxRel = 0, refMax = 0;
        for (int y = 24; y < H - 24; y++)
            for (int x = 24; x < W - 24; x++) {
                int i = y * W + x;
                refMax = Math.Max(refMax, Math.Abs(spatial[i]));
                maxRel = Math.Max(maxRel, Math.Abs(spatial[i] - fft[i]));
            }
        double rel = maxRel / Math.Max(1, refMax);
        TestContext.WriteLine($"max interior |spatial-fft| = {maxRel:F2} ({100 * rel:F2}% of peak)");
        Assert.That(rel, Is.LessThan(0.02), "FFT path must match the spatial path");
    }

    // ── photometric safety: total flux preserved ────────────────────────────
    [Test]
    public void ConservesFlux() {
        const int W = 300, H = 300;
        var sharp = MakeSharpField(W, H, 1.2, 30000, seed: 5);
        var psf = PsfModel.Gaussian(19, 1.8);
        var blurred = Convolve(sharp, W, H, psf);

        var rl = new RichardsonLucyDeconvolution { Iterations = 20, ConserveFlux = true };
        var deconv = rl.Deconvolve(blurred, W, H, psf);

        double sin = blurred.Select(v => (double)v).Sum();
        double sout = deconv.Select(v => (double)v).Sum();
        Assert.That(sout, Is.EqualTo(sin).Within(0.01 * sin), "flux must be conserved (≤1%)");
        Assert.That(deconv.All(v => float.IsFinite(v) && v >= 0), "no NaN/negatives");
    }

    // ── delta PSF is the identity (sanity) ──────────────────────────────────
    [Test]
    public void DeltaPsfReturnsInput() {
        const int W = 64, H = 64;
        var img = MakeSharpField(W, H, 1.5, 20000, seed: 3);
        var f = img.Select(v => (float)v).ToArray();
        var delta = new PsfModel(1, new float[] { 1f });
        var rl = new RichardsonLucyDeconvolution { Iterations = 10, TvLambda = 0, ConserveFlux = false };
        var outp = rl.Deconvolve(f, W, H, delta);
        for (int i = 0; i < f.Length; i++)
            Assert.That(outp[i], Is.EqualTo(Math.Max(0, f[i])).Within(1e-2));
    }

    [Test]
    public void ZeroIterationsIsNoOp() {
        const int W = 32, H = 32;
        var f = MakeSharpField(W, H, 1.5, 10000, seed: 8).Select(v => (float)v).ToArray();
        var rl = new RichardsonLucyDeconvolution { Iterations = 0 };
        var outp = rl.Deconvolve(f, W, H, PsfModel.Gaussian(15, 2.0));
        Assert.That(outp, Is.EqualTo(f));
    }

    // ── support mask = 0 keeps the original everywhere ──────────────────────
    [Test]
    public void ZeroSupportMaskKeepsOriginal() {
        const int W = 80, H = 80;
        var f = MakeSharpField(W, H, 1.2, 20000, seed: 11).Select(v => (float)v).ToArray();
        var psf = PsfModel.Gaussian(17, 2.0);
        var mask = new float[f.Length];   // all zeros = protect everything
        var rl = new RichardsonLucyDeconvolution { Iterations = 10 };
        var outp = rl.Deconvolve(f, W, H, psf, mask);
        for (int i = 0; i < f.Length; i++)
            Assert.That(outp[i], Is.EqualTo(f[i]).Within(1e-3));
    }

    // ── optional: real frame — measured PSF then RL, FWHM before/after ──────
    [Test, Explicit("Requires local polaris-ai/data/own/raw/originals FITS")]
    public void RealFrame_Sharpens() {
        string dir = FindOriginalsDir();
        if (dir == null) Assert.Ignore("originals folder not found");
        var fits = Directory.GetFiles(dir!, "*.fit").OrderBy(f => f).FirstOrDefault();
        if (fits == null) Assert.Ignore("no FITS in originals");

        using var fs = File.OpenRead(fits!);
        var img = FITSReader.Read(fs);
        int w = img.Properties.Width, h = img.Properties.Height, ch = img.Properties.Channels;

        // central crop keeps the test fast (CPU RL on a full frame is slow).
        const int C = 600;
        int x0 = Math.Max(0, (w - C) / 2), y0 = Math.Max(0, (h - C) / 2);
        int cw = Math.Min(C, w), chh = Math.Min(C, h);
        var crop = new ushort[cw * chh]; int plane = w * h;
        for (int y = 0; y < chh; y++)
            for (int x = 0; x < cw; x++) {
                int si = (y0 + y) * w + (x0 + x);
                crop[y * cw + x] = ch == 3
                    ? (ushort)((img.Data[si] + img.Data[plane + si] + img.Data[2 * plane + si]) / 3)
                    : img.Data[si];
            }

        var ex = new PsfExtractor();
        var psf = ex.Extract(crop, cw, chh);
        Assert.That(psf, Is.Not.Null, "need a measured PSF");
        var before = psf!.FwhmPx;

        var cf = new float[crop.Length];
        for (int i = 0; i < crop.Length; i++) cf[i] = crop[i];
        var rl = new RichardsonLucyDeconvolution { Iterations = 12, TvLambda = 0.002 };
        var deconv = rl.Deconvolve(cf, cw, chh, psf);

        var du = new ushort[deconv.Length];
        for (int i = 0; i < deconv.Length; i++) du[i] = (ushort)Math.Clamp((int)deconv[i], 0, 65535);
        var after = ex.Extract(du, cw, chh);
        Assert.That(after, Is.Not.Null);
        TestContext.WriteLine($"{Path.GetFileName(fits)}: FWHM {before:F2} → {after!.FwhmPx:F2} px " +
                              $"({100 * (1 - after.FwhmPx / before):F0}% tighter)");
        Assert.That(after.FwhmPx, Is.LessThan(before), "real stars should get tighter");
    }

    private static string FindOriginalsDir() {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int up = 0; up < 8 && d != null; up++, d = d.Parent) {
            string c = Path.Combine(d.FullName, "polaris-ai", "data", "own", "raw", "originals");
            if (Directory.Exists(c)) return c;
        }
        return null;
    }
}
