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
using NINA.Polaris.Services.External;
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
                lastTestResult = p.StorageLastTestResult,
                remoteName     = p.StorageRemoteName,
                bandwidthLimit = p.StorageBandwidthLimit,
                pushOnSessionEnd = p.StoragePushOnSessionEnd
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
            // Password semantics, matching the relay token: null keeps the
            // stored one (the GET never sends it back), an empty string clears
            // it, anything else replaces it. Until this change there was no way
            // to clear a stored password at all.
            if (req.Password != null) p.StoragePassword = req.Password;
            p.StorageRemoteName = (req.RemoteName ?? "").Trim();
            if (req.BandwidthLimit != null) {
                var bw = req.BandwidthLimit.Trim();
                // Validated against the offered list, because the value becomes
                // a process argument.
                p.StorageBandwidthLimit = RcloneArgs.IsValidBandwidth(bw) ? bw : "";
            }
            p.StoragePushOnSessionEnd = req.PushOnSessionEnd;
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

        // Send one folder now. The operator picks it in FILES; everything under
        // it that the target does not already have goes into the same queue the
        // automatic push uses, so it inherits the pacing and the breaker.
        group.MapPost("/push-folder", async (PushFolderRequest req, StoragePushService push,
                                             FileBrowserService files, ProfileService profiles) => {
            if (req == null || string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new { error = "Select one folder to send." });
            if (!push.Enabled)
                return Results.BadRequest(new { error = "Auto-push is disabled." });
            string full;
            try { full = files.ResolveSafe(req.Path, mustExist: true); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
            if (!Directory.Exists(full))
                return Results.BadRequest(new { error = "Select one folder to send." });
            var root = profiles.Active?.ImageOutputDir ?? "";
            if (!StoragePushService.IsUnderRoot(root, full))
                return Results.BadRequest(new { error =
                    "That folder is outside the capture folder, so it cannot be mirrored to the remote." });
            var n = await Task.Run(() => push.EnqueueTree(full));
            return Results.Ok(new { ok = true, queued = n });
        });

        // ---- rclone: the binary, and the remotes it knows about ----

        group.MapGet("/rclone", async (RcloneService rclone, CancellationToken ct) => {
            var available = rclone.IsAvailable;
            return Results.Ok(new {
                available,
                binaryPath = rclone.BinaryPath,
                version = available ? await rclone.VersionAsync(ct) : null,
                // Ubuntu packages 1.60, old enough that an aborted upload can
                // leave a truncated file under the real name. The card says so
                // rather than letting the operator discover it.
                partialUploads = available && await rclone.SupportsPartialSuffixAsync(ct),
                configPath = rclone.ConfigPath,
                // Where we looked, so an operator with rclone somewhere unusual
                // can see why we did not find it.
                candidates = rclone.EnumerateBinaryCandidates()
                    .Select(c => new { description = c.Description, path = c.Path, exists = c.Exists }),
                remotes = available
                    ? (await rclone.ListRemotesAsync(ct)).Select(r => new { name = r.Name, type = r.Type })
                    : Enumerable.Empty<object>(),
                providers = RcloneArgs.ProviderTypes.Select(t => new {
                    id = t, needsBrowser = RcloneArgs.NeedsBrowserSignIn(t)
                }),
                bandwidthChoices = RcloneArgs.BandwidthChoices
            });
        });

        // The command the operator runs on a machine that has a browser. Built
        // on the host so the provider id cannot drift from the allowlist.
        group.MapGet("/rclone/authorize-command", (string? type) => {
            if (!RcloneArgs.IsProviderType(type))
                return Results.BadRequest(new { error = "Unknown provider." });
            return Results.Ok(new { command = RcloneArgs.AuthorizeCommand(type!) });
        });

        group.MapPost("/rclone/remotes", async (RcloneRemoteRequest req, RcloneService rclone,
                                                ProfileService profiles, CancellationToken ct) => {
            if (req == null) return Results.BadRequest(new { error = "missing body" });

            string name = (req.Name ?? "").Trim();
            string type = (req.Type ?? "").Trim().ToLowerInvariant();
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            bool obscure = true;

            if (!string.IsNullOrWhiteSpace(req.Paste)) {
                if (!RcloneConfigPaste.TryParse(req.Paste, out var parsed, out var perr))
                    return Results.BadRequest(new { error = perr });
                if (parsed.Name != null && name.Length == 0) name = parsed.Name;
                if (parsed.Type != null) type = parsed.Type;
                foreach (var (k, v) in parsed.Values) values[k] = v;
                // A section copied out of a working rclone.conf already carries
                // an obscured password; obscuring it again breaks it.
                obscure = !parsed.AlreadyObscured;
            }
            if (req.Values != null) foreach (var (k, v) in req.Values) values[k] = v;

            if (!RcloneArgs.IsValidRemoteName(name, out var why))
                return Results.BadRequest(new { error = why });
            if (!RcloneArgs.IsProviderType(type))
                return Results.BadRequest(new { error = "Pick a provider." });

            var (ok, message) = await rclone.CreateRemoteAsync(name, type, values, obscure, ct);
            if (!ok) return Results.BadRequest(new { error = message });

            // The credential lives in rclone.conf, which StorageConfig cannot
            // see, so bump the revision to make the push lanes drop a connection
            // built against the old one.
            var p = profiles.Active;
            if (p != null) { p.StorageRemoteRevision++; profiles.Save(); }
            return Results.Ok(new { ok = true, message, name });
        });

        group.MapDelete("/rclone/remotes/{name}", async (string name, RcloneService rclone,
                                                         ProfileService profiles, CancellationToken ct) => {
            var (ok, message) = await rclone.DeleteRemoteAsync(name, ct);
            if (!ok) return Results.BadRequest(new { error = message });
            var p = profiles.Active;
            if (p != null) {
                p.StorageRemoteRevision++;
                if (string.Equals(p.StorageRemoteName, name, StringComparison.OrdinalIgnoreCase))
                    p.StorageRemoteName = "";
                profiles.Save();
            }
            return Results.Ok(new { ok = true, message });
        });

        // Probe one remote without disturbing the configured destination.
        group.MapPost("/rclone/remotes/{name}/test", async (string name, RcloneService rclone,
                                                            ProfileService profiles,
                                                            IStorageTargetFactory factory,
                                                            CancellationToken ct) => {
            if (!RcloneArgs.IsValidRemoteName(name, out var why))
                return Results.BadRequest(new { error = why });
            var cfg = StorageConfig.FromProfile(profiles.Active!) with {
                Kind = "rclone", RemoteName = name
            };
            using var target = factory.Create("rclone");
            try { await target.ConnectAsync(cfg, ct); }
            catch (Exception ex) { return Results.Ok(new { ok = false, message = ex.Message }); }
            var (ok, message) = await target.TestAsync(cfg, ct);
            return Results.Ok(new { ok, message });
        });
    }

    private static string NormalizeKind(string? kind) =>
        (kind ?? "smb").Trim().ToLowerInvariant() switch {
            "sftp"   => "sftp",
            "local"  => "local",
            "rclone" => "rclone",
            _        => "smb"
        };

    public record StorageConfigRequest(
        bool Enabled, string? Kind, string? Host, int Port, string? Share,
        string? BasePath, string? Domain, string? Username, string? Password,
        int LinkSharePercent = 0, string? RemoteName = null, string? BandwidthLimit = null,
        bool PushOnSessionEnd = false);

    public record PushFolderRequest(string Path);
    public record RcloneRemoteRequest(string? Name, string? Type,
                                      Dictionary<string, string>? Values, string? Paste);
}
