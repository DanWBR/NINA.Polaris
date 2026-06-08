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

/// <summary>Switch the filter wheel to a named filter (or numeric position).</summary>
public class SwitchFilterInstruction : SequenceInstruction {
    public override string Type => "SwitchFilter";

    /// <summary>Filter name as configured in the wheel. Takes precedence over <see cref="Position"/>.</summary>
    public string? FilterName { get; set; }

    /// <summary>1-based filter position fallback when <see cref="FilterName"/> isn't set.</summary>
    public int? Position { get; set; }

    public override IReadOnlyList<string> Validate() {
        if (string.IsNullOrWhiteSpace(FilterName) && !Position.HasValue)
            return new[] { "Provide either FilterName or Position" };
        return Array.Empty<string>();
    }

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var fw = ctx.Equipment.FilterWheel ?? throw new InvalidOperationException("No filter wheel connected");
        if (!string.IsNullOrEmpty(FilterName)) {
            await fw.SetFilterByNameAsync(FilterName, ct);
        } else if (Position.HasValue) {
            await fw.SetPositionAsync(Position.Value, ct);
        }
    }
}