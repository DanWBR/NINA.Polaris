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

using Microsoft.Extensions.Logging.Abstractions;
using NINA.Core.Enum;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Studio;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// MEMOPT: the live stacker reuses session scratch buffers for the
/// per-frame transients (debayer planes, warped planes, calibrated
/// frame, SNR reconstruction) instead of allocating fresh LOH arrays
/// every frame. These tests pin two things:
///
/// 1. The destination-buffer overloads are numerically IDENTICAL to
///    the legacy allocating overloads — including when the destination
///    is dirty from a previous frame (the resampler must zero
///    off-canvas pixels explicitly).
/// 2. The per-frame managed allocation of a colour stacking session
///    actually dropped (regression guard so a future change doesn't
///    quietly reintroduce the ~150 MB/frame churn that ballooned RSS
///    to ~1 GB on a 9 MP OSC camera).
/// </summary>
[TestFixture]
[NonParallelizable]   // allocation measurements are process-wide
public class MemoryOptimizationTests {

    // ---- component equivalence: dest overload == allocating overload ----

    [Test]
    public void BayerDebayer_DestOverload_MatchesAllocatingOverload() {
        const int W = 97, H = 61;   // odd sizes exercise the border averages
        var rng = new Random(42);
        var cfa = new ushort[W * H];
        for (int i = 0; i < cfa.Length; i++) cfa[i] = (ushort)rng.Next(0, 65536);

        var expected = BayerDebayer.Bilinear(cfa, W, H, BayerPatternEnum.RGGB);

        // Dirty destinations: every cell must be overwritten.
        var r = new ushort[W * H]; var g = new ushort[W * H]; var b = new ushort[W * H];
        Array.Fill(r, (ushort)0xBEEF); Array.Fill(g, (ushort)0xBEEF); Array.Fill(b, (ushort)0xBEEF);
        var actual = BayerDebayer.Bilinear(cfa, W, H, BayerPatternEnum.RGGB, r, g, b);

        Assert.That(actual.R, Is.EqualTo(expected.R));
        Assert.That(actual.G, Is.EqualTo(expected.G));
        Assert.That(actual.B, Is.EqualTo(expected.B));
    }

    [Test]
    public void ImageResampler_DestOverload_MatchesAllocatingOverload_AndZeroesOffCanvas() {
        const int W = 120, H = 90;
        var rng = new Random(7);
        var src = new ushort[W * H];
        for (int i = 0; i < src.Length; i++) src[i] = (ushort)rng.Next(0, 65536);

        // Small rotation + shift: pushes a border strip off-canvas, so a
        // dirty reused destination would leak stale pixels there if the
        // overload forgot to zero them.
        var t = new AffineTransform {
            M00 = 0.999, M01 = -0.04, M10 = 0.04, M11 = 0.999, Tx = 6.3, Ty = -4.1
        };

        var expected = ImageResampler.ApplyTransform(src, W, H, t);

        var dest = new ushort[W * H];
        Array.Fill(dest, (ushort)0xFFFF);   // worst-case dirty scratch
        var actual = ImageResampler.ApplyTransform(src, W, H, t, dest);

        Assert.That(ReferenceEquals(actual, dest), Is.True,
            "Non-degenerate transform must fill and return the destination.");
        Assert.That(actual, Is.EqualTo(expected),
            "Dest overload must be bit-identical, including zeroed off-canvas pixels.");
    }

    [Test]
    public void ImageResampler_DegenerateTransform_ReturnsSourceNotDest() {
        var src = new ushort[16];
        var dest = new ushort[16];
        var degenerate = new AffineTransform { M00 = 0, M01 = 0, M10 = 0, M11 = 0 };
        var result = ImageResampler.ApplyTransform(src, 4, 4, degenerate, dest);
        Assert.That(ReferenceEquals(result, src), Is.True,
            "Degenerate transforms return the source unchanged; callers must use the return value.");
    }

