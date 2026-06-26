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
using NINA.Polaris.Services.OpenCl;

namespace NINA.Polaris.Test;

/// <summary>
/// Unit tests for <see cref="GpuOffloadPolicy"/> — the per-op decision that keeps
/// the GPU offload a win on unified-memory SBCs (offload everything) while
/// avoiding the regression on a discrete GPU (offload only the ops the probe
/// measured as actually faster). The decision is a pure function of its inputs,
/// so it is fully testable without any OpenCL device.
/// </summary>
[TestFixture]
public class GpuOffloadPolicyTests {

    private static readonly GpuOp[] AllOps = System.Enum.GetValues<GpuOp>();

    // ----- AllowAll (unified memory / SBC) -----

    [Test]
    public void AllowAll_offloads_every_op() {
        var p = GpuOffloadPolicy.AllowAll(unifiedMemory: true);
        Assert.That(p.UnifiedMemory, Is.True);
        Assert.That(p.Probed, Is.False);
        foreach (var op in AllOps)
            Assert.That(p.Allows(op), Is.True, $"{op} must offload on unified memory");
        Assert.That(p.AllowedOps, Is.EquivalentTo(AllOps));
    }

    [Test]
    public void AllowAll_records_unified_flag() {
        Assert.That(GpuOffloadPolicy.AllowAll(unifiedMemory: false).UnifiedMemory, Is.False);
        Assert.That(GpuOffloadPolicy.AllowAll(unifiedMemory: true).UnifiedMemory, Is.True);
    }

    // ----- FromProbe (discrete GPU) -----

    [Test]
    public void FromProbe_discrete_like_RTX_offloads_only_blur() {
        // Mirrors the measured RTX 5070 numbers: warp 0.47x and debayer 0.40x are
        // SLOWER than the CPU, only blur (16.17x) wins. So warp + debayer + the
        // transfer-bound lut/accumulate stay on the CPU; blur (and the derived
        // box blur) offload.
        var speedups = new Dictionary<GpuOp, double> {
            [GpuOp.Warp] = 0.47,
            [GpuOp.Debayer] = 0.40,
            [GpuOp.SeparableBlur] = 16.17,
            [GpuOp.ApplyLut8] = 0.5,
            [GpuOp.Accumulate] = 0.6,
        };
        var p = GpuOffloadPolicy.FromProbe(speedups);

        Assert.That(p.Probed, Is.True);
        Assert.That(p.UnifiedMemory, Is.False);
        Assert.That(p.Allows(GpuOp.Warp), Is.False);
        Assert.That(p.Allows(GpuOp.Debayer), Is.False);
        Assert.That(p.Allows(GpuOp.ApplyLut8), Is.False);
        Assert.That(p.Allows(GpuOp.Accumulate), Is.False);
        Assert.That(p.Allows(GpuOp.SeparableBlur), Is.True);
        // BoxBlur8 isn't probed directly; it follows the separable-blur result.
        Assert.That(p.Allows(GpuOp.BoxBlur8), Is.True);
        Assert.That(p.AllowedOps, Is.EquivalentTo(new[] { GpuOp.SeparableBlur, GpuOp.BoxBlur8 }));
    }

    [Test]
    public void FromProbe_unified_memory_Adreno_gates_out_losing_ops() {
        // Mirrors the measured Radxa Dragon Q6A / Adreno 643 numbers: the OpenCL
        // device reports unified memory, yet warp (0.69x) and debayer (0.34x) are
        // SLOWER than the CPU because the Qualcomm stack copies host<->device for
        // ordinary buffers. Only blur (2.56x) wins. The probe must therefore gate
        // out warp/debayer even on a unified-memory device — "unified ⇒ everything
        // wins" is false here. The unified flag is recorded for diagnostics but the
        // gating is purely by measured speedup.
        var speedups = new Dictionary<GpuOp, double> {
            [GpuOp.Warp] = 0.69,
            [GpuOp.Debayer] = 0.34,
            [GpuOp.SeparableBlur] = 2.56,
            [GpuOp.ApplyLut8] = 0.8,
            [GpuOp.Accumulate] = 0.9,
        };
        var p = GpuOffloadPolicy.FromProbe(speedups, unifiedMemory: true);

        Assert.That(p.Probed, Is.True);
        Assert.That(p.UnifiedMemory, Is.True, "the unified flag is carried for diagnostics");
        Assert.That(p.Allows(GpuOp.Warp), Is.False);
        Assert.That(p.Allows(GpuOp.Debayer), Is.False);
        Assert.That(p.Allows(GpuOp.ApplyLut8), Is.False);
        Assert.That(p.Allows(GpuOp.Accumulate), Is.False);
        Assert.That(p.Allows(GpuOp.SeparableBlur), Is.True);
        Assert.That(p.Allows(GpuOp.BoxBlur8), Is.True);
        Assert.That(p.AllowedOps, Is.EquivalentTo(new[] { GpuOp.SeparableBlur, GpuOp.BoxBlur8 }));
    }

