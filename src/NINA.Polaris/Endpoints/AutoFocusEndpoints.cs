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

using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

public static class AutoFocusEndpoints {
    public static void MapAutoFocusEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/autofocus");

        group.MapGet("/status", (AutoFocusService af) => {
            return Results.Ok(new {
                state = af.State.ToString().ToLowerInvariant(),
                progress = new {
                    steps = af.Progress.Steps,
                    currentSampleIndex = af.Progress.CurrentSampleIndex,
                    currentPosition = af.Progress.CurrentPosition,
                    lastHfr = af.Progress.LastHfr,
                    lastStarCount = af.Progress.LastStarCount,
                    points = af.Progress.Points,
                    startedAt = af.Progress.StartedAt,
                    mode = af.Progress.Mode,
                    phase = af.Progress.Phase
                },
                lastError = af.LastError
            });
        });

        group.MapGet("/result", (AutoFocusService af) => {
            if (af.LastResult == null) return Results.Ok(new { hasResult = false });
            return Results.Ok(new { hasResult = true, result = af.LastResult });
        });

        group.MapPost("/start", (AutoFocusRequest? request, AutoFocusService af) => {
            try {
                af.Start(request ?? new AutoFocusRequest());
                return Results.Ok(new { state = "running" });
            } catch (InvalidOperationException ex) {
                return Results.Conflict(new { error = ex.Message });
            } catch (ArgumentException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/abort", (AutoFocusService af) => {
            af.Abort();
            return Results.Ok(new { state = af.State.ToString().ToLowerInvariant() });
        });
    }
}