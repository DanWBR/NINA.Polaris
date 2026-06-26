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

namespace NINA.Polaris.Services.OpenCl;

/// <summary>The GPU-offloadable image kernels, used as the key of the per-op
/// offload allow-list (<see cref="GpuOffloadPolicy"/>).</summary>
public enum GpuOp { Warp, Debayer, SeparableBlur, BoxBlur8, ApplyLut8, Accumulate }

/// <summary>
/// Decides, per op, whether <c>OpenClGpuCompute</c> should offload to the GPU or
/// decline (so the caller runs the CPU reference). The GPU is not an
/// unconditional win even on the unified-memory SBCs we primarily target: on a
/// <i>discrete</i> GPU every op pays a PCIe host&lt;-&gt;device round-trip, and
/// even on a unified-memory SBC some OpenCL stacks still copy host↔device for
/// ordinary buffers (e.g. Qualcomm Adreno on the QCS6490), so the light
/// memory-bound kernels (warp, debayer) lose while the same code wins on Mali.
/// For the light kernels that transfer/copy can dominate and make the GPU path
/// <i>slower</i> than the CPU — only the heavier blur reliably wins. Offloading
/// everything blindly is then a net regression.
///
/// Two factories encode the policy:
/// <list type="bullet">
/// <item><see cref="FromProbe"/> — the production path for both discrete and
/// unified-memory devices: offload only the ops whose one-time micro-probe
/// measured the GPU at least <see cref="MinSpeedup"/>× the CPU. The probe
/// measures the actual condition that matters (does the GPU win for this op on
/// this hardware?), so it is robust to driver/board differences rather than
/// guessing from device class. It records <see cref="UnifiedMemory"/> for
/// diagnostics.</item>
/// <item><see cref="AllowAll"/> — force every op on, used by the self-test and
/// benchmark (via <c>WithAllKernels</c>) to validate/measure every kernel
/// regardless of the production decision.</item>
/// </list>
///
/// The decision is a pure function of the inputs, so it is unit-tested without
/// any GPU.
/// </summary>
public sealed class GpuOffloadPolicy {
    /// <summary>An op must be at least this many times faster on the GPU than the
    /// CPU to be offloaded on a discrete device. 1.0 = "GPU must not be slower".</summary>
    public const double DefaultMinSpeedup = 1.0;

    private readonly HashSet<GpuOp> _allow;

    /// <summary>True when built for a unified-memory (SBC) device — every op is
    /// allowed and no probe was run.</summary>
    public bool UnifiedMemory { get; }

    /// <summary>True when this policy came from a discrete-device micro-probe.</summary>
    public bool Probed { get; }

    /// <summary>The speedup threshold used by <see cref="FromProbe"/> (NaN for
    /// <see cref="AllowAll"/>).</summary>
    public double MinSpeedup { get; }

    /// <summary>Per-op GPU/CPU speedups measured by the probe (empty for
    /// <see cref="AllowAll"/>); surfaced for diagnostics / status.</summary>
    public IReadOnlyDictionary<GpuOp, double> Speedups { get; }

    private GpuOffloadPolicy(HashSet<GpuOp> allow, bool unifiedMemory, bool probed,
                             double minSpeedup, IReadOnlyDictionary<GpuOp, double> speedups) {
        _allow = allow;
        UnifiedMemory = unifiedMemory;
        Probed = probed;
        MinSpeedup = minSpeedup;
        Speedups = speedups;
    }

    /// <summary>True when <paramref name="op"/> should run on the GPU.</summary>
    public bool Allows(GpuOp op) => _allow.Contains(op);

    /// <summary>The ops this policy offloads (stable order, for logs / status).</summary>
    public IReadOnlyList<GpuOp> AllowedOps =>
        Enum.GetValues<GpuOp>().Where(_allow.Contains).ToArray();

    private static readonly IReadOnlyDictionary<GpuOp, double> EmptySpeedups =
        new Dictionary<GpuOp, double>();

    /// <summary>Force every op on (no probe). Used by the self-test and benchmark
    /// (<c>WithAllKernels</c>) to validate/measure every kernel regardless of the
    /// production decision. The production path uses <see cref="FromProbe"/>.</summary>
    public static GpuOffloadPolicy AllowAll(bool unifiedMemory) =>
        new(new HashSet<GpuOp>(Enum.GetValues<GpuOp>()), unifiedMemory,
            probed: false, double.NaN, EmptySpeedups);

    /// <summary>
    /// Probe-derived policy: offload an op only where the probe measured the GPU
    /// at least <paramref name="minSpeedup"/>× the CPU. Used for both discrete
    /// GPUs and unified-memory SBCs — the assumption that "unified memory ⇒ every
    /// op wins" is false on some stacks (e.g. Qualcomm Adreno copies host↔device
    /// for ordinary buffers, so the light memory-bound kernels — warp, debayer —
    /// actually lose, while the same code wins on Mali). Measuring the real
    /// condition per op makes the decision robust to driver/board differences.
    /// Pass <paramref name="unifiedMemory"/> through purely for diagnostics/status.
    ///
    /// <see cref="GpuOp.BoxBlur8"/> has no <c>CpuGpuCompute</c> reference (the CPU
    /// backend declines it), so it can't be probed directly; it follows the
    /// <see cref="GpuOp.SeparableBlur"/> result — both are convolution blurs and
    /// the box blur is multi-pass, i.e. at least as GPU-favourable, so if the
    /// separable blur wins the box blur wins too.
    /// </summary>
    public static GpuOffloadPolicy FromProbe(IReadOnlyDictionary<GpuOp, double> speedups,
                                             double minSpeedup = DefaultMinSpeedup,
                                             bool unifiedMemory = false) {
        var allow = new HashSet<GpuOp>();
        foreach (var kv in speedups)
            if (kv.Value >= minSpeedup) allow.Add(kv.Key);

        // Derive BoxBlur8 from SeparableBlur when it wasn't measured directly.
        if (!speedups.ContainsKey(GpuOp.BoxBlur8) &&
            speedups.TryGetValue(GpuOp.SeparableBlur, out var blur) && blur >= minSpeedup)
            allow.Add(GpuOp.BoxBlur8);

        return new(allow, unifiedMemory, probed: true, minSpeedup,
                   new Dictionary<GpuOp, double>(speedups));
    }
}
