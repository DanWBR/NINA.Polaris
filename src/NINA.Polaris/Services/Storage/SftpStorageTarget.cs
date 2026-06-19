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

using Renci.SshNet;

namespace NINA.Polaris.Services.Storage;

/// <summary>
/// SFTP (SSH) backend. Reuses the SSH.NET dependency already pulled in for the
/// remote-terminal feature, so no new package. Mirrors the capture tree under
/// an optional base directory on the server (default = login home).
/// </summary>
public sealed class SftpStorageTarget : IStorageTarget {
    private SftpClient? _client;
    private string _base = ".";

    public string Kind => "sftp";

    public Task ConnectAsync(StorageConfig cfg, CancellationToken ct) {
        var port = cfg.Port > 0 ? cfg.Port : 22;
        var client = new SftpClient(cfg.Host, port, cfg.Username, cfg.Password) {
            OperationTimeout = TimeSpan.FromSeconds(30)
        };
        client.Connect();
        _client = client;
        _base = string.IsNullOrWhiteSpace(cfg.BasePath) ? "." : cfg.BasePath.Replace('\\', '/').TrimEnd('/');
        return Task.CompletedTask;
    }

    public Task UploadAsync(string localPath, string relPath, CancellationToken ct) {
        if (_client is not { IsConnected: true }) throw new InvalidOperationException("SFTP not connected");
        var segs = StoragePath.Segments(relPath);
        // Ensure each directory level exists (SFTP has no recursive mkdir).
        var dir = _base;
        for (int i = 0; i < segs.Length - 1; i++) {
            dir = dir + "/" + segs[i];
            if (!_client.Exists(dir)) _client.CreateDirectory(dir);
        }
        var remote = _base + "/" + string.Join('/', segs);
        using var fs = File.OpenRead(localPath);
        _client.UploadFile(fs, remote, canOverride: true);
        return Task.CompletedTask;
    }

    public Task<(bool ok, string message)> TestAsync(StorageConfig cfg, CancellationToken ct) {
        try {
            var port = cfg.Port > 0 ? cfg.Port : 22;
            using var c = new SftpClient(cfg.Host, port, cfg.Username, cfg.Password) {
                OperationTimeout = TimeSpan.FromSeconds(15)
            };
            c.Connect();
            var basePath = string.IsNullOrWhiteSpace(cfg.BasePath) ? "." : cfg.BasePath.Replace('\\', '/').TrimEnd('/');
            var exists = c.Exists(basePath);
            c.Disconnect();
            return Task.FromResult(exists
                ? (true, $"Connected to {cfg.Host}:{port}, base \"{basePath}\" OK")
                : (false, $"Connected, but base path not found: {basePath}"));
        } catch (Exception ex) {
            return Task.FromResult((false, ex.Message));
        }
    }

    public void Disconnect() {
        try { if (_client?.IsConnected == true) _client.Disconnect(); } catch { /* ignore */ }
        _client?.Dispose();
        _client = null;
    }

    public void Dispose() => Disconnect();
}
