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

using System.Diagnostics;
using NINA.Polaris.Services.External;

namespace NINA.Polaris.Services.Storage;

/// <summary>
/// Uploads to anything rclone can reach: Google Drive, OneDrive, Dropbox,
/// Nextcloud and other WebDAV, SFTP, S3 and the rest of its catalogue.
///
/// <para>One rclone process per file. That is deliberate, and it is the one
/// thing to reconsider if a large backfill turns out slow: a single
/// <c>rclone copy</c> of the whole tree would be faster, but it would also
/// throw away everything the push service already does well, which is per-file
/// retry, per-file progress, per-file abort and a circuit breaker that stops a
/// dead remote from starving the live view. Keeping the unit of work at one
/// file keeps all of that for free.</para>
///
/// <para>The provider is not Polaris's business: it is a property of the
/// remote, configured once in rclone's own config file. That is why there is
/// one kind here and not one per provider.</para>
/// </summary>
public sealed class RcloneStorageTarget : IStorageTarget {
    private readonly RcloneService _rclone;
    private readonly ILogger<RcloneStorageTarget> _logger;
    private StorageConfig? _cfg;
    private bool _partialSuffix;

    public RcloneStorageTarget(RcloneService rclone, ILogger<RcloneStorageTarget> logger) {
        _rclone = rclone;
        _logger = logger;
    }

    public string Kind => "rclone";

    /// <summary>Checks that are local and instant: the binary, the name, and
    /// whether the remote is in the config.
    ///
    /// <para>No network probe on purpose. The lane drops and rebuilds this
    /// connection after every failure, so a round trip here would be paid again
    /// on every retry, and a connectivity problem belongs to the first upload
    /// where the breaker can see it. The Test button does the real probe.</para></summary>
    public async Task ConnectAsync(StorageConfig cfg, CancellationToken ct) {
        if (!_rclone.IsAvailable)
            throw new InvalidOperationException(
                "rclone is not installed on this host, so cloud uploads cannot run.");
        if (!RcloneArgs.IsValidRemoteName(cfg.RemoteName, out var why))
            throw new InvalidOperationException(why!);
        // Resolved once here rather than per upload. Ubuntu packages rclone
        // 1.60, which does not know --partial-suffix and fails the whole
        // command over the unknown flag.
        _partialSuffix = await _rclone.SupportsPartialSuffixAsync(ct);
        _cfg = cfg;
    }

