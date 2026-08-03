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
public class PsfExtractorTests {

    // ── synthetic field helpers ─────────────────────────────────────────────
    // Plants elliptical-Gaussian stars on a flat background + read noise, so
    // the "true" PSF is known and the extractor can be checked against it.
    private static ushort[] MakeField(int w, int h, double sigmaX, double sigmaY,
                                      double amplitude, out int starCount,
                                      int seed = 1234, double bg = 800, double noiseSd = 12) {
        var rng = new Random(seed);
        var img = new double[w * h];
        for (int i = 0; i < img.Length; i++)
            img[i] = bg + Gaussian(rng) * noiseSd;

        // grid of well-separated stars away from the border
        int margin = 40, step = 45, r = (int)Math.Ceiling(4 * Math.Max(sigmaX, sigmaY));
        starCount = 0;
        for (int cy = margin; cy < h - margin; cy += step) {
            for (int cx = margin; cx < w - margin; cx += step) {
                double jx = cx + (rng.NextDouble() - 0.5) * 0.8;
                double jy = cy + (rng.NextDouble() - 0.5) * 0.8;
                for (int y = -r; y <= r; y++)
                    for (int x = -r; x <= r; x++) {
                        int ix = (int)Math.Round(jx) + x, iy = (int)Math.Round(jy) + y;
                        if (ix < 0 || iy < 0 || ix >= w || iy >= h) continue;
                        double dx = (jx - Math.Round(jx)) - x, dy = (jy - Math.Round(jy)) - y;
                        img[iy * w + ix] += amplitude *
                            Math.Exp(-(dx * dx) / (2 * sigmaX * sigmaX)
                                     - (dy * dy) / (2 * sigmaY * sigmaY));
                    }
                starCount++;
            }
        }

        var outp = new ushort[w * h];
        for (int i = 0; i < img.Length; i++)
            outp[i] = (ushort)Math.Max(0, Math.Min(65535, Math.Round(img[i])));
        return outp;
    }

