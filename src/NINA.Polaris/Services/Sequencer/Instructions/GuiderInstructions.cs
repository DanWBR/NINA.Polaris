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

public class StartGuidingInstruction : SequenceInstruction {
    public override string Type => "StartGuiding";
    public double SettlePixels { get; set; } = 1.5;
    public int SettleTimeSeconds { get; set; } = 10;
    public int SettleTimeoutSeconds { get; set; } = 40;
    public bool Recalibrate { get; set; } = false;

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        if (!ctx.PHD2.IsConnected) throw new InvalidOperationException("PHD2 not connected");
        await ctx.PHD2.StartGuidingAsync(SettlePixels, SettleTimeSeconds, SettleTimeoutSeconds, Recalibrate);
    }
}

public class StopGuidingInstruction : SequenceInstruction {
    public override string Type => "StopGuiding";
    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        if (!ctx.PHD2.IsConnected) return;
        await ctx.PHD2.StopAsync();
    }
}

public class DitherInstruction : SequenceInstruction {
    public override string Type => "Dither";
    public double Pixels { get; set; } = 5.0;
    public bool RaOnly { get; set; } = false;
    public double SettlePixels { get; set; } = 1.5;
    public int SettleTimeSeconds { get; set; } = 10;
    public int SettleTimeoutSeconds { get; set; } = 40;

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        if (!ctx.PHD2.IsConnected) throw new InvalidOperationException("PHD2 not connected");
        await ctx.PHD2.DitherAsync(Pixels, RaOnly, SettlePixels, SettleTimeSeconds, SettleTimeoutSeconds);
    }
}

public class AutoSelectStarInstruction : SequenceInstruction {
    public override string Type => "AutoSelectStar";
    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        if (!ctx.PHD2.IsConnected) throw new InvalidOperationException("PHD2 not connected");
        await ctx.PHD2.AutoSelectStarAsync();
    }
}