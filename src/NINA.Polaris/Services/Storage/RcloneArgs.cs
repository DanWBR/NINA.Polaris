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
/// Every rclone command line Polaris builds, as a pure function of its inputs.
///
/// <para>Separated from the adapter so the argument order, the quoting-free
/// list form and the fs composition can be tested on a machine with no rclone
/// installed, which is every CI runner and most development boxes.</para>
///
/// <para>Everything here returns a LIST, never a command string. Remote specs
/// carry a colon, capture folders carry spaces, and Windows paths carry
/// backslashes; handing that to a shell as one string is how an upload of
/// <c>M 31/light 1.fits</c> becomes three arguments. The caller passes the list
/// to <c>ProcessStartInfo.ArgumentList</c> and the runtime does the
/// platform-correct escaping.</para>
/// </summary>
public static class RcloneArgs {
    /// <summary>The config file name inside the rclone data directory.</summary>
    public const string ConfigFileName = "rclone.conf";

    /// <summary>Upload speed caps offered in the UI, in rclone's own syntax.
    /// Empty means unlimited. A fixed list rather than a free-text box: the
    /// value becomes a process argument, and "2 M" or a stray semicolon should
    /// fail in the form, not in the transfer.</summary>
    public static readonly IReadOnlyList<string> BandwidthChoices =
        new[] { "", "500k", "1M", "2M", "5M", "10M" };

    /// <summary>rclone backend types Polaris will create a remote for. The
    /// allowlist is the security boundary for a pasted config: a stanza naming
    /// any other type is refused rather than written to the config file.</summary>
    public static readonly IReadOnlyList<string> ProviderTypes =
        new[] { "drive", "onedrive", "dropbox", "webdav", "sftp", "s3" };

    /// <summary>Backend types whose sign-in needs a browser, so the operator has
    /// to authorise on another machine and paste the result.</summary>
    public static readonly IReadOnlyList<string> OAuthProviderTypes =
        new[] { "drive", "onedrive", "dropbox" };

    /// <summary>Names Polaris uses for its own storage kinds. A remote called
    /// "sftp" would make every log line ambiguous about whether it means the
    /// built-in SFTP adapter or an rclone remote, so the name is refused.</summary>
    private static readonly string[] ReservedNames = { "smb", "sftp", "local", "rclone" };

    private const int MaxRemoteNameLength = 32;

    // ---- validation -----------------------------------------------------

    public static bool IsValidBandwidth(string? value) =>
        BandwidthChoices.Contains((value ?? "").Trim(), StringComparer.Ordinal);

    public static bool IsProviderType(string? type) =>
        ProviderTypes.Contains((type ?? "").Trim().ToLowerInvariant(), StringComparer.Ordinal);

    public static bool NeedsBrowserSignIn(string? type) =>
        OAuthProviderTypes.Contains((type ?? "").Trim().ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>Is this a usable remote name, and if not, why not? The reason is
    /// shown to the operator, so it says what to do rather than what failed.</summary>
    public static bool IsValidRemoteName(string? name, out string? reason) {
        var n = (name ?? "").Trim();
        if (n.Length == 0) {
            reason = "Give the remote a name.";
            return false;
        }
        if (n.Length > MaxRemoteNameLength) {
            reason = $"Keep the name under {MaxRemoteNameLength} characters.";
            return false;
        }
        if (n[0] == '-' || n[0] == '.') {
            reason = "Start the name with a letter or a digit.";
            return false;
        }
        foreach (var c in n) {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '.' && c != '_') {
                reason = "Use letters, numbers, dash, dot and underscore. No spaces or colons.";
                return false;
            }
        }
        if (ReservedNames.Contains(n, StringComparer.OrdinalIgnoreCase)) {
            reason = $"\"{n}\" is reserved, pick another name.";
            return false;
        }
        reason = null;
        return true;
    }

    // ---- fs composition -------------------------------------------------

    /// <summary>The rclone fs string: <c>remote:base/rel</c>.
    ///
    /// <para>The base and the relative path are normalised through
    /// <see cref="StoragePath.Segments"/>, which collapses separators and
    /// rejects <c>..</c>, so a crafted capture path cannot climb out of the
    /// operator's folder on the remote.</para></summary>
    public static string Fs(string remoteName, string? basePath, string? relPath = null) {
        var segments = new List<string>();
        if (!string.IsNullOrWhiteSpace(basePath)) segments.AddRange(StoragePath.Segments(basePath!));
        if (!string.IsNullOrWhiteSpace(relPath)) segments.AddRange(StoragePath.Segments(relPath!));
        return remoteName + ":" + string.Join('/', segments);
    }

    // ---- commands -------------------------------------------------------

    /// <summary>Common to every invocation: the explicit config path, so a
    /// root-owned ~/.config/rclone/rclone.conf can never shadow ours.</summary>
    // --use-json-log on EVERY command, not just the copy: a failure line is
    // only parseable when it is JSON, and the plain-text form left the Test
    // button reporting "rclone rejected the command" for an ordinary wrong
    // URL. --config is explicit because a service account's HOME is not the
    // operator's.
    private static List<string> Base(string configPath) =>
        new() { "--config", configPath, "--use-json-log" };

