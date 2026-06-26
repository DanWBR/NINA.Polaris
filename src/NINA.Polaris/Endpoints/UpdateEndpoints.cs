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

        // List recent GitHub releases (cached 30 min) with this host's arch
        // asset resolved + each tagged newer/current/older. Powers the rollback
        // / version-history modal. ?max=N (default 15), ?force=true bypasses cache.
        group.MapGet("/releases", async (UpdateService svc, int? max, bool? force, CancellationToken ct) =>
            Results.Ok(await svc.ListReleasesAsync(max ?? 15, force ?? false, ct)));

        // Install (or roll back to) a SPECIFIC release by tag. The asset URL is
        // resolved server-side from the releases list, never taken from the
        // caller. No "must be newer" gate — apt runs with --allow-downgrades.
        group.MapPost("/install-version", async (UpdateService svc, InstallVersionRequest req, CancellationToken ct) => {
            var (ok, error) = await svc.InstallVersionAsync(req?.Tag ?? "", ct);
            return ok
                ? Results.Ok(new { started = true })
                : Results.BadRequest(new { error });
        });

        // Internet-free host facts (version + dpkg arch) the browser needs to
        // find the right release asset on GitHub for the offline-sideload flow.
        group.MapGet("/local-info", (UpdateService svc) => Results.Ok(svc.LocalInfo()));

        // Offline / relay install: the SBC has no internet but the client
        // (phone on 4G/5G) does, so the browser downloads the .deb from GitHub
        // and POSTs the raw bytes here. The expected size + SHA-256 (which the
        // browser also read from the GitHub API over TLS) come as headers so the
        // server can verify integrity before the privileged install runs.
        // Body is the raw .deb (application/octet-stream); Kestrel's 1 GB body
        // limit (Program.cs) covers the ~80 MB package.
        group.MapPost("/upload-deb", async (HttpRequest req, UpdateService svc, CancellationToken ct) => {
            long expectedSize = 0;
            if (req.Headers.TryGetValue("X-Expected-Size", out var sz))
                long.TryParse(sz.ToString(), out expectedSize);
            var sha = req.Headers.TryGetValue("X-Expected-Sha256", out var h) ? h.ToString() : null;
            // Explicit rollback opt-in: the offline path normally refuses a
            // package not newer than the running one; this header allows it for
            // a deliberate downgrade. Package-name + SHA-256 gates still apply.
            var allowDowngrade = req.Headers.TryGetValue("X-Allow-Downgrade", out var ad)
                && string.Equals(ad.ToString().Trim(), "true", StringComparison.OrdinalIgnoreCase);

            var (ok, error) = await svc.InstallFromUploadAsync(req.Body, expectedSize, sha, ct, allowDowngrade);
            return ok
                ? Results.Ok(new { started = true })
                : Results.BadRequest(new { error });
        });
    }

    /// <summary>Body of POST /api/update/install-version: the release tag to
    /// install (e.g. "0.84.5" or "v0.84.5"; a leading 'v' is tolerated).</summary>
    public record InstallVersionRequest(string Tag);
}
