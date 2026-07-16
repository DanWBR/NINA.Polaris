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

/// <summary>HTTP surface for the on-demand DSO thumbnail-pack downloader. The
/// Settings tab polls /status, kicks /download, and can /cancel a running job.
/// Mirrors the DSS downloader endpoints.</summary>
public static class DsoThumbPackEndpoints {
    public static void MapDsoThumbPackEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/sky/dso-thumbs");

        g.MapGet("/status", (DsoThumbPackService pack) => Results.Ok(pack.GetStatus()));

        g.MapPost("/download", (DsoThumbPackService pack) => {
            var started = pack.Start();
            return started
                ? Results.Accepted(value: pack.GetStatus())
                : Results.Conflict(new { error = "A thumbnail-pack download is already running" });
        });

        g.MapPost("/cancel", (DsoThumbPackService pack) => {
            pack.Cancel();
            return Results.Ok(new { ok = true });
        });
    }
}
