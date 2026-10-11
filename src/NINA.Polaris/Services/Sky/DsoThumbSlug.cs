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

namespace NINA.Polaris.Services.Sky;

/// <summary>
/// The filename rule for the bundled DSO cutouts: a catalogue designation
/// becomes an uppercase slug with no spaces and no leading zeros, so "NGC 7000"
/// and "ngc7000" both land on <c>NGC7000.jpg</c>.
///
/// <para>A common name never slugs. "Lagoon Nebula" has no digits, so it comes
/// back empty and the caller has to go through the catalogue to reach a
/// designation first. That is the intended shape, not a gap: the cutouts are
/// named after catalogue codes because those are unambiguous.</para>
///
/// <para>The rule also lives in <c>dsoThumbUrl()</c> in app.js, because the SKY
/// card resolves its own thumbnail in the browser. Keep the two in step; the
/// bundle-coverage tests check this copy against what actually ships.</para>
/// </summary>
public static partial class DsoThumbSlug {

    /// <summary>The slug for a designation, or "" when the name is not one.</summary>
    public static string For(string? name) {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var raw = name.Trim();
        // Sharpless is spelled half a dozen ways in catalogues and by hand
        // ("Sh2-155", "Sh 2 155", "Sharpless 155"); the cutouts use SH2155.
        var sh = Sh2Regex().Match(raw);
        if (!sh.Success) sh = SharplessRegex().Match(raw);
        if (sh.Success) return "SH2" + sh.Groups[1].Value;
        var m = DesignationRegex().Match(raw);
        return m.Success ? (m.Groups[1].Value + m.Groups[2].Value).ToUpperInvariant() : "";
    }

    [GeneratedRegex(@"^sh\s*2\s*[-\s]?\s*0*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex Sh2Regex();

    [GeneratedRegex(@"^sharpless\s*[-\s]?\s*0*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SharplessRegex();

    // "IC 1396A" keeps its letter suffix; the leading zeros of "NGC 0224" go.
    [GeneratedRegex(@"^([A-Za-z]+)\s*0*(\d+[A-Za-z]?)")]
    private static partial Regex DesignationRegex();
}
