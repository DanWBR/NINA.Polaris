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

using Microsoft.AspNetCore.Http;
using NINA.Polaris.Services.External;

namespace NINA.Polaris.Endpoints;

/// <summary>HTTP surface for the on-demand DSS Color HiPS downloader. The
/// Settings tab polls /status, kicks /download for a chosen max order, and
/// can /cancel a running job.</summary>
public static class DssEndpoints {
    public static void MapDssEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/sky/dss");

        g.MapGet("/status", (DssDownloadService dss) => Results.Ok(dss.GetStatus()));

        g.MapPost("/download", (DssDownloadService dss, DssDownloadRequest req) => {
            if (req.MaxOrder < 0 || req.MaxOrder > 6)
                return Results.BadRequest(new { error = "maxOrder must be 0-6" });
            var started = dss.Start(req.MaxOrder);
            return started
                ? Results.Accepted(value: dss.GetStatus())
                : Results.Conflict(new { error = "A DSS download is already running" });
        });

        g.MapPost("/cancel", (DssDownloadService dss) => {
            dss.Cancel();
            return Results.Ok(new { ok = true });
        });
    }

    public record DssDownloadRequest(int MaxOrder);
}
