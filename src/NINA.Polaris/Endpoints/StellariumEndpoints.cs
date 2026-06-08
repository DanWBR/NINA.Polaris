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

public static class StellariumEndpoints {
    public static void MapStellariumEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/stellarium");

        group.MapGet("/target", async (string? host, int? port, StellariumClient client) => {
            var h = string.IsNullOrWhiteSpace(host) ? "localhost" : host!;
            var p = port ?? 8090;
            try {
                var target = await client.GetSelectedObjectAsync(h, p);
                if (target == null) return Results.NotFound(new { error = "No object currently selected in Stellarium" });
                return Results.Ok(target);
            } catch (TimeoutException ex) {
                return Results.Problem(ex.Message);
            } catch (InvalidOperationException ex) {
                return Results.Problem(ex.Message);
            }
        });

        group.MapGet("/view", async (string? host, int? port, StellariumClient client) => {
            var h = string.IsNullOrWhiteSpace(host) ? "localhost" : host!;
            var p = port ?? 8090;
            var view = await client.GetViewAsync(h, p);
            if (view == null) return Results.NotFound(new { error = "Stellarium view query failed" });
            return Results.Ok(view);
        });
    }
}