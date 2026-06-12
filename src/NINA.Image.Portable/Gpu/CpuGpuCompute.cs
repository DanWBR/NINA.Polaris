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

using System.Collections.Concurrent;
using System.Threading.Tasks;
using NINA.Core.Enum;
using NINA.Image.ImageAnalysis;

namespace NINA.Image.Gpu;

/// <summary>
/// Default <see cref="IGpuCompute"/> backend: runs every kernel on the CPU by
/// delegating to the existing, already-parallelized static helpers. It is always
/// available, is the behavioural reference the OpenCL backend is validated
/// against, and every method returns <c>true</c> (the CPU never "declines").
///
/// This is what runs on Raspberry Pi / x86 and anywhere the OpenCL probe fails,
/// so the feature is a pure no-op until a board with a usable GPU is present.
/// </summary>
public sealed class CpuGpuCompute : IGpuCompute {
    public string BackendName => "CPU";
    public bool IsHardware => false;

    public bool TrySeparableBlur(ushort[] data, int width, int height, int radius,
                                 double sigma, out ushort[] result) {
        result = GaussianBlur.Apply(data, width, height, radius, sigma);
        return true;
    }

    public bool TryBoxBlur8(byte[] src, int width, int height, int radius, int passes, out byte[] result) {
        // The CPU box blur lives in EditPipeline (private, well-tested). Decline
        // so the caller runs that canonical version; only the OpenCL backend
        // accelerates this op.
        result = System.Array.Empty<byte>();
        return false;
    }

    public bool TryWarpAffine(ushort[] source, int width, int height,
                              AffineTransform transform, out ushort[] result) {
        result = ImageResampler.ApplyTransform(source, width, height, transform);
        return true;
    }

    public bool TryDebayerBilinear(ushort[] cfa, int width, int height,
                                   BayerPatternEnum pattern, out BayerDebayer.Channels result) {
        result = BayerDebayer.Bilinear(cfa, width, height, pattern);
        return true;
    }

    public bool TryApplyLut8(ushort[] data, byte[] lut, out byte[] result) {
        var outp = new byte[data.Length];
        // Same per-pixel LUT map AutoStretch.ApplyManual uses; embarrassingly
        // parallel so it fans out across cores.
        Parallel.ForEach(Partitioner.Create(0, data.Length), range => {
            for (int i = range.Item1; i < range.Item2; i++)
                outp[i] = lut[data[i]];
        });
        result = outp;
        return true;
    }

    public bool TryAccumulate(ushort[] frame, float[] accum, int[] count, int length) {
        for (int i = 0; i < length; i++) {
            accum[i] += frame[i];
            count[i]++;
        }
        return true;
    }
}