    [Test]
    public void CalibratePixels_DestOverload_MatchesAllocatingOverload() {
        var rng = new Random(11);
        int n = 4096;
        var light = new ushort[n];
        var dark = new ushort[n];
        var norm = new float[n];
        for (int i = 0; i < n; i++) {
            light[i] = (ushort)rng.Next(0, 65536);
            dark[i] = (ushort)rng.Next(0, 500);
            norm[i] = (float)(0.5 + rng.NextDouble());
        }

        var expected = CalibrationMath.CalibratePixels(light, dark, bias: null, flat: (norm, 1.0));

        var dest = new ushort[n];
        Array.Fill(dest, (ushort)0xBEEF);
        var actual = CalibrationMath.CalibratePixels(light, dark, bias: null, flat: (norm, 1.0), dest: dest);

        Assert.That(ReferenceEquals(actual, dest), Is.True);
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void CalibratePixels_DestAliasingLight_FallsBackToFreshArray() {
        var light = new ushort[64];
        Array.Fill(light, (ushort)100);
        var dark = new ushort[64];
        Array.Fill(dark, (ushort)30);
        var result = CalibrationMath.CalibratePixels(light, dark, bias: null, flat: null, dest: light);
        Assert.That(ReferenceEquals(result, light), Is.False,
            "The input must never be mutated; an aliasing dest is ignored.");
        Assert.That(light[0], Is.EqualTo(100), "Input untouched.");
        Assert.That(result[0], Is.EqualTo(70));
    }

    // ---- colour session end-to-end -------------------------------------

    private const int W = 300, H = 300, Bg = 100;

    private static readonly (double x, double y)[] Stars = {
        (60, 70), (220, 80), (120, 210), (200, 160),
        (90, 185), (250, 250), (150, 120), (45, 245),
    };

    private static LiveStackingService MakeColourService() {
        var relay = new ImageRelayService(NullLogger<ImageRelayService>.Instance);
        return new LiveStackingService(relay, NullLogger<LiveStackingService>.Instance) {
            ColorStacking = true
        };
    }

    private static BaseImageData MakeOscFrame((double x, double y)[] stars, int w = W, int h = H) {
        var data = new ushort[w * h];
        for (int i = 0; i < data.Length; i++) data[i] = Bg;
        foreach (var (cx, cy) in stars) PlantStar(data, cx, cy, w, h, sigma: 2.0, amp: 5000);
        var props = new ImageProperties {
            Width = w, Height = h, BitDepth = 16, Channels = 1,
            IsBayered = true, BayerPattern = BayerPatternEnum.RGGB
        };
        return new BaseImageData(data, props);
    }

    private static void PlantStar(ushort[] d, double cx, double cy, int w, int h, double sigma, double amp) {
        int rad = (int)Math.Ceiling(3.5 * sigma);
        for (int y = (int)cy - rad; y <= cy + rad; y++) {
            if (y < 0 || y >= h) continue;
            for (int x = (int)cx - rad; x <= cx + rad; x++) {
                if (x < 0 || x >= w) continue;
                double dx = x - cx, dy = y - cy;
                double g = amp * Math.Exp(-0.5 * (dx * dx + dy * dy) / (sigma * sigma));
                int v = d[y * w + x] + (int)g;
                d[y * w + x] = (ushort)Math.Min(65535, v);
            }
        }
    }

    [Test]
    public async Task ColourSession_StacksDriftedFramesIntoScratch_AndSkipsMonoBuffer() {
        var svc = MakeColourService();
        svc.Start();

        await svc.AddFrameAsync(MakeOscFrame(Stars));
        // Drifted frames exercise the warp-into-scratch path.
        var drifted1 = Stars.Select(s => (s.x + 4, s.y + 3)).ToArray();
        var drifted2 = Stars.Select(s => (s.x - 3, s.y + 5)).ToArray();
        await svc.AddFrameAsync(MakeOscFrame(drifted1));
        await svc.AddFrameAsync(MakeOscFrame(drifted2));

        Assert.That(svc.FrameCount, Is.EqualTo(3));
        Assert.That(svc.ColorActive, Is.True);
        // MEMOPT: the mono accumulator must NOT be allocated in colour mode.
        Assert.That(svc.GetStackedResult(), Is.Empty,
            "Colour sessions never write the mono accumulator; it should stay null.");

        // All three frames must have landed on the reference grid: the
        // green plane (direct samples on RGGB) stays bright at the
        // reference star positions under the running mean.
        var rgb = svc.GetStackedResultRgb();
        Assert.That(rgb.Length, Is.EqualTo(W * H * 3));
        int n = W * H;
        foreach (var (x, y) in Stars) {
            int idx = (int)y * W + (int)x;
            Assert.That((int)rgb[n + idx], Is.GreaterThan(2000),
                $"Green plane should stay bright at reference star ({x},{y}); got {rgb[n + idx]}.");
        }

        // MEMOPT also fixed the colour cumulative SNR (it used to read the
        // never-written mono buffer and report the SNR of a zero image).
        Assert.That(svc.CumulativeSnr, Is.GreaterThan(0),
            "Colour sessions must report a real cumulative SNR from the luminance reconstruction.");
    }

    // ---- allocation regression guard ------------------------------------

    [Test]
    public async Task ColourSession_PerFrameAllocations_StayBounded() {
        // 1024x1024 OSC frames. Legacy per-frame churn at this size was
        // ~20 MB (debayer 3x2 MB + warp 3x2 MB + visited 1 MB + stacked
        // RGB 6 MB + detector/statistics transients); with the session
        // scratch it drops to roughly the stacked-RGB output + JPEG
        // render + detector transients. The 14 MB bound sits between the
        // two regimes with headroom for runtime noise — if scratch reuse
        // regresses this climbs right back past 20 MB and fails.
        const int Size = 1024;
        var stars = Stars.Select(s => (s.x * Size / (double)W, s.y * Size / (double)H)).ToArray();

        var svc = MakeColourService();
        svc.Start();

        // Warm-up: reference frame + one aligned frame (allocates the
        // accumulators + all scratch buffers).
        await svc.AddFrameAsync(MakeOscFrame(stars, Size, Size));
        await svc.AddFrameAsync(MakeOscFrame(stars.Select(s => (s.Item1 + 2, s.Item2 + 1)).ToArray(), Size, Size));

        const int Measured = 4;
        long before = GC.GetTotalAllocatedBytes(precise: true);
        for (int i = 0; i < Measured; i++) {
            var drifted = stars.Select(s => (s.Item1 + 3 + i, s.Item2 - 2 + i)).ToArray();
            // Frame built OUTSIDE the measurement? No — building it is 2 MB
            // and part of real per-frame cost; keep it inside for realism.
            await svc.AddFrameAsync(MakeOscFrame(drifted, Size, Size));
        }
        long after = GC.GetTotalAllocatedBytes(precise: true);

        Assert.That(svc.FrameCount, Is.EqualTo(2 + Measured),
            "All measured frames must have aligned + integrated (a rejected frame skips the hot path and voids the measurement).");

        double perFrameMb = (after - before) / (double)Measured / (1024.0 * 1024.0);
        TestContext.Out.WriteLine($"Per-frame managed allocation: {perFrameMb:F1} MB");
        Assert.That(perFrameMb, Is.LessThan(14.0),
            $"Colour stacking allocated {perFrameMb:F1} MB/frame — scratch reuse regressed " +
            "(legacy per-frame churn at this size was ~20 MB).");
    }
}
