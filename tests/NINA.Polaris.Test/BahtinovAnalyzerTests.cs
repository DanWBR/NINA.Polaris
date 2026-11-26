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

using NUnit.Framework;
using NINA.Polaris.Services.Focus;

namespace NINA.Polaris.Test;

/// <summary>
/// Synthetic-frame tests for the Bahtinov analyser. We bake a 3-spike
/// pattern into a small ushort[] frame and assert the analyser
/// recovers the spike geometry, finds the right "centre" spike, and
/// produces a focus offset of roughly the magnitude we injected.
/// </summary>
[TestFixture]
public class BahtinovAnalyzerTests {

    private const int W = 400;
    private const int H = 400;
    private const ushort BackgroundLevel = 100;
    private const ushort SpikeIntensity = 8000;

    [Test]
    public void FindTopPeaks_ThreeClearPeaks_PicksAll() {
        var signal = new double[360];
        // Three peaks at 30°, 90°, 150° (steps of 60°), each a tight
        // gaussian-ish bump.
        foreach (var centre in new[] { 60, 180, 300 }) {     // indices for 0.5° step
            for (int i = -3; i <= 3; i++) {
                signal[(centre + i + 360) % 360] += Math.Exp(-i * i / 4.0);
            }
        }
        var peaks = BahtinovAnalyzer.FindTopPeaks(signal, 0.5, 30.0, 3);
        Assert.That(peaks.Count, Is.EqualTo(3));
        peaks.Sort();
        Assert.That(peaks[0], Is.EqualTo(30).Within(2));
        Assert.That(peaks[1], Is.EqualTo(90).Within(2));
        Assert.That(peaks[2], Is.EqualTo(150).Within(2));
    }

    [Test]
    public void FindTopPeaks_PeaksTooClose_RejectsClumps() {
        var signal = new double[360];
        // Peak at 90° and a competing peak only 10° away. With a
        // 30° minimum separation only the stronger one survives.
        signal[180] = 10;   // 90°
        signal[200] = 8;    // 100°
        signal[80] = 6;     // 40°
        signal[280] = 5;    // 140°
        var peaks = BahtinovAnalyzer.FindTopPeaks(signal, 0.5, 30.0, 3);
        Assert.That(peaks.Count, Is.EqualTo(3));
        // 90° must be in, 100° must NOT be (too close).
        Assert.That(peaks, Has.Some.EqualTo(90).Within(0.5));
        Assert.That(peaks, Has.None.EqualTo(100).Within(0.5));
    }

    [Test]
    public void SolveIntersection_ParallelLines_ReturnsNull() {
        // Two lines at the SAME angle = parallel = no unique intersection
        var a = new BahtinovSpike(45, 0, 1);
        var b = new BahtinovSpike(45, 5, 1);
        Assert.That(BahtinovAnalyzer.SolveIntersection(a, b, 100, 100), Is.Null);
    }

    [Test]
    public void SolveIntersection_PerpendicularLinesThroughOrigin_AtOrigin() {
        // Horizontal + vertical through (100, 100) = intersect at (100, 100)
        var a = new BahtinovSpike(0,   0, 1);   // horizontal
        var b = new BahtinovSpike(90,  0, 1);   // vertical
        var ix = BahtinovAnalyzer.SolveIntersection(a, b, 100, 100);
        Assert.That(ix, Is.Not.Null);
        Assert.That(ix!.Value.X, Is.EqualTo(100).Within(1e-6));
        Assert.That(ix!.Value.Y, Is.EqualTo(100).Within(1e-6));
    }

    [Test]
    public void Analyze_EmptyFrame_FailsGracefully() {
        var pixels = new ushort[W * H];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = BackgroundLevel;
        var r = BahtinovAnalyzer.Analyze(pixels, W, H);
        Assert.That(r.Ok, Is.False);
        Assert.That(r.Error, Does.Contain("stars").Or.Contain("ROI"));
    }

