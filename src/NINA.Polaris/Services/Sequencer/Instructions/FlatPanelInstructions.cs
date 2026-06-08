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

public class OpenFlatCoverInstruction : SequenceInstruction {
    public override string Type => "OpenFlatCover";
    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var d = ctx.Equipment.FlatDevice ?? throw new InvalidOperationException("No flat panel connected");
        await d.OpenCoverAsync(ct);
    }
}

public class CloseFlatCoverInstruction : SequenceInstruction {
    public override string Type => "CloseFlatCover";
    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var d = ctx.Equipment.FlatDevice ?? throw new InvalidOperationException("No flat panel connected");
        await d.CloseCoverAsync(ct);
    }
}

public class SetFlatBrightnessInstruction : SequenceInstruction {
    public override string Type => "SetFlatBrightness";
    /// <summary>0-100; driver-specific scaling beyond that.</summary>
    public int Brightness { get; set; } = 50;

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var d = ctx.Equipment.FlatDevice ?? throw new InvalidOperationException("No flat panel connected");
        await d.SetBrightnessAsync(Brightness, ct);
    }
}

public class ToggleFlatLightInstruction : SequenceInstruction {
    public override string Type => "ToggleFlatLight";
    public bool On { get; set; } = true;
    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var d = ctx.Equipment.FlatDevice ?? throw new InvalidOperationException("No flat panel connected");
        await d.SetLightAsync(On, ct);
    }
}