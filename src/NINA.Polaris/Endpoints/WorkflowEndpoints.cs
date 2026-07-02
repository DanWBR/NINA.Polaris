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

using System.Text.Json;
using NINA.Polaris.Services.Workflow;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// Named store for STUDIO "Auto Workflow" definitions so a user can save a
/// post-processing sequence once and re-run it on other files.
///   GET    /api/workflow/defs          → { workflows: string[] }
///   GET    /api/workflow/defs/{name}   → raw workflow JSON (404 if missing)
///   PUT    /api/workflow/defs/{name}   → save (raw JSON body)
///   DELETE /api/workflow/defs/{name}   → { deleted: bool }
///
/// The document schema lives on the client (the step-type registry in app.js);
/// the server stores the JSON verbatim (see <see cref="WorkflowStore"/>).
/// </summary>
public static class WorkflowEndpoints {
    public static void MapWorkflowEndpoints(this IEndpointRouteBuilder app) {
        var g = app.MapGroup("/api/workflow");

        g.MapGet("/defs", (WorkflowStore store) =>
            Results.Ok(new { workflows = store.List() }));

        g.MapGet("/defs/{name}", (WorkflowStore store, string name) => {
            var json = store.Load(name);
            return json is null
                ? Results.NotFound(new { error = "workflow not found" })
                : Results.Content(json, "application/json");
        });

        g.MapPut("/defs/{name}", async (WorkflowStore store, string name, HttpRequest req) => {
            using var reader = new StreamReader(req.Body);
            var json = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(json))
                return Results.BadRequest(new { error = "empty body" });
            // Validate it parses as JSON before persisting so we never store junk.
            try { using var _ = JsonDocument.Parse(json); }
            catch (JsonException) { return Results.BadRequest(new { error = "body is not valid JSON" }); }
            try {
                store.Save(name, json);
                return Results.Ok(new { saved = name });
            } catch (ArgumentException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        g.MapDelete("/defs/{name}", (WorkflowStore store, string name) =>
            Results.Ok(new { deleted = store.Delete(name) }));
    }
}
