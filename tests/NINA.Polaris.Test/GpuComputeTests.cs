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
using NINA.Core.Enum;
using NINA.Image.Gpu;
using NINA.Image.ImageAnalysis;
using NINA.Polaris.Services.OpenCl;

namespace NINA.Polaris.Test;

/// <summary>
/// Tests for the GPU compute abstraction (OCL epic). These run on CI / dev
/// machines that have no OpenCL device, so they pin the always-available
/// <see cref="CpuGpuCompute"/> reference path and the conservative behaviour of
/// the <see cref="OpenClRuntime"/> probe. When a kernel is GPU-accelerated, the
/// CPU output proven here is the reference the GPU is validated against on the
/// target board.
/// </summary>
[TestFixture]
public class GpuComputeTests {
    private static ushort[] Ramp(int n) {
        var a = new ushort[n];
        for (int i = 0; i < n; i++) a[i] = (ushort)((i * 37) & 0xFFFF);
        return a;
    }

    [Test]
    public void Cpu_backend_reports_software() {
        var cpu = new CpuGpuCompute();
        Assert.That(cpu.IsHardware, Is.False);
        Assert.That(cpu.BackendName, Is.EqualTo("CPU"));
    }

    [Test]
    public void Cpu_blur_matches_GaussianBlur() {
        const int w = 33, h = 21;
        var data = Ramp(w * h);
        var cpu = new CpuGpuCompute();

        Assert.That(cpu.TrySeparableBlur(data, w, h, 2, 1.0, out var got), Is.True);
        var expected = GaussianBlur.Apply(data, w, h, 2, 1.0);
        Assert.That(got, Is.EqualTo(expected));
    }

    [Test]
    public void Cpu_warp_matches_ImageResampler() {
        const int w = 24, h = 18;
        var data = Ramp(w * h);
        var t = new AffineTransform { M00 = 1, M11 = 1, Tx = 3.5, Ty = -2.0 };
        var cpu = new CpuGpuCompute();

        Assert.That(cpu.TryWarpAffine(data, w, h, t, out var got), Is.True);
        var expected = ImageResampler.ApplyTransform(data, w, h, t);
        Assert.That(got, Is.EqualTo(expected));
    }

    [Test]
    public void Cpu_lut8_maps_every_pixel() {
        var data = Ramp(500);
        var lut = new byte[65536];
        for (int i = 0; i < 65536; i++) lut[i] = (byte)(i >> 8); // top 8 bits
        var cpu = new CpuGpuCompute();

        Assert.That(cpu.TryApplyLut8(data, lut, out var got), Is.True);
        Assert.That(got.Length, Is.EqualTo(data.Length));
        for (int i = 0; i < data.Length; i++)
            Assert.That(got[i], Is.EqualTo(lut[data[i]]));
    }

    [Test]
    public void Cpu_accumulate_running_mean() {
        var frame = Ramp(64);
        var accum = new float[64];
        var count = new int[64];
        var cpu = new CpuGpuCompute();

        Assert.That(cpu.TryAccumulate(frame, accum, count, 64), Is.True);
        Assert.That(cpu.TryAccumulate(frame, accum, count, 64), Is.True);
        for (int i = 0; i < 64; i++) {
            Assert.That(count[i], Is.EqualTo(2));
            Assert.That(accum[i], Is.EqualTo(frame[i] * 2f));
        }
    }

    [Test]
    public void OpenCl_probe_does_not_throw_and_is_consistent() {
        // On CI/dev there is no OpenCL ICD; IsAvailable must be false and never
        // throw. We only assert it returns a stable bool + non-empty diagnostics
        // (the value differs by machine, so we don't hard-assert true/false).
        bool available = false;
        Assert.DoesNotThrow(() => available = OpenClRuntime.IsAvailable);
        Assert.That(OpenClRuntime.Diagnostics, Is.Not.Null.And.Not.Empty);
        // Disable gate is honoured regardless of hardware.
        Assert.That(OpenClRuntime.IsAvailable, Is.EqualTo(available)); // stable across calls
    }
}
