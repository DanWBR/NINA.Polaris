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

using System.Text.Json;

namespace NINA.Polaris.Services.Storage;

/// <summary>
/// Reading what rclone says: stats, listings and exit codes, as pure functions.
///
/// <para>Every parser here fails soft. rclone's JSON log shape is stable in
/// practice but is not a documented API, so a line that does not parse is
/// skipped rather than thrown on: a future rclone that renames a field costs
/// the progress bar, never the upload.</para>
/// </summary>
public static class RcloneOutput {
    /// <summary>Cumulative bytes from one <c>--use-json-log</c> stats line, or
    /// null when this line is not one. rclone emits a JSON object per line on
    /// stderr; the ones carrying <c>stats.bytes</c> are the progress.</summary>
    public static long? StatsBytes(string? line) {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{') return null;
        try {
            using var doc = JsonDocument.Parse(trimmed);
            if (!doc.RootElement.TryGetProperty("stats", out var stats)) return null;
            if (!stats.TryGetProperty("bytes", out var bytes)) return null;
            return bytes.TryGetInt64(out var v) ? v : null;
        } catch (JsonException) {
            return null;
        }
    }

    /// <summary>The message of one JSON log line when it is an error, else null.
    /// Used to build the operator-facing LastError, which lands in the status
    /// block and then in the card, so it must be the driver's words and not a
    /// stack trace.</summary>
    public static string? ErrorMessage(string? line) {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{') return null;
        try {
            using var doc = JsonDocument.Parse(trimmed);
            if (!doc.RootElement.TryGetProperty("level", out var level)) return null;
            var l = level.GetString();
            if (l != "error" && l != "critical") return null;
            return doc.RootElement.TryGetProperty("msg", out var msg) ? msg.GetString() : null;
        } catch (JsonException) {
            return null;
        }
    }

    /// <summary>Join the error lines of a run into one sentence for the UI.</summary>
    public static string? JoinErrors(IEnumerable<string> lines, int maxLength = 300) {
        var msgs = lines.Select(ErrorMessage)
                        .Where(m => !string.IsNullOrWhiteSpace(m))
                        .Select(m => m!.Trim())
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
        if (msgs.Count == 0) return null;
        var joined = string.Join("; ", msgs);
        return joined.Length <= maxLength ? joined : joined[..maxLength].TrimEnd() + "...";
    }

    /// <summary>Parse <c>lsjson --recursive --files-only</c> into the
    /// relative-path to size map <see cref="IStorageTarget.ListAsync"/> promises.
    /// Returns null when the output is not usable, which the caller treats as
    /// "cannot enumerate cheaply" and falls back to enqueue-all.</summary>
    public static IReadOnlyDictionary<string, long>? ParseLsjson(string? json, int maxEntries = 200_000) {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in doc.RootElement.EnumerateArray()) {
                if (map.Count >= maxEntries) break;
                if (e.TryGetProperty("IsDir", out var isDir) && isDir.ValueKind == JsonValueKind.True)
                    continue;
                if (!e.TryGetProperty("Path", out var path)) continue;
                var p = path.GetString();
                if (string.IsNullOrEmpty(p)) continue;
                long size = 0;
                if (e.TryGetProperty("Size", out var s) && s.TryGetInt64(out var v)) size = v;
                // rclone reports -1 for a size it does not know; treat that as
                // "cannot compare", which makes the backfill re-send rather than
                // skip a file it cannot vouch for.
                map[p.Replace('\\', '/')] = size < 0 ? -1 : size;
            }
            return map;
        } catch (JsonException) {
            return null;
        }
    }

    /// <summary>Parse <c>listremotes --long</c>, whose lines are
    /// <c>name:{spaces}type</c>.</summary>
    public static IReadOnlyList<(string Name, string Type)> ParseRemotes(string? output) {
        var list = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(output)) return list;
        foreach (var raw in output.Split('\n')) {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var type = line[(colon + 1)..].Trim();
            if (name.Length == 0) continue;
            list.Add((name, type));
        }
        return list;
    }

    /// <summary>What an rclone exit code means for us.
    ///
    /// <para>The retryable flag is what decides whether the push lane backs off
    /// and tries again or gives up on the file immediately. Getting it wrong in
    /// the safe direction costs three attempts; getting it wrong the other way
    /// hides a real failure behind a retry loop.</para></summary>
    public static RcloneExit Classify(int exitCode) => exitCode switch {
        // 0 = done, 9 = nothing to transfer because the destination already
        // matches, which is exactly the idempotent skip we want.
        0 or 9 => new RcloneExit(true, false, null),
        1 => new RcloneExit(false, false, "rclone rejected the command (this is a Polaris bug)."),
        2 => new RcloneExit(false, true, "rclone reported an error."),
        3 => new RcloneExit(false, false, "The folder does not exist on the remote."),
        4 => new RcloneExit(false, false, "The file was gone before it could be uploaded."),
        5 => new RcloneExit(false, true, "rclone gave up after its own retries."),
        6 => new RcloneExit(false, false, "rclone reported an error it will not retry."),
        7 => new RcloneExit(false, false, "rclone reported a fatal error."),
        _ => new RcloneExit(false, true, $"rclone exited with code {exitCode}.")
    };
}

/// <summary>The meaning of one rclone exit code: did it work, is it worth
/// another go, and what to tell the operator when it is not.</summary>
public readonly record struct RcloneExit(bool Success, bool Retryable, string? Message);

/// <summary>Thrown when rclone failed in a way that retrying cannot fix, so the
/// push lane parks the file instead of spending two more attempts on it.</summary>
public sealed class RcloneFatalException : Exception {
    public RcloneFatalException(string message) : base(message) { }
}
