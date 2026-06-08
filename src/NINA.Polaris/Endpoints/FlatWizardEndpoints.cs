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

public static class FlatWizardEndpoints {
    public static void MapFlatWizardEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/flatwizard");

        group.MapGet("/status", (FlatWizardService fw) => Results.Ok(new {
            state = fw.State.ToString().ToLowerInvariant(),
            progress = fw.Progress,
            lastError = fw.LastError
        }));

        group.MapGet("/trained", (FlatWizardService fw) => Results.Ok(fw.TrainedExposures));

        group.MapPost("/start", (FlatWizardRequest request, FlatWizardService fw) => {
            try {
                fw.Start(request);
                return Results.Ok(new { state = "running" });
            } catch (InvalidOperationException ex) {
                return Results.Conflict(new { error = ex.Message });
            } catch (ArgumentException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/abort", (FlatWizardService fw) => {
            fw.Abort();
            return Results.Ok(new { state = fw.State.ToString().ToLowerInvariant() });
        });
    }
}