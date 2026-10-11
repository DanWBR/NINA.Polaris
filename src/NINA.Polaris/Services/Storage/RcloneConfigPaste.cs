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
/// Reads what the operator pasted after signing in on another machine.
///
/// <para>The rig has no browser, so a Drive, OneDrive or Dropbox sign-in happens
/// on a laptop and its result is carried over by hand. Operators bring back one
/// of two things, and telling them they pasted the wrong one is a worse product
/// than accepting both:</para>
/// <list type="bullet">
/// <item>the token JSON that <c>rclone authorize "drive"</c> prints, wrapped in
/// its "Paste the following into your remote machine" banner;</item>
/// <item>a whole <c>[name]</c> section lifted out of a working rclone.conf on
/// their desktop, which is the only way to carry OneDrive's drive id.</item>
/// </list>
///
/// <para>Pure and string-only: it never writes the config file. Creating the
/// remote stays rclone's job, because a hand-written INI turns an escaping bug
/// into a corrupted credential.</para>
/// </summary>
public static class RcloneConfigPaste {
    private const string BannerStart = "Paste the following into your remote machine";
    private const string BannerEnd = "<---End paste";

    /// <summary>What a paste turned out to contain.</summary>
    /// <param name="Name">The section name, when the paste was a whole stanza.</param>
    /// <param name="Type">The backend type, when the paste declared one.</param>
    /// <param name="Values">The key/value pairs to hand to <c>rclone config create</c>.</param>
    /// <param name="AlreadyObscured">True for a stanza copied out of an existing
    /// rclone.conf, whose password is already in rclone's obscured form and must
    /// not be obscured a second time.</param>
    public readonly record struct Parsed(string? Name, string? Type,
                                         IReadOnlyDictionary<string, string> Values,
                                         bool AlreadyObscured);

    /// <summary>Parse a paste. Returns false with an operator-facing reason
    /// rather than throwing, because every failure here is something the person
    /// at the keyboard can fix.</summary>
    public static bool TryParse(string? text, out Parsed result, out string? error) {
        result = default;
        error = null;

        var body = StripBanner(text ?? "");
        if (body.Length == 0) {
            error = "Paste the output rclone printed, or the remote section from your rclone.conf.";
            return false;
        }

        // A bare token: what `rclone authorize` prints.
        if (body[0] == '{') {
            if (!LooksLikeJsonObject(body)) {
                error = "That looks like a token but it is not complete. Copy everything rclone printed.";
                return false;
            }
            result = new Parsed(null, null,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = body },
                AlreadyObscured: false);
            return true;
        }

        // A whole section copied out of an rclone.conf.
        if (body[0] == '[') return TryParseSection(body, out result, out error);

        error = "Paste either the token rclone printed, or a whole [section] from your rclone.conf.";
        return false;
    }

    /// <summary>Drop the human-readable framing `rclone authorize` prints around
    /// the token, and any blank lines around the payload.</summary>
    private static string StripBanner(string text) {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>();
        foreach (var line in lines) {
            var t = line.Trim();
            if (t.Length == 0) continue;
            if (t.Contains(BannerStart, StringComparison.OrdinalIgnoreCase)) continue;
            if (t.Contains(BannerEnd, StringComparison.OrdinalIgnoreCase)) continue;
            if (t.StartsWith("--->", StringComparison.Ordinal)) continue;
            // Comments are dropped here rather than only inside the section
            // parser, so a config that opens with "# my nas" is still
            // recognised as a section and not as free text.
            if (t[0] == '#' || t[0] == ';') continue;
            kept.Add(line.TrimEnd());
        }
        return string.Join('\n', kept).Trim();
    }

    private static bool LooksLikeJsonObject(string s) {
        if (s.Length < 2 || s[0] != '{' || s[^1] != '}') return false;
        try {
            using var _ = System.Text.Json.JsonDocument.Parse(s);
            return true;
        } catch (System.Text.Json.JsonException) {
            return false;
        }
    }

    private static bool TryParseSection(string body, out Parsed result, out string? error) {
        result = default;
        error = null;

        string? name = null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in body.Split('\n')) {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '#' || line[0] == ';') continue;

            if (line[0] == '[') {
                if (name != null) {
                    error = "Paste one remote at a time: that is more than one [section].";
                    return false;
                }
                var close = line.IndexOf(']');
                if (close <= 1) {
                    error = "The [section] line is malformed.";
                    return false;
                }
                name = line[1..close].Trim();
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;                      // not a key = value line
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Length == 0) continue;
            values[key] = value;
        }

        if (string.IsNullOrEmpty(name)) {
            error = "That section has no name. Copy the [name] line too.";
            return false;
        }
        if (!values.TryGetValue("type", out var type) || string.IsNullOrWhiteSpace(type)) {
            error = "That section has no type line, so Polaris cannot tell which provider it is.";
            return false;
        }
        if (!RcloneArgs.IsProviderType(type)) {
            error = $"Polaris does not set up \"{type}\" remotes.";
            return false;
        }
        values.Remove("type");

        result = new Parsed(name, type.Trim().ToLowerInvariant(), values, AlreadyObscured: true);
        return true;
    }
}