    public async Task UploadAsync(string localPath, string relPath, CancellationToken ct,
                                  IProgress<long>? progress = null) {
        var cfg = _cfg ?? throw new InvalidOperationException("Not connected.");
        var exe = _rclone.BinaryPath
            ?? throw new InvalidOperationException("rclone is not installed on this host.");

        var args = RcloneArgs.Copy(_rclone.ConfigPath, cfg, localPath, relPath, _partialSuffix);
        var psi = new ProcessStartInfo {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // rclone reports progress and errors as JSON lines on stderr. Read them
        // as they arrive: a five-minute upload with no feedback is what the
        // progress bar exists to avoid.
        var errorLines = new List<string>();
        var reader = Task.Run(async () => {
            while (true) {
                var line = await proc.StandardError.ReadLineAsync(CancellationToken.None);
                if (line == null) break;
                var bytes = RcloneOutput.StatsBytes(line);
                if (bytes.HasValue) { try { progress?.Report(bytes.Value); } catch { } }
                else if (RcloneOutput.ErrorMessage(line) != null) {
                    lock (errorLines) { if (errorLines.Count < 20) errorLines.Add(line); }
                }
            }
        }, CancellationToken.None);

        try {
            await proc.WaitForExitAsync(ct);
        } catch (OperationCanceledException) {
            // An abort, or the host shutting down. Kill the tree: a killed
            // copyto leaves either a .part sidecar or nothing, both harmless.
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        } finally {
            try { await reader; } catch { }
        }

        var outcome = RcloneOutput.Classify(proc.ExitCode);
        if (outcome.Success) {
            // rclone reports no bytes when it skipped an identical destination,
            // so finish the bar from the local size rather than leaving it short.
            try { progress?.Report(new FileInfo(localPath).Length); } catch { }
            return;
        }

        string detail;
        lock (errorLines) { detail = RcloneOutput.JoinErrors(errorLines) ?? outcome.Message ?? "Upload failed."; }
        if (!outcome.Retryable) throw new RcloneFatalException(detail);
        throw new IOException(detail);
    }

    public async Task<(bool ok, string message)> TestAsync(StorageConfig cfg, CancellationToken ct) {
        if (!_rclone.IsAvailable) return (false, "rclone is not installed on this host.");
        if (!RcloneArgs.IsValidRemoteName(cfg.RemoteName, out var why)) return (false, why!);
        if (!await _rclone.RemoteExistsAsync(cfg.RemoteName, ct))
            return (false, $"Remote \"{cfg.RemoteName}\" is not set up yet.");

        try {
            var r = await _rclone.RunAsync(_rclone.BinaryPath!, RcloneArgs.Lsd(_rclone.ConfigPath, cfg), ct);
            if (r.ExitCode == 0) {
                var where = string.IsNullOrWhiteSpace(cfg.BasePath)
                    ? cfg.RemoteName : $"{cfg.RemoteName}:{cfg.BasePath}";
                return (true, $"Connected to {where}.");
            }
            // Exit 3 is "directory not found", which on a first run means the
            // folder simply does not exist yet. copyto will create it, so this
            // is a success with a note, not a failure.
            if (r.ExitCode == 3)
                return (true, $"Connected. The folder \"{cfg.BasePath}\" does not exist yet "
                            + "and will be created by the first upload.");
            var msg = RcloneOutput.JoinErrors(r.Stderr.Split('\n'))
                      ?? RcloneOutput.Classify(r.ExitCode).Message
                      ?? $"rclone exited with code {r.ExitCode}.";
            return (false, msg);
        } catch (OperationCanceledException) {
            return (false, "The remote did not answer in time.");
        } catch (Exception ex) {
            return (false, ex.Message);
        }
    }

    public Task<IReadOnlyDictionary<string, long>?> ListAsync(CancellationToken ct) =>
        ListAsync("", ct);

    /// <summary>The remote's files under one sub-tree, for the backfill's
    /// pre-scan. Scoped, because sending one night should not enumerate a whole
    /// cloud account: on a rate-limited backend that is the difference between
    /// a second and several minutes of API calls.</summary>
    public async Task<IReadOnlyDictionary<string, long>?> ListAsync(string relPrefix,
                                                                    CancellationToken ct) {
        var cfg = _cfg;
        if (cfg == null || !_rclone.IsAvailable) return null;
        try {
            var args = RcloneArgs.ListJson(_rclone.ConfigPath, cfg, relPrefix);
            var r = await _rclone.RunAsync(_rclone.BinaryPath!, args, ct);
            // Exit 3 means the folder is not there yet: an empty remote, not a
            // failure. Saying "empty" rather than "unknown" lets the backfill
            // queue everything once instead of falling back blindly.
            if (r.ExitCode == 3)
                return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (r.ExitCode != 0) return null;

            var map = RcloneOutput.ParseLsjson(r.Stdout);
            if (map == null || string.IsNullOrWhiteSpace(relPrefix)) return map;

            // lsjson paths are relative to the fs it was given, and the caller
            // keys everything on the capture root, so put the prefix back.
            var prefix = string.Join('/', StoragePath.Segments(relPrefix));
            var rebased = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in map) rebased[prefix + "/" + k] = v;
            return rebased;
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "rclone listing failed; the backfill will queue everything");
            return null;
        }
    }

    public void Disconnect() => _cfg = null;

    public void Dispose() => Disconnect();
}
