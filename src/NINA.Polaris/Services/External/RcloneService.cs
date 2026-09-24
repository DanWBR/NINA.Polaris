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
using NINA.Polaris.Services.Storage;

namespace NINA.Polaris.Services.External;

/// <summary>
/// Finds rclone and owns its configuration file.
///
/// <para>Polaris does not speak Google Drive, OneDrive or Dropbox. rclone does,
/// along with seventy other providers, and it already solves the parts that are
/// genuinely hard: the OAuth dance, refreshing a token at three in the morning,
/// chunked uploads that survive a dropped link. So the cloud destination is
/// orchestration, not integration, and this class is the seam.</para>
///
/// <para><b>The config file is the credential store and Polaris never reads
/// it.</b> It lives at <c>{DataDir}/rclone/rclone.conf</c>, mode 600, and every
/// invocation passes <c>--config</c> explicitly. Two reasons for the explicit
/// path: under systemd the polaris user's HOME is not where anyone expects, and
/// a root-owned config left behind by a <c>sudo rclone config</c> session would
/// otherwise win silently. Writes go through <c>rclone config</c> rather than
/// our own INI writer, because an escaping bug in a hand-rolled writer is a
/// corrupted credential, and because rclone owns the password obscuring.</para>
/// </summary>
public sealed class RcloneService {
    private readonly ProfileService _profiles;
    private readonly ILogger<RcloneService> _logger;

