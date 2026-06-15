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
using NINA.Polaris.Services.Plan;

namespace NINA.Polaris.Endpoints;

public static class PlanEndpoints {
    public static void MapPlanEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/plan");

        // ---- CRUD over the global plan library ----
        g.MapGet("/plans", (ProfileService profiles) =>
            Results.Ok(profiles.Active.Plans));

        g.MapGet("/plans/{id}", (string id, ProfileService profiles) => {
            var plan = profiles.Active.Plans.FirstOrDefault(p => p.Id == id);
            return plan == null ? Results.NotFound() : Results.Ok(plan);
        });

        g.MapPost("/plans", (ImagingPlan plan, ProfileService profiles) => {
            if (string.IsNullOrWhiteSpace(plan.Id)) plan.Id = Guid.NewGuid().ToString("N");
            profiles.Active.Plans.Add(plan);
            profiles.Save();
            return Results.Ok(plan);
        });

        g.MapPut("/plans/{id}", (string id, ImagingPlan plan, ProfileService profiles) => {
            var idx = profiles.Active.Plans.FindIndex(p => p.Id == id);
            if (idx < 0) return Results.NotFound();
            plan.Id = id;                       // keep the URL id authoritative
            profiles.Active.Plans[idx] = plan;
            profiles.Save();
            return Results.Ok(plan);
        });

        g.MapDelete("/plans/{id}", (string id, ProfileService profiles) => {
            var removed = profiles.Active.Plans.RemoveAll(p => p.Id == id);
            if (removed == 0) return Results.NotFound();
            profiles.Save();
            return Results.Ok(new { removed });
        });

        // ---- Run control ----
        g.MapPost("/{id}/start", (string id, ProfileService profiles, PlanRunnerService runner) => {
            var plan = profiles.Active.Plans.FirstOrDefault(p => p.Id == id);
            if (plan == null) return Results.NotFound();
            var (ok, error) = runner.StartPlan(plan);
            return ok ? Results.Ok(runner.GetStatus()) : Results.BadRequest(new { error });
        });

        g.MapPost("/stop", (PlanRunnerService runner) => {
            runner.StopPlan();
            return Results.Ok(runner.GetStatus());
        });

        g.MapGet("/status", (PlanRunnerService runner) => Results.Ok(runner.GetStatus()));

        // ---- Compile preview (validation + time estimate, no run) ----
        g.MapPost("/{id}/compile", (string id, ProfileService profiles, PlanCompilerService compiler) => {
            var plan = profiles.Active.Plans.FirstOrDefault(p => p.Id == id);
            if (plan == null) return Results.NotFound();
            var doc = compiler.Compile(plan);
            return Results.Ok(new {
                document = doc,
                validation = doc.Root.Validate(),
                estimateSeconds = compiler.EstimateSeconds(plan)
            });
        });

        // Compile preview straight from a posted (unsaved) plan body, so the UI
        // can show a live time estimate while editing without saving first.
        g.MapPost("/compile", (ImagingPlan plan, PlanCompilerService compiler) => {
            var doc = compiler.Compile(plan);
            return Results.Ok(new {
                validation = doc.Root.Validate(),
                estimateSeconds = compiler.EstimateSeconds(plan)
            });
        });
    }
}