    /// <summary>Copy ONE file to an exact destination name.
    ///
    /// <para><c>copyto</c> rather than <c>copy</c> because copy treats the last
    /// argument as a directory; copyto names the destination file and creates
    /// the intermediate folders itself, which is the mkdir loop the SFTP
    /// adapter has to do by hand. It also skips a destination that already
    /// matches, which is the idempotent re-push the interface promises.</para>
    ///
    /// <para>Retry is deliberately almost off. The push service already retries
    /// three times with backoff and opens a circuit breaker after three
    /// consecutive failures; rclone's own defaults would spend minutes per file
    /// against a dead remote and hide the failures the breaker exists to
    /// catch.</para></summary>
    /// <param name="partialSuffix">Only for rclone 1.63 and newer, which is
    /// where the flag was added. Ubuntu still packages 1.60, and an unknown
    /// flag fails the whole command, so an old rclone uploads without it and
    /// loses only the partial-name guarantee.</param>
    public static List<string> Copy(string configPath, StorageConfig cfg,
                                    string localPath, string relPath,
                                    bool partialSuffix = true) {
        var args = Base(configPath);
        args.Add("copyto");
        args.Add(localPath);
        args.Add(Fs(cfg.RemoteName, cfg.BasePath, relPath));
        args.Add("--stats"); args.Add("1s");   // machine-readable stats on stderr
        args.Add("--stats-log-level"); args.Add("NOTICE");
        args.Add("--transfers"); args.Add("1");
        args.Add("--checkers"); args.Add("2");
        args.Add("--retries"); args.Add("1");
        args.Add("--low-level-retries"); args.Add("3");
        args.Add("--contimeout"); args.Add("30s");
        args.Add("--timeout"); args.Add("5m");
        // Honoured by backends that support partial uploads; object stores only
        // publish the object once it is complete, so they are atomic anyway.
        if (partialSuffix) {
            args.Add("--partial-suffix"); args.Add(StoragePath.PartialSuffix);
        }
        // One file into a folder that may hold thousands: without this rclone
        // lists the destination directory first, which is a real API call per
        // frame on Drive.
        args.Add("--no-traverse");
        if (IsValidBandwidth(cfg.BandwidthLimit) && cfg.BandwidthLimit.Length > 0) {
            args.Add("--bwlimit"); args.Add(cfg.BandwidthLimit);
        }
        return args;
    }

    /// <summary>Recursive file listing for the backfill's one-shot pre-scan.
    /// <paramref name="relPrefix"/> scopes it to one folder so sending a single
    /// night does not enumerate an entire Drive.</summary>
    public static List<string> ListJson(string configPath, StorageConfig cfg, string? relPrefix = null) {
        var args = Base(configPath);
        args.Add("lsjson");
        args.Add(Fs(cfg.RemoteName, cfg.BasePath, relPrefix));
        args.Add("--recursive");
        args.Add("--files-only");
        args.Add("--no-modtime");
        args.Add("--no-mimetype");
        // Collapses a recursive listing into a handful of calls on bucket-like
        // backends: the difference between seconds and minutes on a big archive.
        args.Add("--fast-list");
        return args;
    }

    /// <summary>Connectivity probe for the Test button.</summary>
    public static List<string> Lsd(string configPath, StorageConfig cfg) {
        var args = Base(configPath);
        args.Add("lsd");
        args.Add(Fs(cfg.RemoteName, cfg.BasePath));
        args.Add("--max-depth"); args.Add("1");
        args.Add("--contimeout"); args.Add("20s");
        args.Add("--timeout"); args.Add("30s");
        return args;
    }

    /// <summary>Configured remotes, as name:type pairs. A local file read.</summary>
    public static List<string> ListRemotes(string configPath) {
        var args = Base(configPath);
        args.Add("listremotes");
        args.Add("--long");
        return args;
    }

    /// <summary>Free space, when the backend implements it. Best effort: plenty
    /// of backends do not, and a failure here must never turn a good test into
    /// a bad one.</summary>
    public static List<string> About(string configPath, string remoteName) {
        var args = Base(configPath);
        args.Add("about");
        args.Add(remoteName + ":");
        args.Add("--json");
        return args;
    }

    public static List<string> Version(string configPath) {
        var args = Base(configPath);
        args.Add("version");
        return args;
    }

    /// <summary>Create a remote.
    ///
    /// <para>NEVER log the list this returns: for a password or a pasted token
    /// the secret is one of these arguments. The copy path does log its
    /// arguments, so the asymmetry is deliberate and the adapter says so at the
    /// call site.</para>
    ///
    /// <para><paramref name="obscure"/> is for a password the operator typed in
    /// clear; a value pasted out of an existing rclone.conf is already obscured
    /// and must be passed through untouched.</para></summary>
    public static List<string> ConfigCreate(string configPath, string name, string type,
                                            IEnumerable<KeyValuePair<string, string>> values,
                                            bool obscure) {
        var args = Base(configPath);
        args.Add("config");
        args.Add("create");
        args.Add(name);
        args.Add(type);
        foreach (var kv in values) {
            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
            args.Add(kv.Key);
            args.Add(kv.Value ?? "");
        }
        args.Add("--non-interactive");
        args.Add(obscure ? "--obscure" : "--no-obscure");
        return args;
    }

    public static List<string> ConfigDelete(string configPath, string name) {
        var args = Base(configPath);
        args.Add("config");
        args.Add("delete");
        args.Add(name);
        return args;
    }

    /// <summary>The command the operator runs on a machine that HAS a browser.
    /// Built here rather than in the UI so the provider id cannot drift between
    /// the instructions and the allowlist.</summary>
    public static string AuthorizeCommand(string type) {
        var t = (type ?? "").Trim().ToLowerInvariant();
        return $"rclone authorize \"{t}\"";
    }
}
