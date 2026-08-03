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
using System.Linq;
using NINA.Image.ImageAnalysis;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class FieldDeconvolutionTests {

    // Star field whose PSF WIDTH grows toward the corners (simulates field
    // curvature / coma): each planted star is rendered with a local sigma.
    private static ushort[] VaryingField(int w, int h, double sigCenter, double sigCorner,
                                         double amp, int seed = 73, double bg = 800, double noise = 8) {
        var rng = new Random(seed);
        var img = new double[w * h];
        for (int i = 0; i < img.Length; i++) img[i] = bg + Gaussian(rng) * noise;
        double cx0 = w / 2.0, cy0 = h / 2.0, norm = Math.Sqrt(cx0 * cx0 + cy0 * cy0);
        for (int cy = 40; cy <= h - 40; cy += 40)
            for (int cx = 40; cx <= w - 40; cx += 40) {
                double d = Math.Sqrt((cx - cx0) * (cx - cx0) + (cy - cy0) * (cy - cy0)) / norm;
                double s = sigCenter + (sigCorner - sigCenter) * d;
                int r = (int)Math.Ceiling(4 * s);
                for (int y = -r; y <= r; y++)
                    for (int x = -r; x <= r; x++) {
                        int ix = cx + x, iy = cy + y;
                        if (ix < 0 || iy < 0 || ix >= w || iy >= h) continue;
                        img[iy * w + ix] += amp * Math.Exp(-(x * x + y * y) / (2 * s * s));
                    }
            }
        var u = new ushort[w * h];
        for (int i = 0; i < u.Length; i++) u[i] = (ushort)Math.Clamp((int)Math.Round(img[i]), 0, 65535);
        return u;
    }

    private static double Gaussian(Random r) {
        double u1 = 1.0 - r.NextDouble(), u2 = 1.0 - r.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static double Fwhm(ushort[] img, int w, int x0, int y0, int r, double bg) {
        double sum = 0, mxx = 0, myy = 0, cx = 0, cy = 0;
        for (int y = -r; y <= r; y++) for (int x = -r; x <= r; x++) { double v = img[(y0 + y) * w + (x0 + x)] - bg; if (v < 0) v = 0; sum += v; cx += v * x; cy += v * y; }
        if (sum <= 0) return double.NaN; cx /= sum; cy /= sum;
        for (int y = -r; y <= r; y++) for (int x = -r; x <= r; x++) { double v = img[(y0 + y) * w + (x0 + x)] - bg; if (v < 0) v = 0; mxx += v * (x - cx) * (x - cx); myy += v * (y - cy) * (y - cy); }
        return 2.3548200450309493 * Math.Sqrt((mxx / sum + myy / sum) / 2.0);
    }

    // Half-max FWHM from a radial profile around (x0,y0): core-focused, so it
    // isn't inflated by deconvolution ringing in the wings the way the
    // second-moment FWHM is. Returns 2·(radius where the profile crosses
    // half of peak-above-background).
    private static double HalfMaxFwhm(ushort[] img, int w, int x0, int y0, int r, double bg) {
        double peak = 0;
        for (int y = -2; y <= 2; y++) for (int x = -2; x <= 2; x++)
            peak = Math.Max(peak, img[(y0 + y) * w + (x0 + x)] - bg);
        if (peak <= 0) return double.NaN;
        double half = peak * 0.5;
        // average crossing radius over 0/45/90/... rays
        int rays = 16; double acc = 0; int cnt = 0;
        for (int k = 0; k < rays; k++) {
            double ang = 2 * Math.PI * k / rays, dx = Math.Cos(ang), dy = Math.Sin(ang);
            double prev = peak;
            for (double t = 0.5; t <= r; t += 0.5) {
                int ix = (int)Math.Round(x0 + dx * t), iy = (int)Math.Round(y0 + dy * t);
                double v = img[iy * w + ix] - bg;
                if (v <= half) { // linear interp between prev (>half) and v
                    double frac = (prev - half) / Math.Max(1e-6, prev - v);
                    acc += (t - 0.5) + 0.5 * frac; cnt++; break;
                }
                prev = v;
            }
        }
        return cnt > 0 ? 2.0 * acc / cnt : double.NaN;
    }

    private static ushort[] U16(float[] f) {
        var u = new ushort[f.Length];
        for (int i = 0; i < f.Length; i++) u[i] = (ushort)Math.Clamp((int)Math.Round(f[i]), 0, 65535);
        return u;
    }

    // big stars in the corners exceed StarDetector's default MaxStarSize, so
    // give the extractor a permissive detector for this synthetic.
    private static PsfExtractor MakeExtractor() =>
        new PsfExtractor(new StarDetector { MaxStarSize = 4000, MaxStars = 4000 });

    [Test]
    public void FieldPsfWidensTowardCorners() {
        const int W = 480, H = 480;
        var u = VaryingField(W, H, 1.4, 3.0, 14000);
        var field = MakeExtractor().ExtractField(u, W, H, 3, 3);

        Assert.That(field, Is.Not.Null);
        Assert.That(field!.MeasuredCellCount, Is.GreaterThanOrEqualTo(7), "most cells should fit their own PSF");
        double center = field.Cell(1, 1).FwhmPx;
        double corner = field.Cell(0, 0).FwhmPx;
        TestContext.WriteLine($"cell FWHM center={center:F2} corner={corner:F2} measured={field.MeasuredCellCount}/9");
        Assert.That(corner, Is.GreaterThan(center * 1.25), "corner PSF must read wider than centre");
    }

    [Test]
    public void FieldDeconBeatsGlobalAtCorner() {
        const int W = 480, H = 480;
        var u = VaryingField(W, H, 1.3, 3.6, 14000);     // stronger gradient
        var ex = MakeExtractor();
        var field = ex.ExtractField(u, W, H, 4, 4);       // finer grid -> sharper corner cell
        var global = ex.Extract(u, W, H);
        Assert.That(field, Is.Not.Null);
        Assert.That(global, Is.Not.Null);

        var f = new float[u.Length];
        for (int i = 0; i < u.Length; i++) f[i] = u[i];

        var fieldOut = new FieldDeconvolution { Iterations = 24, TvLambda = 0.001 }
            .Deconvolve(f, W, H, field!);
        var globalOut = new RichardsonLucyDeconvolution { Iterations = 24, TvLambda = 0.001 }
            .Deconvolve(f, W, H, global!);

        // a planted corner star (grid step 40 -> one at 440,440)
        const int sx = 440, sy = 440, win = 14;
        double blurred = HalfMaxFwhm(u, W, sx, sy, win, 800);
        double fieldCorner = HalfMaxFwhm(U16(fieldOut), W, sx, sy, win, 800);
        double globalCorner = HalfMaxFwhm(U16(globalOut), W, sx, sy, win, 800);

        TestContext.WriteLine($"corner half-max FWHM: blurred={blurred:F2}  global-RL={globalCorner:F2}  field-RL={fieldCorner:F2}");
        Assert.That(fieldCorner, Is.LessThan(blurred), "field RL should sharpen the corner");
        Assert.That(fieldCorner, Is.LessThanOrEqualTo(globalCorner),
            "field RL (local PSF) should be at least as tight as global-PSF RL at the corner");

        // flux conserved
        double si = u.Select(v => (double)v).Sum();
        double so = fieldOut.Select(v => (double)v).Sum();
        Assert.That(so, Is.EqualTo(si).Within(0.02 * si));
    }
}
