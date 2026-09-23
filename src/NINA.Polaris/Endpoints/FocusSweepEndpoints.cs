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

using NINA.Polaris.Services.Focus;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// The FOCUS tab's per-filter focus run: measure every selected filter once,
/// then write the offsets to the rig on the operator's say-so.
///
/// <para>The offsets in <c>/result</c> are computed on the server, by the same
/// code the write path uses, so the preview in the tab cannot drift from what
/// Apply stores.</para>
/// </summary>
public static class FocusSweepEndpoints {
    public static void MapFocusSweepEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/focus-sweep");

        group.MapGet("/status", (FilterFocusSweepService svc) => Results.Ok(new {
            state = svc.State.ToString().ToLowerInvariant(),
            phase = svc.Progress.Phase,
            lastError = svc.LastError,
            hasPendingResults = svc.HasPendingResults,
            appliedAt = svc.AppliedAt,
            progress = svc.Progress
        }));

        group.MapGet("/result", (FilterFocusSweepService svc) => Results.Ok(new {
            hasResult = svc.Progress.Results.Count > 0,
            reference = svc.Progress.ReferenceFilter,
            hasPendingResults = svc.HasPendingResults,
            appliedAt = svc.AppliedAt,
            results = svc.Progress.Results
        }));

        group.MapPost("/start", (FilterFocusSweepRequest? request, FilterFocusSweepService svc) => {
            try {
                svc.Start(request);
                return Results.Ok(new { state = "running" });
            } catch (InvalidOperationException ex) {
                return Results.Conflict(new { error = ex.Message });
            } catch (ArgumentException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/abort", (FilterFocusSweepService svc) => {
            svc.Abort();
            return Results.Ok(new { state = svc.State.ToString().ToLowerInvariant() });
        });

        group.MapPost("/apply", (FocusSweepApplyRequest? request, FilterFocusSweepService svc) => {
            try {
                var outcome = svc.Apply(request?.Filters, request?.Reference);
                return Results.Ok(new {
                    applied = outcome.Applied,
                    offsets = outcome.Offsets,
                    reference = outcome.Reference
                });
            } catch (InvalidOperationException ex) {
                return Results.Conflict(new { error = ex.Message });
            } catch (ArgumentException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/discard", (FilterFocusSweepService svc) => {
            try {
                return Results.Ok(new { cleared = svc.Discard() });
            } catch (InvalidOperationException ex) {
                return Results.Conflict(new { error = ex.Message });
            }
        });
    }
}

/// <summary>Which measured filters to write, and what to measure them against.
/// Both optional: no filters means every measured one, no reference lets the
/// run's own resolution stand.</summary>
public record FocusSweepApplyRequest(List<string>? Filters, string? Reference);
