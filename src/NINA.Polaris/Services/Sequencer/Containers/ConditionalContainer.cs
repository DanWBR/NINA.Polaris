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

namespace NINA.Polaris.Services.Sequencer.Containers;

/// <summary>
/// Runs its children EXACTLY ONCE, and only if its predicate holds. The
/// predicate is the AND of the container's <see cref="SequenceContainer.Conditions"/>
/// evaluated once at entry (so "run this block only if the Sun is below -12°",
/// "only while it's safe", etc.). With no conditions it always runs once.
///
/// This is the run-once counterpart to a looping <c>Sequential</c> container
/// (NINA desktop calls it a Conditional/Instruction-Set container). Unlike a
/// loop, the predicate is NOT re-checked between items — it's a gate, not a
/// loop exit. For per-item re-checking use a Sequential container with IsLoop.
/// </summary>
public class ConditionalContainer : SequenceContainer {
    public override string Type => "Conditional";

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        if (Conditions.Count > 0 && !await AllConditionsHoldAsync(ctx, ct)) {
            ctx.Logger.LogInformation(
                "Conditional container '{Name}': predicate is false, skipping {Count} item(s).",
                Name, Items.Count);
            return;
        }
        await RunChildrenOnceAsync(ctx, ct);
    }
}
