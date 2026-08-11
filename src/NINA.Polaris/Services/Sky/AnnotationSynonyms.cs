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
/// Collapses catalogue rows that are the same object under a different name.
///
/// <para>WHY. The catalogue carries one row per designation, which is right for
/// searching: someone typing "NGC 6853" and someone typing "M27" must both find
/// the Dumbbell. It is wrong for an image overlay, where both rows have the
/// same coordinates and the two labels land exactly on top of each other. Every
/// one of the 107 Messier objects has an NGC or IC twin, so this happens on
/// most targets anyone points at.</para>
///
/// <para>THE RULE. Two rows merge only when one names the other in its aliases,
/// in either direction. Sharing coordinates is not enough on its own: the
/// shipped catalogue has 289 positions with more than one row and only 211 of
/// those are real synonyms. The rest are distinct objects that happen to carry
/// the same catalogued centre, such as LBN 970 through 973, and collapsing them
/// would hide objects rather than tidy the overlay.</para>
///
/// <para>An alias still has to agree on where the object is. Of the 320 alias
/// links in the shipped catalogue 318 resolve to exactly the same coordinates;
/// the two that do not are wrong (NGC 7368 and NGC 7418 both claim to be
/// IC 1459, from 35 and 219 arcmin away). Hence the separation guard.</para>
/// </summary>
public static class AnnotationSynonyms {

    /// <summary>How far apart two rows may be and still be believed to be the
    /// same object. Generous next to the 0 arcmin that real synonyms show, and
    /// far below the bad links this has to reject.</summary>
    public const double MaxSeparationArcmin = 10.0;

    private static int Rank(string catalog) => DesignationRank.Of(catalog);

    /// <summary>"NGC 6853", "ngc6853" and "NGC  6853" are one designation.</summary>
    internal static string Normalise(string? designation)
        => new string((designation ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

    /// <param name="Object">The row whose name is shown.</param>
    /// <param name="AlsoKnownAs">The designations that were folded into it,
    /// best-known first, so the UI can offer them without drawing them.</param>
    public readonly record struct Merged(DsoCatalog.DsoObject Object, IReadOnlyList<string> AlsoKnownAs);

    /// <summary>One entry per physical object, input order otherwise preserved
    /// so the overlay does not reshuffle between frames.</summary>
    public static IReadOnlyList<Merged> Collapse(IEnumerable<DsoCatalog.DsoObject> hits) {
        var list = hits?.ToList() ?? new List<DsoCatalog.DsoObject>();
        if (list.Count < 2) {
            return list.Select(o => new Merged(o, Array.Empty<string>())).ToList();
        }

        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < list.Count; i++) {
            var key = Normalise(list[i].Name);
            if (key.Length > 0) byName.TryAdd(key, i);      // first row wins a duplicated name
        }

        // Union-find over the alias links. Transitive on purpose: M31 can reach
        // NGC 224 through a third designation, and all three are one object.
        var parent = Enumerable.Range(0, list.Count).ToArray();
        int Find(int x) {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        void Union(int a, int b) {
            a = Find(a); b = Find(b);
            if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
        }

        for (int i = 0; i < list.Count; i++) {
            foreach (var alias in list[i].Aliases ?? Array.Empty<string>()) {
                var key = Normalise(alias);
                if (key.Length == 0 || !byName.TryGetValue(key, out var j) || j == i) continue;
                if (SeparationArcmin(list[i], list[j]) <= MaxSeparationArcmin) Union(i, j);
            }
        }

        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < list.Count; i++) {
            if (!groups.TryGetValue(Find(i), out var g)) groups[Find(i)] = g = new List<int>();
            g.Add(i);
        }

        var result = new List<Merged>(groups.Count);
        var emitted = new HashSet<int>();
        for (int i = 0; i < list.Count; i++) {
            var root = Find(i);
            if (!emitted.Add(root)) continue;               // keep the first appearance's place
            var members = groups[root]
                .OrderBy(k => Rank(list[k].Catalog))
                .ThenBy(k => list[k].Name?.Length ?? 0)
                .ThenBy(k => list[k].Name, StringComparer.Ordinal)
                .ToList();
            result.Add(new Merged(
                list[members[0]],
                members.Skip(1).Select(k => list[k].Name).ToList()));
        }
        return result;
    }

    private static double SeparationArcmin(DsoCatalog.DsoObject a, DsoCatalog.DsoObject b) {
        var meanDec = (a.DecDeg + b.DecDeg) / 2.0 * Math.PI / 180.0;
        var dRa = (a.RaHours - b.RaHours) * 15.0 * Math.Cos(meanDec);
        var dDec = a.DecDeg - b.DecDeg;
        return Math.Sqrt(dRa * dRa + dDec * dDec) * 60.0;
    }
}
