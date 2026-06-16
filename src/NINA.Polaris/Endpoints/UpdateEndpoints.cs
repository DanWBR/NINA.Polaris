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

/// <summary>Self-update endpoints (SBC .deb installs). Check GitHub releases and,
/// on request with the user's sudo password, download + install the new .deb.</summary>
public static class UpdateEndpoints {
    public static void MapUpdateEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/update");

        // Is there a newer release for this host's architecture? Cached 30 min;
        // ?force=true bypasses the cache (used by the modal's "check again").
        group.MapGet("/check", async (UpdateService svc, bool? force, CancellationToken ct) =>
            Results.Ok(await svc.CheckAsync(force ?? false, ct)));

        // Download + install the latest .deb. The privileged install runs via
        // the on-demand polaris-self-update.service unit, which the polaris user
        // is authorized to start passwordless (PolicyKit). Returns 200 once the
        // install has been launched (the service then restarts and the client
        // polls /api/system/status for the new version), 400 on a setup problem.
        group.MapPost("/install", async (UpdateService svc, CancellationToken ct) => {
            var (ok, error) = await svc.InstallAsync(ct);
            return ok
                ? Results.Ok(new { started = true })
                : Results.BadRequest(new { error });
        });
    }
}
