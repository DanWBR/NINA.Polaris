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
/// decline (so the caller runs the CPU reference). This exists because the GPU
/// is only an unconditional win on the unified-memory SBCs we primarily target
/// (Mali/Adreno): there a buffer is shared, so even a tiny kernel is free to
/// offload. On a <i>discrete</i> GPU (e.g. an NVIDIA card on a Windows mini-PC)
/// every op pays a PCIe host&lt;-&gt;device round-trip, and for the light kernels
/// (warp, debayer) that transfer dominates and makes the GPU path <i>slower</i>
/// than a fast desktop CPU — only the heavier blur wins. Offloading everything
/// there is a net regression.
///
/// Two factories encode the policy:
/// <list type="bullet">
/// <item><see cref="AllowAll"/> — unified memory (SBC): offload every op, the
/// historical behaviour, kept unchanged for the primary target.</item>
/// <item><see cref="FromProbe"/> — discrete memory: offload only the ops whose
/// one-time micro-probe measured the GPU at least <see cref="MinSpeedup"/>× the
/// CPU. The probe measures the actual condition that matters (does the GPU win
/// for this op on this hardware?), so it is robust to driver/board differences
/// rather than guessing from device class.</item>
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

    /// <summary>Unified-memory (SBC) policy: offload every op. This is the
    /// historical full-offload behaviour, preserved unchanged for the primary
    /// target so the SBC path is never gated by a probe.</summary>
    public static GpuOffloadPolicy AllowAll(bool unifiedMemory) =>
        new(new HashSet<GpuOp>(Enum.GetValues<GpuOp>()), unifiedMemory,
            probed: false, double.NaN, EmptySpeedups);

    /// <summary>
    /// Discrete-device policy: offload an op only where the probe measured the
    /// GPU at least <paramref name="minSpeedup"/>× the CPU.
    ///
    /// <see cref="GpuOp.BoxBlur8"/> has no <c>CpuGpuCompute</c> reference (the CPU
    /// backend declines it), so it can't be probed directly; it follows the
    /// <see cref="GpuOp.SeparableBlur"/> result — both are convolution blurs and
    /// the box blur is multi-pass, i.e. at least as GPU-favourable, so if the
    /// separable blur wins the box blur wins too.
    /// </summary>
    public static GpuOffloadPolicy FromProbe(IReadOnlyDictionary<GpuOp, double> speedups,
                                             double minSpeedup = DefaultMinSpeedup) {
        var allow = new HashSet<GpuOp>();
        foreach (var kv in speedups)
            if (kv.Value >= minSpeedup) allow.Add(kv.Key);

        // Derive BoxBlur8 from SeparableBlur when it wasn't measured directly.
        if (!speedups.ContainsKey(GpuOp.BoxBlur8) &&
            speedups.TryGetValue(GpuOp.SeparableBlur, out var blur) && blur >= minSpeedup)
            allow.Add(GpuOp.BoxBlur8);

        return new(allow, unifiedMemory: false, probed: true, minSpeedup,
                   new Dictionary<GpuOp, double>(speedups));
    }
}
