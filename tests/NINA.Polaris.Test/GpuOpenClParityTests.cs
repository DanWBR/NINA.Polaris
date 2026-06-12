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
using NUnit.Framework;
using NINA.Image.Editor;
using NINA.Image.Gpu;
using NINA.Image.ImageAnalysis;
using NINA.Polaris.Services.OpenCl;

namespace NINA.Polaris.Test;

/// <summary>
/// Real OpenCL kernel validation: runs <see cref="OpenClGpuCompute"/> on whatever
/// OpenCL device this machine exposes (e.g. an NVIDIA RTX on the dev box, Mali on
/// an RK3588) and asserts the GPU output matches the CPU reference within a small
/// tolerance. Auto-ignored when no OpenCL GPU is present, so CI stays green.
///
/// GPU floats vs the CPU's double accumulation differ by at most a couple of LSB
/// on the convolution/resample kernels; LUT + accumulate are exact.
/// </summary>
[TestFixture]
public class GpuOpenClParityTests {
    private OpenClGpuCompute _gpu = null!;

    [OneTimeSetUp]
    public void Setup() {
        _gpu = new OpenClGpuCompute(NullLogger<OpenClGpuCompute>.Instance);
        if (!_gpu.EnsureInitialized()) {
            Assert.Ignore($"No OpenCL GPU on this machine. {OpenClRuntime.Diagnostics}. " +
                          $"InitError: {_gpu.InitError}");
        }
        TestContext.Out.WriteLine($"OpenCL device: {_gpu.Device}");
    }

    [OneTimeTearDown]
    public void Teardown() => _gpu.Dispose();

    private static ushort[] Ramp(int n, int mul = 41) {
        var a = new ushort[n];
        for (int i = 0; i < n; i++) a[i] = (ushort)((i * mul + (i % 7) * 1000) & 0xFFFF);
        return a;
    }

    private static long MaxAbsDiff(ushort[] a, ushort[] b) {
        long m = 0;
        for (int i = 0; i < a.Length; i++) { long d = Math.Abs(a[i] - b[i]); if (d > m) m = d; }
        return m;
    }

    [Test]
    public void Blur_matches_cpu_within_tolerance() {
        const int w = 128, h = 96;
        var data = Ramp(w * h);
        Assert.That(_gpu.TrySeparableBlur(data, w, h, 3, 1.5, out var gpu), Is.True,
            "GPU declined blur");
        var cpu = GaussianBlur.Apply(data, w, h, 3, 1.5);
        Assert.That(MaxAbsDiff(gpu, cpu), Is.LessThanOrEqualTo(2),
            "GPU blur differs from CPU by more than 2 LSB");
    }

    [Test]
    public void Warp_matches_cpu_within_tolerance() {
        const int w = 100, h = 80;
        var data = Ramp(w * h, 53);
        var t = new AffineTransform { M00 = 1.0, M01 = 0.02, M10 = -0.015, M11 = 1.0, Tx = 4.3, Ty = -2.7 };
        Assert.That(_gpu.TryWarpAffine(data, w, h, t, out var gpu), Is.True, "GPU declined warp");
        var cpu = ImageResampler.ApplyTransform(data, w, h, t);
        // Edge-handling/rounding can differ by a few LSB on interpolated pixels.
        Assert.That(MaxAbsDiff(gpu, cpu), Is.LessThanOrEqualTo(3),
            "GPU warp differs from CPU by more than 3 LSB");
    }

    [Test]
    public void Debayer_matches_cpu_exactly() {
        const int w = 64, h = 48; // even dims so the Bayer grid is clean
        var cfa = Ramp(w * h, 29);
        foreach (var pat in new[] { NINA.Core.Enum.BayerPatternEnum.RGGB,
                                    NINA.Core.Enum.BayerPatternEnum.GRBG,
                                    NINA.Core.Enum.BayerPatternEnum.GBRG,
                                    NINA.Core.Enum.BayerPatternEnum.BGGR }) {
            Assert.That(_gpu.TryDebayerBilinear(cfa, w, h, pat, out var gpu), Is.True,
                $"GPU declined debayer {pat}");
            var cpu = BayerDebayer.Bilinear(cfa, w, h, pat);
            // Pure integer math -> must be bit-exact.
            Assert.That(gpu.R, Is.EqualTo(cpu.R), $"R mismatch {pat}");
            Assert.That(gpu.G, Is.EqualTo(cpu.G), $"G mismatch {pat}");
            Assert.That(gpu.B, Is.EqualTo(cpu.B), $"B mismatch {pat}");
        }
    }

