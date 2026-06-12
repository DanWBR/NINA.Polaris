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
using NINA.Image.ImageAnalysis;

namespace NINA.Image.Gpu;

/// <summary>
/// Abstraction over the embarrassingly-parallel image kernels that can run on
/// either the CPU (<see cref="CpuGpuCompute"/>, the canonical reference path,
/// always available) or the SBC GPU via OpenCL (the Polaris-side
/// <c>OpenClGpuCompute</c>, used only when the board exposes a usable OpenCL
/// device).
///
/// Every method follows the same contract: it returns <c>true</c> when it
/// produced a result, and <c>false</c> when the backend declined (no GPU,
/// kernel build failed, buffer too large, runtime error). On <c>false</c> the
/// caller MUST run the existing CPU helper itself, so the CPU code stays the
/// behavioural reference and nothing breaks when the GPU is absent:
///
/// <code>
/// if (!gpu.TrySeparableBlur(src, w, h, r, sigma, out var blurred))
///     blurred = GaussianBlur.Apply(src, w, h, r, sigma);
/// </code>
///
/// The default <see cref="CpuGpuCompute"/> always returns <c>true</c> (it *is*
/// the CPU path), so call sites can be written uniformly; the OpenCL impl is the
/// only one that ever returns <c>false</c>.
/// </summary>
public interface IGpuCompute {
    /// <summary>Short backend name for logs / status (e.g. "CPU", "OpenCL: Adreno 643").</summary>
    string BackendName { get; }

    /// <summary>True when this is a real hardware-accelerated backend (OpenCL),
    /// false for the CPU fallback. Lets callers/telemetry tell them apart.</summary>
    bool IsHardware { get; }

    /// <summary>
    /// Separable Gaussian blur (powers the editor Clarity/Texture/Sharpen
    /// unsharp-mask stages). Mirrors <see cref="GaussianBlur.Apply"/>.
    /// </summary>
    bool TrySeparableBlur(ushort[] data, int width, int height, int radius,
                          double sigma, out ushort[] result);

    /// <summary>
    /// 8-bit edge-clamped box blur, <paramref name="passes"/> passes of
    /// (horizontal then vertical), on a single-channel uchar plane. This is the
    /// editor's local-contrast blur (Clarity/Texture/Sharpen), the heaviest
    /// per-slider editor cost. <see cref="CpuGpuCompute"/> declines (returns
    /// false) so the caller keeps using its own canonical CPU box blur.
    /// </summary>
    bool TryBoxBlur8(byte[] src, int width, int height, int radius, int passes, out byte[] result);

    /// <summary>
    /// Affine warp + bilinear resample (live-stack frame alignment). Mirrors
    /// <see cref="ImageResampler.ApplyTransform"/>.
    /// </summary>
    bool TryWarpAffine(ushort[] source, int width, int height,
                       AffineTransform transform, out ushort[] result);

    /// <summary>
    /// Bilinear debayer of a CFA frame into R/G/B planes (live-stack preproc).
    /// Mirrors <see cref="BayerDebayer.Bilinear"/>.
    /// </summary>
    bool TryDebayerBilinear(ushort[] cfa, int width, int height,
                            BayerPatternEnum pattern, out BayerDebayer.Channels result);

    /// <summary>
    /// Apply a precomputed 16-bit -> 8-bit lookup table per pixel
    /// (<c>result[i] = lut[data[i]]</c>); the hot path of
    /// <see cref="AutoStretch.ApplyManual"/>. <paramref name="lut"/> has 65536
    /// entries.
    /// </summary>
    bool TryApplyLut8(ushort[] data, byte[] lut, out byte[] result);

    /// <summary>
    /// Running-mean accumulate for live stacking, in place:
    /// <c>if (frame[i] > 0) { accum[i] += frame[i]; count[i]++; }</c>. Zero
    /// pixels are warp-edge no-data and are skipped so they don't bias the mean.
    /// Returns false if it declined (caller then accumulates on the CPU). All
    /// three arrays share length <paramref name="length"/>.
    /// </summary>
    bool TryAccumulate(ushort[] frame, float[] accum, int[] count, int length);
}
