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

/// <summary>HTTP surface for the on-demand ncnn GPU model-pack downloader.
/// Mirrors the DSO thumbnail-pack and DSS downloader endpoints.</summary>
public static class NcnnModelPackEndpoints {
    public static void MapNcnnModelPackEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/ai/ncnn-models");

        g.MapGet("/status", (NcnnModelPackService pack) => Results.Ok(pack.GetStatus()));

        g.MapPost("/download", (NcnnModelPackService pack) => {
            var started = pack.Start();
            return started
                ? Results.Accepted(value: pack.GetStatus())
                : Results.Conflict(new { error = "A GPU model-pack download is already running" });
        });

        g.MapPost("/cancel", (NcnnModelPackService pack) => {
            pack.Cancel();
            return Results.Ok(new { ok = true });
        });
    }
}
