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
        // A loop with no conditions runs until the sequence is stopped. That's
        // a legitimate "shoot until dawn / until I press Stop" pattern, so we
        // don't treat it as a validation error, but we surface it once in the
        // log so an accidental IsLoop toggle is diagnosable.
        if (IsLoop && Conditions.Count == 0)
            ctx.Logger.LogInformation(
                "Sequential container '{Name}' loops with no exit condition; "
                + "it will repeat until the sequence is stopped.", Name);
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