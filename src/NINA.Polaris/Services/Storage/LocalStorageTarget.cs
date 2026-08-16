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
/// Copies to a local or OS-mounted path (CIFS/NFS/USB drive). The user mounts
/// the share with the OS and points BasePath at the mount; this adapter just
/// does a plain <see cref="File.Copy"/> into the mirrored sub-tree. Covers any
/// protocol the OS can mount, including NFS which has no managed client.
/// </summary>
public sealed class LocalStorageTarget : IStorageTarget {
    private string _base = "";

    public string Kind => "local";

    public Task ConnectAsync(StorageConfig cfg, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(cfg.BasePath))
            throw new InvalidOperationException("Destination path is empty.");
        if (!Directory.Exists(cfg.BasePath))
            throw new DirectoryNotFoundException($"Destination path not found: {cfg.BasePath}");
        _base = cfg.BasePath;
        return Task.CompletedTask;
    }

    public Task UploadAsync(string localPath, string relPath, CancellationToken ct,
                            IProgress<long>? progress = null) {
        var dest = Path.Combine(new[] { _base }.Concat(StoragePath.Segments(relPath)).ToArray());
        var dir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Skip when an identical-length copy already exists (idempotent re-push).
        if (File.Exists(dest) && new FileInfo(dest).Length == new FileInfo(localPath).Length)
            return Task.CompletedTask;
        File.Copy(localPath, dest, overwrite: true);
        // Local copy is atomic; report the whole file at the end.
        try { progress?.Report(new FileInfo(dest).Length); } catch { }
        return Task.CompletedTask;
    }

    /// <summary>SHARESYNC: enumerate the mirrored tree under the base path so
    /// the backfill can skip files already present with the same size.</summary>
    public Task<IReadOnlyDictionary<string, long>?> ListAsync(CancellationToken ct) {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(_base) && Directory.Exists(_base)) {
            foreach (var f in Directory.EnumerateFiles(_base, "*", SearchOption.AllDirectories)) {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(_base, f).Replace('\\', '/');
                try { map[rel] = new FileInfo(f).Length; } catch { }
            }
        }
        return Task.FromResult<IReadOnlyDictionary<string, long>?>(map);
    }

    public Task<(bool ok, string message)> TestAsync(StorageConfig cfg, CancellationToken ct) {
        try {
            if (string.IsNullOrWhiteSpace(cfg.BasePath))
                return Task.FromResult((false, "Destination path is empty."));
            if (!Directory.Exists(cfg.BasePath))
                return Task.FromResult((false, $"Path not found: {cfg.BasePath}"));
            // Probe write access with a temp marker file.
            var probe = Path.Combine(cfg.BasePath, ".polaris_write_test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return Task.FromResult((true, $"Writable: {cfg.BasePath}"));
        } catch (Exception ex) {
            return Task.FromResult((false, ex.Message));
        }
    }

    public void Disconnect() { _base = ""; }
    public void Dispose() => Disconnect();
}