    private static double Gaussian(Random r) {
        double u1 = 1.0 - r.NextDouble(), u2 = 1.0 - r.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    // ── correctness: round PSF FWHM recovery ────────────────────────────────
    [Test]
    public void RecoversRoundGaussianFwhm() {
        const double sigma = 2.2;
        var data = MakeField(420, 420, sigma, sigma, 22000, out _);
        var psf = new PsfExtractor().Extract(data, 420, 420);

        Assert.That(psf, Is.Not.Null, "extractor should find enough probes");
        Assert.That(psf!.StarsUsed, Is.GreaterThanOrEqualTo(8));

        double sum = psf.Kernel.Sum();
        Assert.That(sum, Is.EqualTo(1.0).Within(1e-3), "kernel must conserve flux (Σ=1)");

        double trueFwhm = 2.3548200450309493 * sigma;   // 5.18 px
        Assert.That(psf.FwhmPx, Is.EqualTo(trueFwhm).Within(0.15 * trueFwhm),
            $"measured FWHM {psf.FwhmPx:F2} vs true {trueFwhm:F2}");
        Assert.That(psf.Eccentricity, Is.LessThan(0.2), "round star -> low eccentricity");
    }

    // ── correctness: elongated PSF -> ellipticity detected ──────────────────
    [Test]
    public void DetectsElongation() {
        var data = MakeField(420, 420, 3.0, 1.6, 22000, out _);
        var ex = new PsfExtractor { MaxEccentricity = 0.97 };   // accept elongated probes
        var psf = ex.Extract(data, 420, 420);

        Assert.That(psf, Is.Not.Null);
        Assert.That(psf!.SigmaMajorPx, Is.GreaterThan(psf.SigmaMinorPx));
        Assert.That(psf.Eccentricity, Is.GreaterThan(0.5),
            $"elongated star should read high ecc, got {psf.Eccentricity:F2}");
        // major axis is along x (sigmaX > sigmaY) -> orientation near 0 or ±π
        double ang = Math.Abs(psf.OrientationRad);
        Assert.That(Math.Min(ang, Math.Abs(ang - Math.PI)), Is.LessThan(0.35));
    }

    // ── contract: too few stars -> null (caller falls back to analytic) ─────
    [Test]
    public void ReturnsNullWhenTooFewStars() {
        var data = MakeField(150, 150, 2.0, 2.0, 22000, out int n, seed: 7);
        Assume.That(n, Is.LessThan(8), "this tiny field intentionally has <8 stars");
        var psf = new PsfExtractor().Extract(data, 150, 150);
        Assert.That(psf, Is.Null);
    }

    // ── contract: saturated cores are rejected ──────────────────────────────
    [Test]
    public void RejectsSaturatedStars() {
        // All stars saturated (peak pinned at full scale) -> no clean probes.
        var data = MakeField(420, 420, 2.2, 2.2, 90000 /*clips to 65535*/, out _);
        var psf = new PsfExtractor { SaturationFraction = 0.9 }.Extract(data, 420, 420);
        Assert.That(psf, Is.Null, "saturated cores must be excluded as PSF probes");
    }

    [Test]
    public void GaussianModelIsNormalized() {
        var g = PsfModel.Gaussian(21, 2.0);
        Assert.That(g.Kernel.Sum(), Is.EqualTo(1.0).Within(1e-5));
        Assert.That(g.FwhmPx, Is.EqualTo(2.3548200450309493 * 2.0).Within(1e-6));
    }

    // ── optional: real-frame smoke test (skips when the data isn't present) ──
    // Uses the operator's own linear RGB FITS under polaris-ai/data/own/raw/
    // originals (gitignored, never in CI). Extracts the PSF, logs the shape,
    // and dumps the kernel as a PGM next to the temp dir for eyeballing.
    [Test, Explicit("Requires local polaris-ai/data/own/raw/originals FITS")]
    public void RealFrame_MeasuresPsf() {
        string? dir = FindOriginalsDir();
        if (dir == null) Assert.Ignore("originals folder not found; skipping real-frame test");

        var fits = Directory.GetFiles(dir!, "*.fit")
            .Concat(Directory.GetFiles(dir!, "*.fits"))
            .Concat(Directory.GetFiles(dir!, "*.fts"))
            .OrderBy(f => f).FirstOrDefault();
        if (fits == null) Assert.Ignore("no FITS in originals; skipping");

        TestContext.WriteLine($"PSF from: {Path.GetFileName(fits)}");
        using var fs = File.OpenRead(fits!);
        var img = FITSReader.Read(fs);
        int w = img.Properties.Width, h = img.Properties.Height, ch = img.Properties.Channels;
        ushort[] lum = ToLuminance(img.Data, w, h, ch);

        var psf = new PsfExtractor().Extract(lum, w, h);
        Assert.That(psf, Is.Not.Null, "should measure a PSF from a real frame");
        TestContext.WriteLine(
            $"FWHM={psf!.FwhmPx:F2}px  ecc={psf.Eccentricity:F3}  " +
            $"σmaj={psf.SigmaMajorPx:F2} σmin={psf.SigmaMinorPx:F2}  " +
            $"angle={psf.OrientationRad * 180 / Math.PI:F1}°  stars={psf.StarsUsed}  kernel={psf.Size}²");

        Assert.That(psf.FwhmPx, Is.InRange(0.8, 25.0), "FWHM should be physically plausible");
        Assert.That(psf.Kernel.Sum(), Is.EqualTo(1.0).Within(1e-3));

        string outPgm = Path.Combine(Path.GetTempPath(),
            Path.GetFileNameWithoutExtension(fits) + "_psf.pgm");
        WriteKernelPgm(psf, outPgm);
        TestContext.WriteLine($"PSF kernel written: {outPgm}");
    }

    private static ushort[] ToLuminance(ushort[] data, int w, int h, int channels) {
        int plane = w * h;
        if (channels != 3) return data.Length == plane ? data : data[..plane];
        var lum = new ushort[plane];
        for (int i = 0; i < plane; i++)
            lum[i] = (ushort)((data[i] + data[plane + i] + data[2 * plane + i]) / 3);
        return lum;
    }

    private static string? FindOriginalsDir() {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int up = 0; up < 8 && d != null; up++, d = d.Parent) {
            string cand = Path.Combine(d.FullName, "polaris-ai", "data", "own", "raw", "originals");
            if (Directory.Exists(cand)) return cand;
        }
        return null;
    }

    private static void WriteKernelPgm(PsfModel psf, string path) {
        int n = psf.Size;
        float min = psf.Kernel.Min(), max = psf.Kernel.Max();
        float range = max - min > 1e-12f ? max - min : 1f;
        var px = new byte[n * n];
        for (int i = 0; i < px.Length; i++)
            px[i] = (byte)Math.Clamp((int)((psf.Kernel[i] - min) / range * 255 + 0.5), 0, 255);
        using var s = File.Create(path);
        var hdr = System.Text.Encoding.ASCII.GetBytes($"P5\n{n} {n}\n255\n");
        s.Write(hdr, 0, hdr.Length);
        s.Write(px, 0, px.Length);
    }
}
