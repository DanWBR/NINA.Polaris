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

public class RotateToAngleInstruction : SequenceInstruction {
    public override string Type => "RotateToAngle";
    /// <summary>Sky position angle in degrees, 0 = north up.</summary>
    public double AngleDeg { get; set; }

    public override IReadOnlyList<string> Validate() =>
        (AngleDeg < 0 || AngleDeg >= 360) ? new[] { $"Angle out of range: {AngleDeg}" } : Array.Empty<string>();

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var r = ctx.Equipment.Rotator ?? throw new InvalidOperationException("No rotator connected");
        await r.MoveToAsync(AngleDeg, ct);
    }
}