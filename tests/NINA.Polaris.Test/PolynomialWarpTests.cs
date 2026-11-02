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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

[TestFixture]
public class PolynomialWarpTests {

    /// <summary>A frame of the same sky: turned, shifted, and bent by a
    /// smooth quadratic (distortion moving through the field, differential
    /// refraction). Maps a reference position to where it lands in the frame.</summary>
    private static (double x, double y) Distort(double x, double y, int w, int h,
            double rotDeg, double bend) {
        double a = rotDeg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        double cx = w / 2.0, cy = h / 2.0, u = (x - cx) / cx, v = (y - cy) / cy;
        double rx = cx + c * (x - cx) - s * (y - cy) + 6;
        double ry = cy + s * (x - cx) + c * (y - cy) - 4;
        // Quadratic bend, `bend` px at the corners.
        return (rx + bend * u * v, ry + bend * 0.5 * (u * u - v * v));
    }

    private static List<DetectedStar> Field(int count, int w, int h, int seed) {
        var rng = new Random(seed);
        return Enumerable.Range(0, count).Select(i => new DetectedStar {
            X = 30 + rng.NextDouble() * (w - 60), Y = 30 + rng.NextDouble() * (h - 60),
            HFR = 2, Flux = 1e6 / (i + 1)
        }).ToList();
    }

    [Test]
    public void Fit_RecoversAKnownQuadratic() {
        int w = 4000, h = 3000;
        var refs = Field(80, w, h, 1).Select(s => (s.X, s.Y)).ToList();
        var cur = refs.Select(p => Distort(p.X, p.Y, w, h, 0.7, 6)).ToList();
        var warp = PolynomialWarp.Fit(refs, cur, w, h)!;
        Assert.That(warp.RmsResidual, Is.LessThan(1e-6));
        foreach (var (x, y) in new[] { (0.0, 0.0), (w, 0.0), (w, (double)h), (0.0, (double)h), (w / 2.0, h / 2.0) }) {
            var (ex, ey) = Distort(x, y, w, h, 0.7, 6);
            var (px, py) = warp.Apply(x, y);
            Assert.That(Math.Abs(px - ex) + Math.Abs(py - ey), Is.LessThan(1e-6));
        }
    }

    [Test]
    public void FromStars_BentField_ReturnsAWarpThatPinsTheCorners() {
        int w = 6000, h = 4000;
        var refs = Field(200, w, h, 2);
        var curs = refs.Select(r => {
            var (x, y) = Distort(r.X, r.Y, w, h, 1.2, 4);
            return new DetectedStar { X = x, Y = y, HFR = 2, Flux = r.Flux };
        }).ToList();
        var affine = StarMatcher.Match(refs, curs)!;
        var warp = PolynomialWarp.FromStars(refs, curs, affine, w, h);
        Assert.That(warp, Is.Not.Null);
        foreach (var (x, y) in new[] { (0.0, 0.0), (w - 1.0, 0.0), (w - 1.0, h - 1.0), (0.0, h - 1.0) }) {
            var (ex, ey) = Distort(x, y, w, h, 1.2, 4);
            var (px, py) = warp!.Apply(x, y);
            Assert.That(Math.Sqrt((px - ex) * (px - ex) + (py - ey) * (py - ey)), Is.LessThan(0.2), $"corner {x},{y}");
        }
    }

    [Test]
    public void FromStars_RigidField_KeepsTheAffine() {
        // No bend: the affine already explains the stars, so no quadratic.
        int w = 6000, h = 4000;
        var refs = Field(200, w, h, 3);
        var rng = new Random(9);
        var curs = refs.Select(r => {
            var (x, y) = Distort(r.X, r.Y, w, h, 0.4, 0);
            return new DetectedStar { X = x + (rng.NextDouble() - 0.5) * 0.3, Y = y + (rng.NextDouble() - 0.5) * 0.3, HFR = 2, Flux = r.Flux };
        }).ToList();
        var affine = StarMatcher.Match(refs, curs)!;
        Assert.That(PolynomialWarp.FromStars(refs, curs, affine, w, h), Is.Null);
    }

    [Test]
    public void FromStars_StarsInOneCorner_Refuses() {
        // A quadratic fitted on one corner would bend freely over the rest.
        int w = 6000, h = 4000;
        var refs = Field(200, w / 3, h / 3, 4);
        var curs = refs.Select(r => {
            var (x, y) = Distort(r.X, r.Y, w, h, 1.0, 8);
            return new DetectedStar { X = x, Y = y, HFR = 2, Flux = r.Flux };
        }).ToList();
        var affine = StarMatcher.Match(refs, curs)!;
        Assert.That(PolynomialWarp.FromStars(refs, curs, affine, w, h), Is.Null);
    }

