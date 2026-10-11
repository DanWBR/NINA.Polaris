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

using System.Globalization;

namespace NINA.Polaris.Services.Broadcast;

/// <summary>Where the description on the card came from. Decides the credit
/// line, and whether it is worth trying to fetch a better one later.</summary>
public enum ObjectNoteSource { Generated, Bundled, Fetched }

/// <summary>What the catalogue knows about the object, flattened so the text
/// composer needs no catalogue types and can be tested on its own.</summary>
public sealed record ObjectCardFacts {
    public string? CommonName { get; init; }
    public string? CatalogId { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
    public string? Type { get; init; }
    public string? Constellation { get; init; }
    public double? Magnitude { get; init; }
    public double? SizeArcmin { get; init; }
}

/// <summary>The already-translated fragments the composer fills in. Passed in
/// rather than looked up, so this file has no opinion about localisation and
/// the broadcast can be in a different language from the interface.</summary>
public sealed record ObjectCardTemplates {
    /// <summary>For example "{0} in {1}", type and constellation.</summary>
    public string TypeInConstellation { get; init; } = "{0} in {1}";
    public string MagnitudeLabel { get; init; } = "mag {0}";
    /// <summary>Arcminutes, already abbreviated for the audience.</summary>
    public string SizeLabel { get; init; } = "{0}'";
    public string ViaWikipedia { get; init; } = "via Wikipedia";
    /// <summary>Shown when the catalogue has nothing at all, which happens for
    /// a manually entered target.</summary>
    public string UnknownObject { get; init; } = "Unnamed target";
}

/// <summary>What the card renders.</summary>
public sealed record ObjectCardCopy(
    string Title,
    string Subtitle,
    IReadOnlyList<string> Facts,
    string Description,
    string? Credit,
    ObjectNoteSource Source);

/// <summary>
/// Builds the words on the object card.
///
/// <para>The description has three possible origins and they are tried in a
/// fixed order: the text bundled with Polaris, then a text fetched online and
/// cached, then a sentence generated from the catalogue facts. The order is
/// deliberate. The bundled text is ours, reviewed, and available with no
/// network, which is the normal condition at a dark site. The fetched one is
/// better written than anything generated but arrives only when the host has a
/// connection, and it carries someone else's licence, so it brings a credit
/// line with it. The generated sentence is the floor: it is never absent, so
/// the card is never blank.</para>
///
/// <para>Pure, and that is what makes the ordering testable without a network.</para>
/// </summary>
public static class ObjectCardText {

    public static ObjectCardCopy Compose(ObjectCardFacts? facts, string? bundled, string? fetched,
                                         ObjectCardTemplates? templates = null) {
        var f = facts ?? new ObjectCardFacts();
        var t = templates ?? new ObjectCardTemplates();

        var title = FirstNonBlank(f.CommonName, f.CatalogId, t.UnknownObject)!;
        var subtitle = BuildSubtitle(f, title);
        var chips = BuildFacts(f, t);

        string description;
        string? credit = null;
        ObjectNoteSource source;
        if (!string.IsNullOrWhiteSpace(bundled)) {
            description = bundled.Trim();
            source = ObjectNoteSource.Bundled;
        } else if (!string.IsNullOrWhiteSpace(fetched)) {
            description = fetched.Trim();
            // Not optional: the online text is published under a licence that
            // requires attribution, and a broadcast is publishing.
            credit = t.ViaWikipedia;
            source = ObjectNoteSource.Fetched;
        } else {
            description = Generate(f, t);
            source = ObjectNoteSource.Generated;
        }
        return new ObjectCardCopy(title, subtitle, chips, description, credit, source);
    }

    /// <summary>The catalogue ids, minus whatever is already the title, so the
    /// card does not read "Orion Nebula / Orion Nebula".</summary>
    private static string BuildSubtitle(ObjectCardFacts f, string title) {
        var ids = new List<string>();
        void Add(string? s) {
            if (string.IsNullOrWhiteSpace(s)) return;
            var v = s.Trim();
            if (string.Equals(v, title, StringComparison.OrdinalIgnoreCase)) return;
            if (!ids.Contains(v, StringComparer.OrdinalIgnoreCase)) ids.Add(v);
        }
        Add(f.CatalogId);
        foreach (var a in f.Aliases) { if (ids.Count >= 3) break; Add(a); }
        return string.Join(" · ", ids);
    }

    private static List<string> BuildFacts(ObjectCardFacts f, ObjectCardTemplates t) {
        var chips = new List<string>();
        if (!string.IsNullOrWhiteSpace(f.Type)) chips.Add(f.Type.Trim());
        if (!string.IsNullOrWhiteSpace(f.Constellation)) chips.Add(f.Constellation.Trim());
        if (f.Magnitude is { } m && !double.IsNaN(m))
            chips.Add(string.Format(CultureInfo.InvariantCulture, t.MagnitudeLabel,
                m.ToString("0.#", CultureInfo.InvariantCulture)));
        if (f.SizeArcmin is { } s && s > 0)
            chips.Add(string.Format(CultureInfo.InvariantCulture, t.SizeLabel,
                s.ToString(s < 10 ? "0.#" : "0", CultureInfo.InvariantCulture)));
        return chips;
    }

    /// <summary>The floor: one line from the facts. Mechanical, but it is never
    /// wrong and never empty, which is the job.</summary>
    private static string Generate(ObjectCardFacts f, ObjectCardTemplates t) {
        var hasType = !string.IsNullOrWhiteSpace(f.Type);
        var hasCon = !string.IsNullOrWhiteSpace(f.Constellation);
        var parts = new List<string>();
        if (hasType && hasCon)
            parts.Add(string.Format(CultureInfo.InvariantCulture, t.TypeInConstellation,
                f.Type!.Trim(), f.Constellation!.Trim()));
        else if (hasType) parts.Add(f.Type!.Trim());
        else if (hasCon) parts.Add(f.Constellation!.Trim());

        foreach (var chip in BuildFacts(f, t)) {
            // The type and constellation are already in the sentence above.
            if (string.Equals(chip, f.Type?.Trim(), StringComparison.Ordinal)) continue;
            if (string.Equals(chip, f.Constellation?.Trim(), StringComparison.Ordinal)) continue;
            parts.Add(chip);
        }
        if (parts.Count == 0) return "";
        var sentence = string.Join(", ", parts);
        return char.ToUpperInvariant(sentence[0]) + sentence[1..] + ".";
    }

    private static string? FirstNonBlank(params string?[] values) {
        foreach (var v in values) if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return null;
    }
}