    [Test]
    public void Analyze_InFocusPattern_ReturnsLowOffset() {
        // In-focus = 3 spikes that all cross the star centre. Bake
        // them at 60°, 90°, 120° (V-with-bisector geometry).
        var pixels = MakeBahtinovFrame(W, H, starX: 200, starY: 200,
                                       outerAngle1Deg: 60, centralAngleDeg: 90,
                                       outerAngle2Deg: 120,
                                       centralRho: 0);
        var r = BahtinovAnalyzer.Analyze(pixels, W, H, starX: 200, starY: 200);
        Assert.That(r.Ok, Is.True, r.Error);
        // Spikes should be at roughly the angles we baked.
        var angles = new[] { r.Spike1Angle, r.Spike2Angle, r.Spike3Angle }
            .OrderBy(a => a).ToArray();
        Assert.That(angles[0], Is.EqualTo(60).Within(3));
        Assert.That(angles[1], Is.EqualTo(90).Within(3));
        Assert.That(angles[2], Is.EqualTo(120).Within(3));
        // Offset should be small (in focus).
        Assert.That(Math.Abs(r.OffsetPx), Is.LessThan(2.5),
            $"offset {r.OffsetPx:F2} too large for in-focus pattern");
    }

    [Test]
    public void Analyze_DefocusedPattern_ReturnsLargeOffset() {
        // Same V at 60°/120°, central spike at 90° but shifted 6 px
        // perpendicular = out of focus.
        var pixels = MakeBahtinovFrame(W, H, starX: 200, starY: 200,
                                       outerAngle1Deg: 60, centralAngleDeg: 90,
                                       outerAngle2Deg: 120,
                                       centralRho: 6);
        var r = BahtinovAnalyzer.Analyze(pixels, W, H, starX: 200, starY: 200);
        Assert.That(r.Ok, Is.True, r.Error);
        // Offset magnitude should be in the same ballpark as the
        // injected rho (allowing 1-2 px for sampling + peak refine).
        Assert.That(Math.Abs(r.OffsetPx), Is.GreaterThan(3.0),
            $"offset {r.OffsetPx:F2} too small for defocused pattern");
    }

    [Test]
    public void Analyze_TwentyDegreeMask_FindsOuterSpikes() {
        // Common masks put the outer spikes ~20° from the central one.
        // A 30° minimum peak gap rejected them and picked noise instead.
        var pixels = MakeBahtinovFrame(W, H, starX: 200, starY: 200,
                                       outerAngle1Deg: 70, centralAngleDeg: 90,
                                       outerAngle2Deg: 110,
                                       centralRho: 4);
        var r = BahtinovAnalyzer.Analyze(pixels, W, H, starX: 200, starY: 200);
        Assert.That(r.Ok, Is.True, r.Error);
        var angles = new[] { r.Spike1Angle, r.Spike2Angle, r.Spike3Angle }
            .OrderBy(a => a).ToArray();
        Assert.That(angles[0], Is.EqualTo(70).Within(1));
        Assert.That(angles[1], Is.EqualTo(90).Within(1));
        Assert.That(angles[2], Is.EqualTo(110).Within(1));
        Assert.That(Math.Abs(r.OffsetPx), Is.EqualTo(4).Within(1));
    }

    [Test]
    public void Analyze_OuterSpikesOffCentre_FoundByJointSearch() {
        // Out of focus, the spikes do not all cross the star centre.
        // Here the whole V is shifted 10 px, so no line through the
        // ROI centre lies on an outer spike.
        var px = new ushort[W * H];
        for (int i = 0; i < px.Length; i++) px[i] = BackgroundLevel;
        DrawSpike(px, W, H, 200, 200, 70, rho: 10);
        DrawSpike(px, W, H, 200, 200, 110, rho: -10);
        DrawSpike(px, W, H, 200, 200, 90, rho: 0);
        var r = BahtinovAnalyzer.Analyze(px, W, H, starX: 200, starY: 200);
        Assert.That(r.Ok, Is.True, r.Error);
        var angles = new[] { r.Spike1Angle, r.Spike2Angle, r.Spike3Angle }
            .OrderBy(a => a).ToArray();
        Assert.That(angles[0], Is.EqualTo(70).Within(1));
        Assert.That(angles[1], Is.EqualTo(90).Within(1));
        Assert.That(angles[2], Is.EqualTo(110).Within(1));
    }

    [Test]
    public void Median_LowBitCameraData_IsExact() {
        // 10-bit sensor stored in 16-bit: background ~64 ADU. The old
        // 256-count histogram returned 0 here, so nothing was removed.
        var data = new ushort[1001];
        for (int i = 0; i < data.Length; i++) data[i] = (ushort)(60 + i % 9);
        data[0] = 1023;
        Assert.That(BahtinovAnalyzer.Median(data), Is.EqualTo(64));
    }

