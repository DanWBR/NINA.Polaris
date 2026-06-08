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
/// The default container: runs children in array order, each child finishing
/// before the next starts. Honours triggers between every step and supports
/// <see cref="SequenceContainer.IsLoop"/> + conditions for "do block until X".
/// </summary>
public class SequentialContainer : SequenceContainer {
    public override string Type => "Sequential";

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        do {
            for (int i = 0; i < Items.Count; i++) {
                if (ctx.AbortRequested) return;
                ct.ThrowIfCancellationRequested();

                await EvaluateTriggersAsync(ctx, ct);
                if (ctx.AbortRequested) return;

                var item = Items[i];
                if (item is SequenceEntityBase b) b.ResetRuntimeState();
                item.Status = SequenceEntityStatus.Running;
                item.StartedAt = DateTime.UtcNow;
                try {
                    await item.ExecuteAsync(ctx, ct);
                    item.Status = SequenceEntityStatus.Completed;
                } catch (OperationCanceledException) {
                    item.Status = SequenceEntityStatus.Skipped;
                    throw;
                } catch (Exception ex) {
                    item.Status = SequenceEntityStatus.Failed;
                    item.Error = ex.Message;
                    ctx.Logger.LogWarning(ex, "Sequential step {Name} failed", item.Name);
                    throw;
                } finally {
                    item.FinishedAt = DateTime.UtcNow;
                }
            }
        } while (IsLoop && !ctx.AbortRequested && await AllConditionsHoldAsync(ctx, ct));
    }
}