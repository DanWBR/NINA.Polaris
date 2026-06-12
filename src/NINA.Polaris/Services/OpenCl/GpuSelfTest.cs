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

using NINA.Core.Enum;
using NINA.Image.Gpu;
using NINA.Image.ImageAnalysis;

namespace NINA.Polaris.Services.OpenCl;

/// <summary>
/// In-process GPU-vs-CPU kernel validation, shipped in the binary so a board
/// with no source / test project (e.g. an installed .deb on an Orange Pi) can
/// still confirm the OpenCL kernels produce correct output on its specific GPU
/// (Mali fp behaviour can differ from the dev box). Each kernel runs on the
/// injected <see cref="IGpuCompute"/> and is diffed against the CPU reference;
/// the result is returned as data for the <c>/api/system/gpu/selftest</c>
/// endpoint. Small synthetic buffers keep it sub-second.
/// </summary>
public static class GpuSelfTest {
    public record KernelResult(string Kernel, bool Ran, long MaxDiff, long Tolerance, bool Ok, string? Note = null);

    public static IReadOnlyList<KernelResult> Run(IGpuCompute gpu) {
        // The self-test validates kernel *correctness* on this GPU, so it must
        // exercise every kernel even when the production offload policy (on a
        // discrete GPU) would decline some of them for performance. Force the
        // policy to allow-all for the duration, restoring it afterwards.
        if (gpu is OpenClGpuCompute ocl) {
            ocl.EnsureInitialized();
            var saved = ocl.OffloadPolicy;
            ocl.OffloadPolicy = GpuOffloadPolicy.AllowAll(ocl.HostUnifiedMemory ?? true);
            try { return RunCore(gpu); } finally { ocl.OffloadPolicy = saved; }
        }
        return RunCore(gpu);
    }

    private static IReadOnlyList<KernelResult> RunCore(IGpuCompute gpu) {
        var results = new List<KernelResult>();
        var cpu = new CpuGpuCompute();

        // --- separable gaussian blur (16-bit) ---
        {
            const int w = 128, h = 96; var data = Ramp(w * h, 41);
            if (gpu.TrySeparableBlur(data, w, h, 3, 1.5, out var g)) {
                var c = GaussianBlur.Apply(data, w, h, 3, 1.5);
                long d = MaxDiff(g, c);
                results.Add(new("separableBlur", true, d, 2, d <= 2));
            } else results.Add(new("separableBlur", false, 0, 2, true, "declined (CPU backend)"));
        }
        // --- box blur (8-bit, editor) ---
        {
            const int w = 96, h = 72; var src = RampB(w * h);
            if (gpu.TryBoxBlur8(src, w, h, 4, 3, out var g)) {
                var c = CpuBoxBlur(src, w, h, 4, 3);
                long d = MaxDiff8(g, c);
                results.Add(new("boxBlur8", true, d, 2, d <= 2));
            } else results.Add(new("boxBlur8", false, 0, 2, true, "declined (CPU backend)"));
        }
        // --- affine warp ---
        {
            const int w = 100, h = 80; var data = Ramp(w * h, 53);
            var t = new AffineTransform { M00 = 1, M01 = 0.02, M10 = -0.015, M11 = 1, Tx = 4.3, Ty = -2.7 };
            if (gpu.TryWarpAffine(data, w, h, t, out var g)) {
                var c = ImageResampler.ApplyTransform(data, w, h, t);
                long d = MaxDiff(g, c);
                results.Add(new("warpAffine", true, d, 3, d <= 3));
            } else results.Add(new("warpAffine", false, 0, 3, true, "declined (CPU backend)"));
        }
        // --- debayer (bit-exact) ---
        {
            const int w = 64, h = 48; var cfa = Ramp(w * h, 29);
            if (gpu.TryDebayerBilinear(cfa, w, h, BayerPatternEnum.RGGB, out var g)) {
                var c = BayerDebayer.Bilinear(cfa, w, h, BayerPatternEnum.RGGB);
                long d = Math.Max(MaxDiff(g.R, c.R), Math.Max(MaxDiff(g.G, c.G), MaxDiff(g.B, c.B)));
                results.Add(new("debayerBilinear", true, d, 0, d == 0));
            } else results.Add(new("debayerBilinear", false, 0, 0, true, "declined (CPU backend)"));
        }
        // --- LUT apply (bit-exact) ---
        {
            var data = Ramp(4096, 31); var lut = new byte[65536];
            for (int i = 0; i < 65536; i++) lut[i] = (byte)((i * 255) / 65535);
            if (gpu.TryApplyLut8(data, lut, out var g)) {
                long d = 0; for (int i = 0; i < data.Length; i++) d = Math.Max(d, Math.Abs(g[i] - lut[data[i]]));
                results.Add(new("applyLut8", true, d, 0, d == 0));
            } else results.Add(new("applyLut8", false, 0, 0, true, "declined (CPU backend)"));
        }
        // --- accumulate (bit-exact vs CPU) ---
        {
            const int n = 4096; var frame = Ramp(n, 17);
            var ga = new float[n]; var gc = new int[n]; var ca = new float[n]; var cc = new int[n];
            bool ran = true;
            for (int p = 0; p < 2 && ran; p++) {
                ran = gpu.TryAccumulate(frame, ga, gc, n);
                cpu.TryAccumulate(frame, ca, cc, n);
            }
            if (ran) {
                long d = 0; for (int i = 0; i < n; i++) d = Math.Max(d, (long)Math.Abs(ga[i] - ca[i]) + Math.Abs(gc[i] - cc[i]));
                results.Add(new("accumulate", true, d, 0, d == 0));
            } else results.Add(new("accumulate", false, 0, 0, true, "declined (CPU backend)"));
        }
        return results;
    }

    private static ushort[] Ramp(int n, int mul) {
        var a = new ushort[n];
        for (int i = 0; i < n; i++) a[i] = (ushort)((i * mul + (i % 7) * 1000) & 0xFFFF);
        return a;
    }
    private static byte[] RampB(int n) {
        var a = new byte[n];
        for (int i = 0; i < n; i++) a[i] = (byte)((i * 7 + (i % 11) * 20) & 0xFF);
        return a;
    }
    private static long MaxDiff(ushort[] a, ushort[] b) {
        long m = 0; for (int i = 0; i < a.Length; i++) { long d = Math.Abs(a[i] - b[i]); if (d > m) m = d; } return m;
    }
    private static long MaxDiff8(byte[] a, byte[] b) {
        long m = 0; for (int i = 0; i < a.Length; i++) { long d = Math.Abs(a[i] - b[i]); if (d > m) m = d; } return m;
    }
    private static byte[] CpuBoxBlur(byte[] src, int w, int h, int r, int passes) {
        var cur = (byte[])src.Clone();
        for (int p = 0; p < passes; p++) { cur = Pass(cur, w, h, r, true); cur = Pass(cur, w, h, r, false); }
        return cur;
        static byte[] Pass(byte[] s, int w, int h, int r, bool horiz) {
            var d = new byte[s.Length]; double iarr = 1.0 / (2 * r + 1);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
                int sum = 0;
                for (int k = -r; k <= r; k++) {
                    int xx = horiz ? Math.Clamp(x + k, 0, w - 1) : x;
                    int yy = horiz ? y : Math.Clamp(y + k, 0, h - 1);
                    sum += s[yy * w + xx];
                }
                d[y * w + x] = (byte)Math.Clamp(Math.Round(sum * iarr), 0, 255);
            }
            return d;
        }
    }
}
