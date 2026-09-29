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

namespace NINA.Polaris.Services.Broadcast;

/// <summary>
/// The handful of words the host burns into the video frame.
///
/// <para>Everything else visible in Polaris is translated in the browser, from
/// the JSON catalogues, with the English string as the key. These cannot be:
/// they are drawn into a picture on a headless host that may have no browser
/// attached at all, and the broadcast has to keep reading correctly after the
/// operator closes the tab. So this is a deliberate second home for six
/// strings, not a drift from the pipeline.</para>
///
/// <para>The words the catalogue supplies, object types and constellation
/// names, stay as the catalogue has them. Translating "Emission nebula" would
/// mean translating the catalogue, which is a different and much larger job,
/// and constellation names are Latin to begin with.</para>
/// </summary>
public static class BroadcastStrings {

    /// <summary>The five interface languages, matching the browser catalogues.</summary>
    public static readonly string[] Languages = { "en", "pt", "es", "fr", "de" };

    /// <summary>
    /// What the frame says before there is a picture to show.
    ///
    /// <para>A broadcast starts the moment the button is pressed, which is
    /// usually before the first sub has landed, and a black rectangle with a
    /// card floating on it reads as a fault. This is the difference between a
    /// viewer waiting and a viewer closing the tab.</para>
    /// </summary>
    public static string WaitingForFirstFrame(string? language) => Normalise(language) switch {
        "pt" => "Aguardando a primeira imagem",
        "es" => "Esperando la primera imagen",
        "fr" => "En attente de la premiere image",
        "de" => "Warten auf das erste Bild",
        _ => "Waiting for the first image"
    };

    /// <summary>The card fragments for a language, falling back to English for
    /// anything not on the list.</summary>
    public static ObjectCardTemplates TemplatesFor(string? language) => Normalise(language) switch {
        "pt" => new ObjectCardTemplates {
            TypeInConstellation = "{0} em {1}",
            MagnitudeLabel = "mag {0}",
            SizeLabel = "{0}'",
            ViaWikipedia = "via Wikipédia",
            UnknownObject = "Alvo sem nome"
        },
        "es" => new ObjectCardTemplates {
            TypeInConstellation = "{0} en {1}",
            MagnitudeLabel = "mag {0}",
            SizeLabel = "{0}'",
            ViaWikipedia = "vía Wikipedia",
            UnknownObject = "Objetivo sin nombre"
        },
        "fr" => new ObjectCardTemplates {
            TypeInConstellation = "{0} dans {1}",
            MagnitudeLabel = "mag {0}",
            SizeLabel = "{0}'",
            ViaWikipedia = "via Wikipédia",
            UnknownObject = "Cible sans nom"
        },
        "de" => new ObjectCardTemplates {
            TypeInConstellation = "{0} im {1}",
            MagnitudeLabel = "Mag {0}",
            SizeLabel = "{0}'",
            ViaWikipedia = "via Wikipedia",
            UnknownObject = "Unbenanntes Ziel"
        },
        _ => new ObjectCardTemplates()
    };

    /// <summary>The language to use, which is the interface language when
    /// Polaris has words for it and English otherwise. Accepts "pt-BR" and the
    /// like, because that is what a browser sends.</summary>
    public static string Normalise(string? language) {
        if (string.IsNullOrWhiteSpace(language)) return "en";
        var v = language.Trim().ToLowerInvariant();
        var dash = v.IndexOfAny(new[] { '-', '_' });
        if (dash > 0) v = v[..dash];
        return Array.IndexOf(Languages, v) >= 0 ? v : "en";
    }
}