    [Test]
    public void FindTopPeaks_ShoulderOfStrongPeak_NotPicked() {
        // A broad strong peak at 90° has a shoulder still above the
        // real weaker peaks; only local maxima may be picked.
        var signal = new double[360];
        for (int i = 0; i < 360; i++) signal[i] = 100 * Math.Exp(-Math.Pow((i - 180) / 30.0, 2));
        signal[140] = Math.Max(signal[140], 0) + 30;   // 70°
        signal[220] = Math.Max(signal[220], 0) + 30;   // 110°
        var peaks = BahtinovAnalyzer.FindTopPeaks(signal, 0.5, 12.0, 3);
        peaks.Sort();
        Assert.That(peaks, Is.EqualTo(new[] { 70.0, 90.0, 110.0 }));
    }

    [Test]
    public void Analyze_RealFrame_J37aMask_FindsSpikes() {
        // QHY5 (10-bit data, background ~66 ADU, core clipped at 1030)
        // behind a J-37A 135 mm with a 3D printed mask (outer slots
        // ±20°). Bright red light, slightly defocused. Spikes measured
        // by hand: ~70.5°, 90°, 110°.
        var px = LoadCrop("j37a_red_light_400x400.u16");
        var r = BahtinovAnalyzer.Analyze(px, 400, 400, starX: 200, starY: 200);
        Assert.That(r.Ok, Is.True, r.Error);
        var angles = new[] { r.Spike1Angle, r.Spike2Angle, r.Spike3Angle }
            .OrderBy(a => a).ToArray();
        TestContext.Out.WriteLine($"angles {string.Join(", ", angles)} offset {r.OffsetPx:F2}");
        Assert.That(angles[0], Is.EqualTo(70.5).Within(1.5));
        Assert.That(angles[1], Is.EqualTo(90).Within(1.5));
        Assert.That(angles[2], Is.EqualTo(110).Within(1.5));
        Assert.That(r.CentreSpikeIndex, Is.EqualTo(1));
    }

    private static ushort[] LoadCrop(string name) {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory,
                                "TestData", "Bahtinov", name);
        var bytes = File.ReadAllBytes(path);
        var px = new ushort[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, px, 0, px.Length * 2);
        return px;
    }

    // ─── helpers ──────────────────────────────────────────────────

    /// <summary>Paints a Bahtinov-like 3-spike pattern into a fresh
    /// frame. The two "outer" spikes pass through (starX, starY)
    /// exactly; the "central" spike is offset perpendicular to its
    /// direction by <paramref name="centralRho"/> px.</summary>
    private static ushort[] MakeBahtinovFrame(int w, int h, int starX, int starY,
                                                double outerAngle1Deg,
                                                double centralAngleDeg,
                                                double outerAngle2Deg,
                                                double centralRho) {
        var px = new ushort[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = BackgroundLevel;
        // Add a 1px bright core so StarDetector locks on. Bigger
        // cores break the rho refinement — at the centre of the
        // ROI the line integration always wins regardless of the
        // spike's perpendicular offset because it crosses the
        // bright core. Real Bahtinov masks don't show a separate
        // core; the diffraction spikes ARE the star's response.
        px[starY * w + starX] = 55000;
        DrawSpike(px, w, h, starX, starY, outerAngle1Deg, rho: 0);
        DrawSpike(px, w, h, starX, starY, outerAngle2Deg, rho: 0);
        DrawSpike(px, w, h, starX, starY, centralAngleDeg, rho: centralRho);
        return px;
    }

    private static void DrawSpike(ushort[] px, int w, int h,
                                   int cx, int cy, double angleDeg, double rho) {
        var theta = angleDeg * Math.PI / 180.0;
        var dx = Math.Cos(theta);
        var dy = Math.Sin(theta);
        var nx = -dy;
        var ny =  dx;
        var x0 = cx + rho * nx;
        var y0 = cy + rho * ny;
        for (int t = -80; t <= 80; t++) {
            for (int wn = -1; wn <= 1; wn++) {
                var x = (int)Math.Round(x0 + t * dx + wn * nx);
                var y = (int)Math.Round(y0 + t * dy + wn * ny);
                if (x < 0 || x >= w || y < 0 || y >= h) continue;
                // Triangular falloff perpendicular to spike line.
                var attenuation = wn == 0 ? 1.0 : 0.5;
                var v = (ushort)Math.Min(65535,
                    px[y * w + x] + (int)(SpikeIntensity * attenuation));
                px[y * w + x] = v;
            }
        }
    }
}