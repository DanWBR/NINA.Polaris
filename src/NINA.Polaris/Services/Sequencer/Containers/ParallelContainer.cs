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
/// Runs all children concurrently and waits for every one to complete.
/// Triggers + conditions don't make as much sense here so we evaluate
/// triggers only at start. If any child throws, the others get the
/// shared CancellationToken and are expected to wind down promptly.
/// </summary>
public class ParallelContainer : SequenceContainer {
    public override string Type => "Parallel";

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        await EvaluateTriggersAsync(ctx, ct);
        if (ctx.AbortRequested || Items.Count == 0) return;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = Items.Select(async item => {
            try {
                // RunChildAsync applies the child's Attempts + ErrorBehavior and
                // cascades triggers into child containers. ContinueOnError /
                // SkipBlock are swallowed (siblings keep running); only AbortRun
                // re-throws, which we use to cancel the other branches.
                await RunChildAsync(item, ctx, linked.Token);
            } catch (OperationCanceledException) {
                throw;
            } catch {
                linked.Cancel(); // make siblings hang up
                throw;
            }
        }).ToArray();

        await Task.WhenAll(tasks);
    }
}