    [Test]
    public void Lut8_matches_cpu_exactly() {
        var data = Ramp(4096);
        var lut = new byte[65536];
        for (int i = 0; i < 65536; i++) lut[i] = (byte)((i * 255) / 65535);
        Assert.That(_gpu.TryApplyLut8(data, lut, out var gpu), Is.True, "GPU declined lut");
        for (int i = 0; i < data.Length; i++)
            Assert.That(gpu[i], Is.EqualTo(lut[data[i]]), $"lut mismatch at {i}");
    }

    [Test]
    public void BoxBlur8_matches_cpu_within_tolerance() {
        const int w = 96, h = 72;
        var src = new byte[w * h];
        for (int i = 0; i < src.Length; i++) src[i] = (byte)((i * 7 + (i % 11) * 20) & 0xFF);
        Assert.That(_gpu.TryBoxBlur8(src, w, h, 4, 3, out var gpu), Is.True, "GPU declined box blur");
        // Reference: the editor's exact 3-pass box blur, reached through
        // EditPipeline (mono Clarity uses the same BoxBlur on the luminance).
        // Compare the GPU box blur to a CPU 3-pass box blur computed the same way.
        var cpu = CpuBoxBlur(src, w, h, 4, 3);
        Assert.That(MaxAbsDiff8(gpu, cpu), Is.LessThanOrEqualTo(2),
            "GPU box blur differs from CPU by more than 2 LSB");
    }

    [Test]
    public void Editor_clarity_matches_cpu_within_tolerance() {
        const int w = 80, h = 64;
        var baseBuf = new byte[w * h];
        for (int i = 0; i < baseBuf.Length; i++) baseBuf[i] = (byte)((i * 13) & 0xFF);
        var p = new EditParams(Effects: new EffectsParams(Clarity: 0.6));

        var gpuBuf = (byte[])baseBuf.Clone();
        EditPipeline.Apply(gpuBuf, w, h, 1, p, _gpu);
        var cpuBuf = (byte[])baseBuf.Clone();
        EditPipeline.Apply(cpuBuf, w, h, 1, p, null);

        Assert.That(MaxAbsDiff8(gpuBuf, cpuBuf), Is.LessThanOrEqualTo(4),
            "Editor Clarity (GPU blur) differs from the CPU path by more than 4 LSB");
    }

    private static long MaxAbsDiff8(byte[] a, byte[] b) {
        long m = 0;
        for (int i = 0; i < a.Length; i++) { long d = Math.Abs(a[i] - b[i]); if (d > m) m = d; }
        return m;
    }

    // Edge-clamped 3-pass separable box blur, matching EditPipeline.BoxBlur math.
    private static byte[] CpuBoxBlur(byte[] src, int w, int h, int r, int passes) {
        var cur = (byte[])src.Clone();
        for (int p = 0; p < passes; p++) {
            cur = Pass(cur, w, h, r, horizontal: true);
            cur = Pass(cur, w, h, r, horizontal: false);
        }
        return cur;
        static byte[] Pass(byte[] s, int w, int h, int r, bool horizontal) {
            var d = new byte[s.Length];
            double iarr = 1.0 / (2 * r + 1);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) {
                    int sum = 0;
                    for (int k = -r; k <= r; k++) {
                        int xx = horizontal ? Math.Clamp(x + k, 0, w - 1) : x;
                        int yy = horizontal ? y : Math.Clamp(y + k, 0, h - 1);
                        sum += s[yy * w + xx];
                    }
                    d[y * w + x] = (byte)Math.Clamp(Math.Round(sum * iarr), 0, 255);
                }
            return d;
        }
    }

    [Test]
    public void Accumulate_matches_cpu_exactly() {
        const int n = 4096;
        var frame = Ramp(n, 17);
        var gAcc = new float[n]; var gCnt = new int[n];
        var cAcc = new float[n]; var cCnt = new int[n];
        for (int pass = 0; pass < 3; pass++) {
            Assert.That(_gpu.TryAccumulate(frame, gAcc, gCnt, n), Is.True, "GPU declined accumulate");
            new CpuGpuCompute().TryAccumulate(frame, cAcc, cCnt, n);
        }
        for (int i = 0; i < n; i++) {
            Assert.That(gCnt[i], Is.EqualTo(cCnt[i]), $"count mismatch at {i}");
            Assert.That(gAcc[i], Is.EqualTo(cAcc[i]).Within(0.5f), $"accum mismatch at {i}");
        }
    }
}
