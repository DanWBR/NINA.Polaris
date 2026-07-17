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

using NINA.Polaris.Services.External;

namespace NINA.Polaris.Endpoints;

/// <summary>HTTP surface for the local "On this server (SBC)" Canopus backend:
/// the Settings assistant panel polls /status, downloads the model+runtime, and
/// starts/stops the local server. The chat itself is served through the
/// /canopus/* reverse-proxy, not here.</summary>
public static class CanopusEndpoints {
    public static void MapCanopusEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/canopus");

        g.MapGet("/status", (CanopusServerService server, CanopusModelService models) =>
            Results.Ok(new {
                rid = CanopusModelService.Rid,
                running = server.Running,
                llamaRunning = server.LlamaRunning,
                agentRunning = server.AgentRunning,
                lastError = server.LastError,
                unavailableReason = server.UnavailableReason,
                serverDirPresent = server.ServerDirPresent,
                modelPresent = server.ModelPresent,
                runtimePresent = server.RuntimePresent,
                lastHealthCheckAt = server.LastHealthCheckAt,
                download = models.GetStatus()
            }));

        g.MapPost("/start", async (CanopusServerService server) => {
            var ok = await server.StartAsync();
            return ok ? Results.Ok(new { ok = true })
                      : Results.Conflict(new { error = server.LastError ?? "failed to start" });
        });

        g.MapPost("/stop", async (CanopusServerService server) => {
            await server.StopAsync();
            return Results.Ok(new { ok = true });
        });

        // Download the GGUF model + the arch-matched llama.cpp runtime.
        g.MapPost("/model/download", (CanopusModelService models) =>
            models.Start()
                ? Results.Accepted(value: models.GetStatus())
                : Results.Conflict(new { error = "A download is already running" }));

        g.MapGet("/model/download-status", (CanopusModelService models) =>
            Results.Ok(models.GetStatus()));

        g.MapPost("/model/cancel", (CanopusModelService models) => {
            models.Cancel();
            return Results.Ok(new { ok = true });
        });
    }
}
