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
using NINA.Polaris.Services.Storage;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// REST surface for the auto-push-to-network-storage feature
/// (<see cref="StoragePushService"/>): read/write config, test connectivity,
/// retry failed uploads. The password is never returned — GET reports only
/// whether one is set; PUT keeps the stored password when the field is blank.
/// </summary>
public static class StorageEndpoints {
    public static void MapStorageEndpoints(this IEndpointRouteBuilder app) {
        var group = app.MapGroup("/api/storage");

        group.MapGet("/config", (ProfileService profiles) => {
            var p = profiles.Active;
            return Results.Ok(new {
                enabled        = p.StoragePushEnabled,
                kind           = p.StorageKind,
                host           = p.StorageHost,
                port           = p.StoragePort,
                share          = p.StorageShare,
                basePath       = p.StorageBasePath,
                domain         = p.StorageDomain,
                username       = p.StorageUsername,
                hasPassword    = !string.IsNullOrEmpty(p.StoragePassword),
                linkSharePercent = p.StoragePushLinkSharePercent,
                lastTestResult = p.StorageLastTestResult
            });
        });

        group.MapPut("/config", (StorageConfigRequest req, ProfileService profiles) => {
            if (req == null) return Results.BadRequest(new { error = "missing body" });
            var p = profiles.Active;
            p.StoragePushEnabled = req.Enabled;
            p.StorageKind     = NormalizeKind(req.Kind);
            p.StorageHost     = (req.Host ?? "").Trim();
            p.StoragePort     = req.Port > 0 ? req.Port : 0;
            p.StorageShare    = (req.Share ?? "").Trim();
            p.StorageBasePath = (req.BasePath ?? "").Trim();
            p.StorageDomain   = (req.Domain ?? "").Trim();
            p.StorageUsername = (req.Username ?? "").Trim();
            // Empty password = keep the stored one (the GET never sends it back).
            if (!string.IsNullOrEmpty(req.Password)) p.StoragePassword = req.Password;
            // 0 from an older client means "field absent"; keep what is stored
            // rather than reading it as "never transfer".
            if (req.LinkSharePercent > 0)
                p.StoragePushLinkSharePercent = Math.Clamp(req.LinkSharePercent, 10, 100);
            profiles.Save();
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/test", async (ProfileService profiles, StoragePushService push, CancellationToken ct) => {
            var cfg = StorageConfig.FromProfile(profiles.Active);
            var (ok, message) = await push.TestConnectionAsync(cfg, ct);
            profiles.Active.StorageLastTestResult = message;
            profiles.Save();
            return Results.Ok(new { ok, message });
        });

        group.MapPost("/retry", (StoragePushService push) => {
            var n = push.RetryFailed();
            return Results.Ok(new { ok = true, requeued = n });
        });

        // One-way backfill: enqueue the whole capture tree so files captured
        // while the share was off / unreachable get pushed now. The targets skip
        // anything already present with the same size, so it only copies what's
        // missing. Enumeration runs off the request thread (a large archive can
        // take a moment to walk); the paced lanes handle the actual transfer.
        group.MapPost("/backfill", async (StoragePushService push) => {
            if (!push.Enabled)
                return Results.BadRequest(new { error = "Auto-push is disabled." });
            var n = await Task.Run(() => push.Backfill());
            return Results.Ok(new { ok = true, queued = n });
        });

        // SHARESYNC-2: stop the file currently transferring (keeps the queue).
        group.MapPost("/abort", (StoragePushService push) => {
            push.AbortCurrent();
            return Results.Ok(new { ok = true });
        });

        // SHARESYNC-2: drop everything still queued (and the in-flight file).
        group.MapPost("/clear", (StoragePushService push) => {
            push.ClearQueue();
            return Results.Ok(new { ok = true });
        });
    }

    private static string NormalizeKind(string? kind) =>
        (kind ?? "smb").Trim().ToLowerInvariant() switch {
            "sftp"  => "sftp",
            "local" => "local",
            _       => "smb"
        };

    public record StorageConfigRequest(
        bool Enabled, string? Kind, string? Host, int Port, string? Share,
        string? BasePath, string? Domain, string? Username, string? Password,
        int LinkSharePercent = 0);
}