    [Test]
    public void FromProbe_unified_memory_Mali_keeps_everything() {
        // The Orange Pi 5 Pro / Mali-G610 measured every op as a win, so probing a
        // unified-memory device there is identical to the old full-offload path.
        var speedups = new Dictionary<GpuOp, double> {
            [GpuOp.Warp] = 3.41,
            [GpuOp.Debayer] = 1.19,
            [GpuOp.SeparableBlur] = 12.95,
            [GpuOp.ApplyLut8] = 1.4,
            [GpuOp.Accumulate] = 1.8,
        };
        var p = GpuOffloadPolicy.FromProbe(speedups, unifiedMemory: true);
        foreach (var op in AllOps)
            Assert.That(p.Allows(op), Is.True, $"{op} should still offload on Mali (it won)");
    }

    [Test]
    public void FromProbe_when_everything_wins_offloads_everything() {
        var speedups = new Dictionary<GpuOp, double> {
            [GpuOp.Warp] = 3.4,
            [GpuOp.Debayer] = 1.2,
            [GpuOp.SeparableBlur] = 13.0,
            [GpuOp.ApplyLut8] = 1.5,
            [GpuOp.Accumulate] = 2.0,
        };
        var p = GpuOffloadPolicy.FromProbe(speedups);
        foreach (var op in AllOps)
            Assert.That(p.Allows(op), Is.True, $"{op} should offload when its probe won");
    }

    [Test]
    public void FromProbe_when_nothing_wins_offloads_nothing() {
        var speedups = new Dictionary<GpuOp, double> {
            [GpuOp.Warp] = 0.4,
            [GpuOp.Debayer] = 0.3,
            [GpuOp.SeparableBlur] = 0.9,
            [GpuOp.ApplyLut8] = 0.2,
            [GpuOp.Accumulate] = 0.5,
        };
        var p = GpuOffloadPolicy.FromProbe(speedups);
        foreach (var op in AllOps)
            Assert.That(p.Allows(op), Is.False, $"{op} should stay on CPU when the GPU lost");
        Assert.That(p.AllowedOps, Is.Empty);
    }

    [Test]
    public void FromProbe_threshold_is_inclusive_at_exactly_one() {
        // Exactly 1.0x (a tie) counts as "not slower" and offloads.
        var p = GpuOffloadPolicy.FromProbe(new Dictionary<GpuOp, double> {
            [GpuOp.Warp] = 1.0,
            [GpuOp.SeparableBlur] = 0.99,
        });
        Assert.That(p.Allows(GpuOp.Warp), Is.True);
        Assert.That(p.Allows(GpuOp.SeparableBlur), Is.False);
        // SeparableBlur lost, so the derived BoxBlur8 must not offload either.
        Assert.That(p.Allows(GpuOp.BoxBlur8), Is.False);
    }

    [Test]
    public void FromProbe_honours_custom_minSpeedup() {
        // With a 2x bar, a 1.5x op no longer qualifies.
        var speedups = new Dictionary<GpuOp, double> {
            [GpuOp.Warp] = 1.5,
            [GpuOp.SeparableBlur] = 2.5,
        };
        var p = GpuOffloadPolicy.FromProbe(speedups, minSpeedup: 2.0);
        Assert.That(p.MinSpeedup, Is.EqualTo(2.0));
        Assert.That(p.Allows(GpuOp.Warp), Is.False);
        Assert.That(p.Allows(GpuOp.SeparableBlur), Is.True);
    }

    [Test]
    public void FromProbe_exposes_measured_speedups() {
        var speedups = new Dictionary<GpuOp, double> {
            [GpuOp.Warp] = 0.47,
            [GpuOp.SeparableBlur] = 16.17,
        };
        var p = GpuOffloadPolicy.FromProbe(speedups);
        Assert.That(p.Speedups[GpuOp.Warp], Is.EqualTo(0.47));
        Assert.That(p.Speedups[GpuOp.SeparableBlur], Is.EqualTo(16.17));
    }

    [Test]
    public void FromProbe_does_not_invent_BoxBlur8_when_blur_not_measured() {
        // If SeparableBlur wasn't probed, BoxBlur8 can't be derived and stays off.
        var p = GpuOffloadPolicy.FromProbe(new Dictionary<GpuOp, double> {
            [GpuOp.Warp] = 5.0,
        });
        Assert.That(p.Allows(GpuOp.BoxBlur8), Is.False);
        Assert.That(p.Allows(GpuOp.Warp), Is.True);
    }
}
