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

namespace NINA.Polaris.Services.Sequencer.Instructions;

/// <summary>Switch a power-box outlet (a boolean channel) on or off.</summary>
public class SetPowerOutletInstruction : SequenceInstruction {
    public override string Type => "SetPowerOutlet";
    /// <summary>Channel id of the outlet (see the RIGS power box card).</summary>
    public int Outlet { get; set; }
    public bool On { get; set; } = true;

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var pb = ctx.Equipment.Switch ?? throw new InvalidOperationException("No power box connected");
        await pb.SetBoolAsync(Outlet, On, ct);
    }
}

/// <summary>Set a dew-heater / PWM channel level. Value is the channel's own
/// units (0-100 for typical INDI/ASCOM dew rails); it is clamped to the
/// channel's min/max by the device.</summary>
public class SetDewHeaterInstruction : SequenceInstruction {
    public override string Type => "SetDewHeater";
    /// <summary>Channel id of the dew/PWM output.</summary>
    public int Channel { get; set; }
    /// <summary>Level, typically 0-100.</summary>
    public int Percent { get; set; } = 50;

    public override IReadOnlyList<string> Validate() {
        if (Percent < 0) return new[] { "Percent must be >= 0" };
        return Array.Empty<string>();
    }

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var pb = ctx.Equipment.Switch ?? throw new InvalidOperationException("No power box connected");
        await pb.SetValueAsync(Channel, Percent, ct);
    }
}

/// <summary>Power-cycle an outlet: switch it off, wait, switch it back on.
/// Useful to recover a wedged USB device mid-sequence.</summary>
public class PowerCycleOutletInstruction : SequenceInstruction {
    public override string Type => "PowerCycleOutlet";
    public int Outlet { get; set; }
    /// <summary>Seconds to hold the outlet off before turning it back on.</summary>
    public int OffSeconds { get; set; } = 5;

    public override IReadOnlyList<string> Validate() {
        if (OffSeconds < 0) return new[] { "OffSeconds must be >= 0" };
        return Array.Empty<string>();
    }

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var pb = ctx.Equipment.Switch ?? throw new InvalidOperationException("No power box connected");
        await pb.SetBoolAsync(Outlet, false, ct);
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, OffSeconds)), ct);
        await pb.SetBoolAsync(Outlet, true, ct);
    }
}

/// <summary>Generic ISwitchV2 escape hatch: set any channel to an arbitrary
/// value (clamped to its range by the device). Covers channels that are
/// neither a simple outlet nor a 0-100 dew rail.</summary>
public class SetSwitchValueInstruction : SequenceInstruction {
    public override string Type => "SetSwitchValue";
    public int Channel { get; set; }
    public double Value { get; set; }

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var pb = ctx.Equipment.Switch ?? throw new InvalidOperationException("No power box connected");
        await pb.SetValueAsync(Channel, Value, ct);
    }
}