    [Test]
    public void ApplyWarp_MatchesTheAffineResamplerForAShift() {
        int w = 64, h = 48;
        var src = new ushort[w * h];
        var rng = new Random(5);
        for (int i = 0; i < src.Length; i++) src[i] = (ushort)rng.Next(1000, 60000);
        // Output pixel (x, y) reads source (x - 2.5, y + 1.25).
        var outs = new List<(double, double)>();
        var srcs = new List<(double, double)>();
        for (int y = 0; y < h; y += 8) for (int x = 0; x < w; x += 8) { outs.Add((x, y)); srcs.Add((x - 2.5, y + 1.25)); }
        var warp = PolynomialWarp.Fit(outs, srcs, w, h)!;
        var a = ImageResampler.ApplyWarp(src, w, h, warp, new ushort[w * h]);
        var b = ImageResampler.ApplyTransform(src, w, h,
            new AffineTransform { M00 = 1, M11 = 1, Tx = 2.5, Ty = -1.25 }, new ushort[w * h]);
        for (int i = 0; i < a.Length; i++) Assert.That(Math.Abs(a[i] - b[i]), Is.LessThanOrEqualTo(1), $"pixel {i}");
    }

    // ---- through the live stacker ------------------------------------

    private const int SW = 900, SH = 600;

    private static BaseImageData Frame(IEnumerable<(double x, double y)> stars) {
        var px = new double[SW * SH];
        Array.Fill(px, 200.0);
        foreach (var (cx, cy) in stars)
            for (int y = (int)cy - 6; y <= (int)cy + 6; y++)
                for (int x = (int)cx - 6; x <= (int)cx + 6; x++) {
                    if (x < 0 || y < 0 || x >= SW || y >= SH) continue;
                    double d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                    px[y * SW + x] += 20000 * Math.Exp(-d2 / (2 * 1.3 * 1.3));
                }
        var data = px.Select(v => (ushort)Math.Min(65535, v)).ToArray();
        return new BaseImageData(data, new ImageProperties { Width = SW, Height = SH, BitDepth = 16 }, new ImageMetaData());
    }

    private static async Task<(ushort[] stack, int polyFrames)> StackTwo(bool polynomial,
            List<(double x, double y)> refStars) {
        var svc = new LiveStackingService(new ImageRelayService(NullLogger<ImageRelayService>.Instance),
            NullLogger<LiveStackingService>.Instance) { PolynomialAlignment = polynomial };
        svc.Start();
        await svc.AddFrameAsync(Frame(refStars));
        await svc.AddFrameAsync(Frame(refStars.Select(p => Distort(p.x, p.y, SW, SH, 1.0, 5))));
        Assert.That(svc.FrameCount, Is.EqualTo(2));
        return (svc.GetStackedResult(), svc.PolynomialFrames);
    }

    [Test]
    public async Task LiveStack_BentField_CornerStarsStayStacked() {
        var rng = new Random(12);
        var stars = Enumerable.Range(0, 120)
            .Select(_ => (x: 20 + rng.NextDouble() * (SW - 40), y: 20 + rng.NextDouble() * (SH - 40)))
            .ToList();
        var (withPoly, polyFrames) = await StackTwo(true, stars);
        var (affineOnly, none) = await StackTwo(false, stars);
        Assert.That(polyFrames, Is.EqualTo(1));
        Assert.That(none, Is.EqualTo(0));

        // At stars near the corners, both frames must land on the same spot:
        // the stacked peak stays near a single frame's peak instead of
        // splitting into two half-height smears.
        var corner = stars.Where(s => (s.x < 150 || s.x > SW - 150) && (s.y < 120 || s.y > SH - 120)
                                      && s.x > 30 && s.y > 30 && s.x < SW - 30 && s.y < SH - 30).ToList();
        Assert.That(corner.Count, Is.GreaterThan(3));
        double Peak(ushort[] img, (double x, double y) s) {
            double best = 0;
            for (int y = (int)s.y - 2; y <= (int)s.y + 2; y++)
                for (int x = (int)s.x - 2; x <= (int)s.x + 2; x++) best = Math.Max(best, img[y * SW + x]);
            return best;
        }
        double polyPeak = corner.Average(s => Peak(withPoly, s));
        double affinePeak = corner.Average(s => Peak(affineOnly, s));
        Assert.That(polyPeak, Is.GreaterThan(affinePeak * 1.1),
            $"corner peaks: quadratic {polyPeak:F0}, affine {affinePeak:F0}");
    }
}
