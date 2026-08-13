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

namespace NINA.Polaris.Services.Storage;

/// <summary>
/// Connection parameters for a network-storage target, snapshotted from the
/// active <see cref="UserProfile"/>. Immutable so a queued upload always uses
/// the settings that were live when it was enqueued.
/// </summary>
public sealed record StorageConfig(
    string Kind,        // "smb" | "sftp" | "local"
    string Host,
    int Port,           // 0 => provider default
    string Share,       // SMB share name
    string BasePath,    // SFTP base dir OR local/mounted path
    string Domain,      // SMB workgroup/domain (optional)
    string Username,
    string Password,
    /// <summary>Share of the uplink the push may take, as a percent; 100 means
    /// no pacing. Carried on the config so a target paces itself without
    /// reaching back into the profile mid-transfer.</summary>
    int LinkSharePercent = 100) {

    public static StorageConfig FromProfile(UserProfile p) => new(
        Kind:     (p.StorageKind ?? "smb").Trim().ToLowerInvariant(),
        Host:     (p.StorageHost ?? "").Trim(),
        Port:     p.StoragePort,
        Share:    (p.StorageShare ?? "").Trim(),
        BasePath: (p.StorageBasePath ?? "").Trim(),
        Domain:   (p.StorageDomain ?? "").Trim(),
        Username: (p.StorageUsername ?? "").Trim(),
        Password: p.StoragePassword ?? "",
        LinkSharePercent: p.StoragePushLinkSharePercent);
}

/// <summary>
/// A pluggable network-storage backend. One instance owns one live connection;
/// <see cref="StoragePushService"/> creates a fresh instance per connect cycle
/// via <see cref="StorageTargetFactory"/> and disposes it on drop. Adapters
/// mirror the local capture tree by creating remote directories as needed.
/// </summary>
public interface IStorageTarget : IDisposable {
    string Kind { get; }

    /// <summary>Open the connection / validate the base path. Throws on failure.</summary>
    Task ConnectAsync(StorageConfig cfg, CancellationToken ct);

    /// <summary>Copy <paramref name="localPath"/> to the target at
    /// <paramref name="relPath"/> (relative to the capture root, OS-separated),
    /// creating intermediate directories. Skips when the destination already
    /// exists with the same length (idempotent re-push).</summary>
    Task UploadAsync(string localPath, string relPath, CancellationToken ct);

    /// <summary>Best-effort connectivity probe used by the "Test connection"
    /// button — never throws, returns a human-readable message.</summary>
    Task<(bool ok, string message)> TestAsync(StorageConfig cfg, CancellationToken ct);

    void Disconnect();
}

/// <summary>
/// Helpers shared by the adapters for translating an OS-relative capture path
/// into ordered remote segments.
/// </summary>
public static class StoragePath {
    /// <summary>Split a relative path into clean segments, dropping any leading
    /// base prefix's separators and collapsing "." / empty parts. Rejects any
    /// path that tries to escape upward ("..").</summary>
    public static string[] Segments(string relPath) {
        var parts = relPath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts) {
            if (p == "..") throw new ArgumentException($"Illegal '..' in relative path: {relPath}");
        }
        return parts.Where(p => p != ".").ToArray();
    }
}
