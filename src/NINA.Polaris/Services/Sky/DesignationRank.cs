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

namespace NINA.Polaris.Services.Sky;

/// <summary>
/// Which of an object's names to show, when it has several.
///
/// <para>One rule, shared, because every list that shows a catalogued object
/// needs the same answer and they were each answering it differently: Tonight's
/// Best carried its own copy that parsed the display name, and the annotation
/// overlay had none at all. The field report was Andromeda's companion listed
/// as "Arp 168" rather than "M32".</para>
///
/// <para>The order is what an observer recognises, not what a catalogue thinks
/// is canonical. Messier and Caldwell are the names people use; NGC and IC are
/// the everyday fallback; Sharpless and the Lynds catalogues name things that
/// have no other designation; and the morphology catalogues at the bottom (Arp,
/// HCG, UGC, PGC) almost always re-designate an object that already has a
/// better-known name.</para>
/// </summary>
public static class DesignationRank {

    /// <param name="catalog">The catalogue column, e.g. "M", "NGC", "Arp".</param>
    /// <returns>Lower is more familiar.</returns>
    public static int Of(string? catalog) => (catalog ?? "").Trim().ToUpperInvariant() switch {
        "M" => 0,
        "C" => 1,                                   // Caldwell
        "NGC" => 2,
        "IC" => 3,
        "SH2" or "LBN" or "LDN" or "B" => 4,        // usually the only name there is
        "CR" or "MEL" or "TR" or "STOCK" => 5,      // open-cluster catalogues
        "ARP" or "HCG" or "VV" or "ABELL" or "AGC" => 8,
        "UGC" or "PGC" or "MCG" or "ESO" => 9,
        _ => 6
    };

    /// <summary>The same question asked of a DISPLAY NAME, for callers that
    /// only have the rendered string. "M31" is Messier; "Mel 25" and "MCG 1-2-3"
    /// are not, so the character after the letters has to be a digit or a
    /// space.</summary>
    public static int OfName(string? name) {
        if (string.IsNullOrWhiteSpace(name)) return 99;
        var n = name.Trim();
        int i = 0;
        while (i < n.Length && char.IsLetter(n[i])) i++;
        if (i == 0) return 99;
        var prefix = n[..i];
        // "Sh2 27" splits as letters "Sh" then "2 27"; keep the digit that
        // belongs to the catalogue name.
        if (prefix.Equals("Sh", StringComparison.OrdinalIgnoreCase)
            && i < n.Length && n[i] == '2') prefix = "SH2";
        else if (i < n.Length && !char.IsDigit(n[i]) && n[i] != ' ') return 99;
        return Of(prefix);
    }
}