    /// <summary>Long enough for a slow remote to answer a listing, short enough
    /// that a wedged process cannot hold a request thread all night.</summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    public RcloneService(ProfileService profiles, ILogger<RcloneService> logger) {
        _profiles = profiles;
        _logger = logger;
    }

    public string? BinaryPath => Locate();
    public bool IsAvailable => !string.IsNullOrEmpty(BinaryPath);

    /// <summary>Where the remotes and their tokens live. Created on demand with
    /// owner-only permissions.</summary>
    public string ConfigPath {
        get {
            var dir = Path.Combine(_profiles.DataDir, "rclone");
            EnsureDir(dir);
            return Path.Combine(dir, RcloneArgs.ConfigFileName);
        }
    }

    private string? Locate() => BinaryLocator.Find(
        _profiles.Active?.RclonePath,
        windowsCandidates: new[] {
            @"C:\Program Files\rclone\rclone.exe",
            @"C:\ProgramData\chocolatey\bin\rclone.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Programs", "rclone", "rclone.exe")
        },
        linuxCandidates: new[] { "/usr/bin/rclone", "/usr/local/bin/rclone", "/snap/bin/rclone" },
        macCandidates: new[] { "/opt/homebrew/bin/rclone", "/usr/local/bin/rclone" },
        pathLookupName: "rclone");

    /// <summary>Every place we looked, so the card can show the operator where
    /// to drop the binary instead of just saying no.</summary>
    public IReadOnlyList<BinaryLocator.Candidate> EnumerateBinaryCandidates() =>
        BinaryLocator.Enumerate(
            _profiles.Active?.RclonePath,
            new[] {
                @"C:\Program Files\rclone\rclone.exe",
                @"C:\ProgramData\chocolatey\bin\rclone.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "Programs", "rclone", "rclone.exe")
            },
            new[] { "/usr/bin/rclone", "/usr/local/bin/rclone", "/snap/bin/rclone" },
            new[] { "/opt/homebrew/bin/rclone", "/usr/local/bin/rclone" },
            "rclone");

    /// <summary>The version string, or null when rclone is not installed or did
    /// not answer. Display only.</summary>
    public async Task<string?> VersionAsync(CancellationToken ct = default) {
        var exe = BinaryPath;
        if (exe == null) return null;
        var r = await RunAsync(exe, RcloneArgs.Version(ConfigPath), ct);
        if (r.ExitCode != 0) return null;
        var first = r.Stdout.Split('\n').FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(first) ? null : first;
    }

    /// <summary>The configured remotes. A local file read, so it is safe to call
    /// from a request and it works with no network.</summary>
    public async Task<IReadOnlyList<(string Name, string Type)>> ListRemotesAsync(
            CancellationToken ct = default) {
        var exe = BinaryPath;
        if (exe == null) return Array.Empty<(string, string)>();
        var r = await RunAsync(exe, RcloneArgs.ListRemotes(ConfigPath), ct);
        return r.ExitCode == 0
            ? RcloneOutput.ParseRemotes(r.Stdout)
            : Array.Empty<(string, string)>();
    }

    public async Task<bool> RemoteExistsAsync(string name, CancellationToken ct = default) =>
        (await ListRemotesAsync(ct)).Any(r =>
            string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Create or replace a remote.
    ///
    /// <para>The secret is one of the arguments, so this is the one call whose
    /// argument list is NEVER logged, at any level. The copy path does log its
    /// arguments; do not make these symmetric.</para></summary>
    public async Task<(bool ok, string message)> CreateRemoteAsync(
            string name, string type, IReadOnlyDictionary<string, string> values,
            bool obscure, CancellationToken ct = default) {
        var exe = BinaryPath;
        if (exe == null) return (false, "rclone is not installed on this host.");
        if (!RcloneArgs.IsValidRemoteName(name, out var why)) return (false, why!);
        if (!RcloneArgs.IsProviderType(type)) return (false, $"Polaris does not set up \"{type}\" remotes.");

        var args = RcloneArgs.ConfigCreate(ConfigPath, name, type, values, obscure);
        var r = await RunAsync(exe, args, ct, logArguments: false);
        HardenConfigPermissions();
        if (r.ExitCode != 0) {
            // The message may quote the backend's own complaint, which is
            // useful, but it must never carry the argument list back.
            var msg = FirstLine(r.Stderr) ?? FirstLine(r.Stdout) ?? $"rclone exited with code {r.ExitCode}.";
            _logger.LogWarning("Creating rclone remote {Name} ({Type}) failed: {Message}", name, type, msg);
            return (false, msg);
        }
        _logger.LogInformation("rclone remote {Name} ({Type}) created", name, type);
        return (true, $"Remote \"{name}\" created.");
    }

    public async Task<(bool ok, string message)> DeleteRemoteAsync(string name,
                                                                   CancellationToken ct = default) {
        var exe = BinaryPath;
        if (exe == null) return (false, "rclone is not installed on this host.");
        if (!RcloneArgs.IsValidRemoteName(name, out var why)) return (false, why!);
        var r = await RunAsync(exe, RcloneArgs.ConfigDelete(ConfigPath, name), ct);
        HardenConfigPermissions();
        return r.ExitCode == 0
            ? (true, $"Remote \"{name}\" removed.")
            : (false, FirstLine(r.Stderr) ?? $"rclone exited with code {r.ExitCode}.");
    }

    /// <summary>Run rclone and collect both streams. Used for the short
    /// commands; the upload has its own streaming loop because it needs
    /// progress as it goes.</summary>
    public async Task<ProcessOutput> RunAsync(string exe, IReadOnlyList<string> args,
                                              CancellationToken ct, bool logArguments = true) {
        var psi = new ProcessStartInfo {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        if (logArguments) _logger.LogDebug("rclone {Args}", string.Join(' ', args));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CommandTimeout);
        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var stdout = proc.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = proc.StandardError.ReadToEndAsync(timeout.Token);
        try {
            await proc.WaitForExitAsync(timeout.Token);
        } catch (OperationCanceledException) {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return new ProcessOutput(proc.ExitCode, await stdout, await stderr);
    }

    public readonly record struct ProcessOutput(int ExitCode, string Stdout, string Stderr);

    private static string? FirstLine(string? s) {
        if (string.IsNullOrWhiteSpace(s)) return null;
        foreach (var line in s.Split('\n')) {
            var t = line.Trim();
            if (t.Length > 0) return t.Length > 300 ? t[..300] : t;
        }
        return null;
    }

    private void EnsureDir(string dir) {
        try {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows()) {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite
                                        | UnixFileMode.UserExecute);
            }
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Could not prepare the rclone config directory {Dir}", dir);
        }
    }

    /// <summary>rclone already writes the file 0600, but the data directory's
    /// umask is not ours to assume, and this file holds refresh tokens.</summary>
    private void HardenConfigPermissions() {
        if (OperatingSystem.IsWindows()) return;
        try {
            var path = ConfigPath;
            if (File.Exists(path))
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "chmod 600 on the rclone config failed");
        }
    }
}
