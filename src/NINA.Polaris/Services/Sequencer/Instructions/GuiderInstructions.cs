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

using Microsoft.Extensions.Logging;

namespace NINA.Polaris.Services.Sequencer.Instructions;

public class StartGuidingInstruction : SequenceInstruction {
    public override string Type => "StartGuiding";
    public double SettlePixels { get; set; } = 1.5;
    public int SettleTimeSeconds { get; set; } = 10;
    public int SettleTimeoutSeconds { get; set; } = 40;
    public bool Recalibrate { get; set; } = false;

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        if (!ctx.Guider.IsConnected) throw new InvalidOperationException("Guider not connected");
        await ctx.Guider.StartGuidingAsync(SettlePixels, SettleTimeSeconds, SettleTimeoutSeconds, Recalibrate);
    }
}

public class StopGuidingInstruction : SequenceInstruction {
    public override string Type => "StopGuiding";
    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        if (!ctx.Guider.IsConnected) return;
        await ctx.Guider.StopAsync();
    }
}

/// <summary>One-shot dither. A pure marker now — the amount + settle parameters
/// come from the global dither config (<c>EquipmentProfile.DitherProfile</c>), so
/// dither is tuned in one place. Legacy per-node fields are kept for
/// serialization back-compat but are no longer read.</summary>
public class DitherInstruction : SequenceInstruction {
    public override string Type => "Dither";
    [System.Text.Json.Serialization.JsonIgnore] public double Pixels { get; set; } = 5.0;
    [System.Text.Json.Serialization.JsonIgnore] public bool RaOnly { get; set; } = false;
    [System.Text.Json.Serialization.JsonIgnore] public double SettlePixels { get; set; } = 1.5;
    [System.Text.Json.Serialization.JsonIgnore] public int SettleTimeSeconds { get; set; } = 10;
    [System.Text.Json.Serialization.JsonIgnore] public int SettleTimeoutSeconds { get; set; } = 40;

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        if (!ctx.Guider.IsConnected) throw new InvalidOperationException("Guider not connected");
        var g = ctx.Profiles.ActiveEquipmentProfile?.EffectiveDither
                ?? new NINA.Polaris.Services.DitherSettings();
        await ctx.Guider.DitherAsync(g.Pixels, g.RaOnly, g.SettlePixels, g.SettleTime, g.SettleTimeout);
    }
}

public class AutoSelectStarInstruction : SequenceInstruction {
    public override string Type => "AutoSelectStar";
    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        if (!ctx.Guider.IsConnected) throw new InvalidOperationException("Guider not connected");
        await ctx.Guider.AutoSelectStarAsync();
    }
}

/// <summary>
/// Change the NATIVE guide camera gain mid-sequence. Writes the active rig's
/// <c>NativeGuideGain</c> (clamped to the guide camera's real range when one is
/// connected); the native guide loop reads it on the very next exposure, so the
/// change takes effect live without restarting guiding.
///
/// Only the native guider honours this — when guiding through PHD2 the guide
/// camera is owned by PHD2, so this only affects the native guider (logged).
/// </summary>
public class SetGuiderGainInstruction : SequenceInstruction {
    public override string Type => "SetGuiderGain";

    /// <summary>Target gain in the guide camera's native units.</summary>
    public int Gain { get; set; } = 40;

    public override IReadOnlyList<string> Validate() =>
        Gain < 0 ? new[] { "Gain must be >= 0" } : Array.Empty<string>();

    public override Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var rig = ctx.Profiles.ActiveEquipmentProfile
            ?? throw new InvalidOperationException("No active rig");

        var gain = Gain;
        var cam = ctx.Equipment.GuideCamera;
        if (cam != null && cam.GainMax > cam.GainMin && cam.GainMax > 0)
            gain = Math.Clamp(gain, cam.GainMin, cam.GainMax);

        ctx.Profiles.UpdateEquipmentProfile(rig.Id, r => r.NativeGuideGain = gain);
        ctx.Logger.LogInformation(
            "Guide camera gain set to {Gain} (requested {Requested}); applies on the next guide frame.",
            gain, Gain);

        if (ctx.PHD2.IsConnected)
            ctx.Logger.LogWarning(
                "PHD2 is connected — the guide camera gain is owned by PHD2; this change only "
                + "affects the native guider.");
        return Task.CompletedTask;
    }
}