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

namespace NINA.Polaris.Services;

/// <summary>
/// Mutable per-run state for the filter switcher: which filter is currently
/// loaded and what focuser offset is currently applied for it. Both AUTORUN
/// (one instance on <c>SequenceEngine</c>) and the tree sequencer (kept in the
/// run's <c>SequenceContext.Scratch</c>) use this so the focuser offset is
/// applied as a DELTA between filters rather than re-added every frame.
/// </summary>
public sealed class FilterState {
    /// <summary>Filter the wheel is currently on, null = unknown (nothing applied yet).</summary>
    public string? CurrentFilter { get; set; }
    /// <summary>Focuser offset currently applied for <see cref="CurrentFilter"/> (steps).</summary>
    public int AppliedOffset { get; set; }
}

/// <summary>
/// Switches the filter wheel to a named filter and applies the per-filter
/// focuser offset as a <b>relative delta</b> from the offset that was applied
/// for the previous filter, so repeated captures don't accumulate the absolute
/// offset. No-ops when the requested filter is already loaded, when no filter
/// is requested, or when no filter wheel is connected. All hardware failures
/// are caught + logged; a filter/focuser glitch never aborts the run.
/// </summary>
public static class FilterSwitcher {
    public static async Task ApplyAsync(
        IFilterWheel? wheel,
        IFocuser? focuser,
        IReadOnlyDictionary<string, int>? offsets,
        string? targetFilter,
        FilterState state,
        ILogger logger,
        CancellationToken ct) {

        if (string.IsNullOrWhiteSpace(targetFilter)) return;
        // Already on this filter (and its offset already applied) — nothing to do.
        if (string.Equals(state.CurrentFilter, targetFilter, StringComparison.OrdinalIgnoreCase))
            return;

        // 1. Move the wheel (best effort).
        if (wheel is { IsConnected: true }) {
            try {
                await wheel.SetFilterByNameAsync(targetFilter, ct);
                logger.LogInformation("Filter → {Filter}", targetFilter);
            } catch (OperationCanceledException) { throw; } catch (Exception ex) {
                logger.LogWarning(ex, "Filter move to '{Filter}' failed, continuing", targetFilter);
            }
        }

        // 2. Apply the focuser offset as a delta from what's currently applied.
        int newOffset = (offsets != null && offsets.TryGetValue(targetFilter, out var o)) ? o : 0;
        int delta = newOffset - state.AppliedOffset;
        if (focuser is { IsConnected: true } && delta != 0) {
            try {
                await focuser.MoveRelativeAsync(delta, ct);
                logger.LogInformation("Filter offset for {Filter}: {New} steps (moved {Delta:+#;-#;0})",
                    targetFilter, newOffset, delta);
                state.AppliedOffset = newOffset;
            } catch (OperationCanceledException) { throw; } catch (Exception ex) {
                logger.LogWarning(ex, "Focuser offset move for '{Filter}' failed, continuing", targetFilter);
                // Don't update AppliedOffset on failure so the next switch retries the delta.
            }
        } else {
            // No focuser (or no change): still record the baseline so the first
            // real move computes the correct delta.
            state.AppliedOffset = newOffset;
        }

        state.CurrentFilter = targetFilter;
    }
}
