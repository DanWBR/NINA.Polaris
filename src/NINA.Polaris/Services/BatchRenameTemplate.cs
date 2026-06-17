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

using System.Text.RegularExpressions;

namespace NINA.Polaris.Services;

/// <summary>
/// Pure (filesystem-free, unit-testable) template engine for the STUDIO
/// batch-rename tool. Substitutes <c>{TOKEN}</c> placeholders in a
/// user-supplied template against a file's FITS header values plus a
/// running counter. Caller is responsible for appending the extension and
/// sanitising the result (see <see cref="FileBrowserService"/>).
///
/// Token grammar:
///   • <c>{KEYWORD}</c>  — any FITS header keyword (case-insensitive),
///                         e.g. {OBJECT}, {FILTER}, {EXPTIME}, {DATE-OBS}.
///                         Missing keyword → empty string.
///   • <c>{n}</c>        — the 1-based sequence number, no padding.
///   • <c>{n:0N}</c>     — the sequence number zero-padded to N digits
///                         (e.g. {n:03} → 001). The leading zero is
///                         conventional; {n:3} works too.
///   • anything else     — literal text, passed through unchanged.
/// </summary>
public static class BatchRenameTemplate {
    // One {...} placeholder. [^{}]+ keeps it from spanning braces.
    private static readonly Regex TokenRx = new(@"\{([^{}]+)\}", RegexOptions.Compiled);
    // Counter token: n, optionally ":<width>" (any leading zeros ignored).
    private static readonly Regex CounterRx = new(@"^n(?::0*(\d+))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Apply <paramref name="template"/> for one file. <paramref
    /// name="values"/> is the file's header keyword→value map (should use a
    /// case-insensitive comparer); <paramref name="index"/> is the 1-based
    /// position of this file in the batch (drives {n}). Returns the
    /// substituted base name WITHOUT extension or sanitisation.</summary>
    public static string Apply(string template, IReadOnlyDictionary<string, string> values, int index) {
        if (string.IsNullOrEmpty(template)) return "";
        return TokenRx.Replace(template, m => {
            var inner = m.Groups[1].Value.Trim();
            var counter = CounterRx.Match(inner);
            if (counter.Success) {
                var width = counter.Groups[1].Success
                    ? int.Parse(counter.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                    : 0;
                return width > 0
                    ? index.ToString("D" + width, System.Globalization.CultureInfo.InvariantCulture)
                    : index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            // Header keyword lookup (values dict is case-insensitive).
            return values.TryGetValue(inner, out var v) ? (v ?? "") : "";
        });
    }
}
