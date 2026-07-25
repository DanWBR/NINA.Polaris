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

using NINA.Image.Interfaces;

namespace NINA.Polaris.Services.Sequencer.Instructions;

/// <summary>
/// Shared channel addressing for the power-box instructions.
/// </summary>
internal static class PowerBoxTarget {
    /// <summary>
    /// Resolve which channel an instruction acts on.
    ///
    /// <para>The numeric channel id is a POSITION in the device's current
    /// channel map, and a saved sequence outlives that map: the order shifts
    /// whenever the driver publishes a different property set (a firmware or
    /// driver update, a different power box on the same rig, INDI vs ASCOM for
    /// the same hardware). A sequence that said "power cycle outlet 3" would
    /// then cut power to whatever now sits at index 3. <see cref="SwitchChannel.Key"/>
    /// is stable across all of that, so it wins whenever it was recorded.</para>
    ///
    /// <para>A key that no longer exists THROWS rather than falling back to the
    /// index. Falling back is precisely the failure being prevented: it would
    /// silently act on an unrelated outlet, and on a power box that means
    /// yanking power from real equipment.</para>
    /// </summary>
    public static int Resolve(ISwitchDevice pb, string? channelKey, int fallbackId) {
        if (string.IsNullOrWhiteSpace(channelKey)) return fallbackId;
        foreach (var c in pb.Channels) {
            if (string.Equals(c.Key, channelKey, StringComparison.Ordinal)) return c.Id;
        }
        throw new InvalidOperationException(
            $"Power box channel '{channelKey}' is not present on '{pb.DeviceName}'. " +
            "Re-select the channel in this instruction; acting on the stored " +
            "position instead could switch the wrong outlet.");
    }
}

/// <summary>Switch a power-box outlet (a boolean channel) on or off.</summary>
public class SetPowerOutletInstruction : SequenceInstruction {
    public override string Type => "SetPowerOutlet";
    /// <summary>Channel id of the outlet (see the RIGS power box card).
    /// Only used when <see cref="ChannelKey"/> is empty.</summary>
    public int Outlet { get; set; }
    /// <summary>Stable channel key (<c>PROPERTY.ELEMENT</c> over INDI,
    /// <c>#index</c> over ASCOM/Alpaca). Preferred over <see cref="Outlet"/>:
    /// it survives the channel list being reordered. Empty on sequences saved
    /// before this existed, which keep using the numeric id.</summary>
    public string ChannelKey { get; set; } = "";
    public bool On { get; set; } = true;

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var pb = ctx.Equipment.Switch ?? throw new InvalidOperationException("No power box connected");
        await pb.SetBoolAsync(PowerBoxTarget.Resolve(pb, ChannelKey, Outlet), On, ct);
    }
}

/// <summary>Set a dew-heater / PWM channel level. Value is the channel's own
/// units (0-100 for typical INDI/ASCOM dew rails); it is clamped to the
/// channel's min/max by the device.</summary>
public class SetDewHeaterInstruction : SequenceInstruction {
    public override string Type => "SetDewHeater";
    /// <summary>Channel id of the dew/PWM output. Only used when
    /// <see cref="ChannelKey"/> is empty.</summary>
    public int Channel { get; set; }
    /// <summary>Stable channel key; preferred over <see cref="Channel"/>.
    /// See <see cref="PowerBoxTarget.Resolve"/>.</summary>
    public string ChannelKey { get; set; } = "";
    /// <summary>Level, typically 0-100.</summary>
    public int Percent { get; set; } = 50;

    public override IReadOnlyList<string> Validate() {
        if (Percent < 0) return new[] { "Percent must be >= 0" };
        return Array.Empty<string>();
    }

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var pb = ctx.Equipment.Switch ?? throw new InvalidOperationException("No power box connected");
        await pb.SetValueAsync(PowerBoxTarget.Resolve(pb, ChannelKey, Channel), Percent, ct);
    }
}

/// <summary>Power-cycle an outlet: switch it off, wait, switch it back on.
/// Useful to recover a wedged USB device mid-sequence.</summary>
public class PowerCycleOutletInstruction : SequenceInstruction {
    public override string Type => "PowerCycleOutlet";
    /// <summary>Only used when <see cref="ChannelKey"/> is empty.</summary>
    public int Outlet { get; set; }
    /// <summary>Stable channel key; preferred over <see cref="Outlet"/>.
    /// See <see cref="PowerBoxTarget.Resolve"/>.</summary>
    public string ChannelKey { get; set; } = "";
    /// <summary>Seconds to hold the outlet off before turning it back on.</summary>
    public int OffSeconds { get; set; } = 5;

    public override IReadOnlyList<string> Validate() {
        if (OffSeconds < 0) return new[] { "OffSeconds must be >= 0" };
        return Array.Empty<string>();
    }

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var pb = ctx.Equipment.Switch ?? throw new InvalidOperationException("No power box connected");
        // Resolve ONCE: re-resolving for the on-write could land on a different
        // channel if the device republished its properties during the wait.
        var id = PowerBoxTarget.Resolve(pb, ChannelKey, Outlet);
        await pb.SetBoolAsync(id, false, ct);
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, OffSeconds)), ct);
        await pb.SetBoolAsync(id, true, ct);
    }
}

/// <summary>Generic ISwitchV2 escape hatch: set any channel to an arbitrary
/// value (clamped to its range by the device). Covers channels that are
/// neither a simple outlet nor a 0-100 dew rail.</summary>
public class SetSwitchValueInstruction : SequenceInstruction {
    public override string Type => "SetSwitchValue";
    /// <summary>Only used when <see cref="ChannelKey"/> is empty.</summary>
    public int Channel { get; set; }
    /// <summary>Stable channel key; preferred over <see cref="Channel"/>.
    /// See <see cref="PowerBoxTarget.Resolve"/>.</summary>
    public string ChannelKey { get; set; } = "";
    public double Value { get; set; }

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var pb = ctx.Equipment.Switch ?? throw new InvalidOperationException("No power box connected");
        await pb.SetValueAsync(PowerBoxTarget.Resolve(pb, ChannelKey, Channel), Value, ct);
    }
}
