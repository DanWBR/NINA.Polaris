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

using System.Net.Http.Headers;
using System.Text.Json;
using NINA.Polaris.Services.External;
using NINA.Polaris.Services.Sky;

namespace NINA.Polaris.Services.Broadcast;

/// <summary>Everything the broadcast needs in order to draw the object card.</summary>
public sealed record ObjectCard {
    /// <summary>The name the broadcast asked about, as it was given.</summary>
    public string TargetName { get; init; } = "";
    /// <summary>A JPEG on disk, from the bundled cutouts or the downloaded
    /// pack. Null when neither has one, and the card then has no picture.</summary>
    public string? ThumbnailPath { get; init; }
    public ObjectCardCopy Copy { get; init; } = ObjectCardText.Compose(null, null, null);
    public string Language { get; init; } = "en";
}

/// <summary>
/// Builds the object card for whatever the rig is pointed at.
///
/// <para>The facts come from the bundled DSO catalogue and the picture from the
/// cutout bundle, both offline. The description is the part with a decision in
/// it, and it is taken in a fixed order: the text Polaris ships, then a text
/// fetched from Wikipedia and cached, then a sentence generated from the facts.
/// <see cref="ObjectCardText"/> owns that order and is tested on its own; this
/// class supplies the three candidates and <see cref="ObjectNoteStore"/> owns
/// the two that live on disk.</para>
///
/// <para><b>A lookup never delays a frame.</b> <see cref="BuildAsync"/> reads
/// only what is already on disk and returns; when there is nothing and the host
/// has a connection, the lookup runs detached and <see cref="NoteFetched"/>
/// says when there is something better to show. A broadcast at a dark site with
/// no network therefore behaves exactly like one with a network, only with the
/// generated sentence, and nothing waits on a socket that will not answer.</para>
/// </summary>
public sealed class ObjectCardService {

    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    private readonly DsoCatalog _catalog;
    private readonly DsoThumbPackService _thumbs;
    private readonly ProfileService _profiles;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ObjectCardService> _logger;
    private readonly ObjectNoteStore _notes;
    private readonly object _lock = new();
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);

    /// <summary>Raised with the target name when a background lookup has found
    /// a description, so whatever is on air can rebuild its card.</summary>
    public event Action<string>? NoteFetched;

    public ObjectCardService(DsoCatalog catalog, DsoThumbPackService thumbs, ProfileService profiles,
                             IWebHostEnvironment env, IHttpClientFactory http,
                             ILogger<ObjectCardService> logger) {
        _catalog = catalog;
        _thumbs = thumbs;
        _profiles = profiles;
        _http = http;
        _logger = logger;
        var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        _notes = new ObjectNoteStore(
            // Beside the cutouts, but bundled rather than inside the 215 MB
            // pack: the whole set of notes is a few tens of kilobytes, and a
            // host that never downloads the pack should still have the words.
            Path.Combine(webRoot, "sky", "data", "skydata"),
            Path.Combine(profiles.DataDir, "broadcast", "notes-cache"),
            logger);
    }

    public ObjectNoteStore Notes => _notes;

    /// <summary>The language the card is written in: the interface language
    /// where Polaris has the words, English otherwise.</summary>
    public string Language => BroadcastStrings.Normalise(_profiles.Active?.UiLanguage);

    /// <summary>
    /// The card for <paramref name="targetName"/>. Returns what can be known
    /// without a network; a lookup for anything missing runs behind it.
    /// </summary>
    public async Task<ObjectCard> BuildAsync(string? targetName, bool fetchOnline,
                                             CancellationToken ct = default) {
        var lang = Language;
        var templates = BroadcastStrings.TemplatesFor(lang);
        var name = (targetName ?? "").Trim();
        if (name.Length == 0)
            return new ObjectCard { Language = lang, Copy = ObjectCardText.Compose(null, null, null, templates) };

        var dso = await LookupAsync(name, ct).ConfigureAwait(false);
        var key = ObjectNoteStore.KeyFor(FirstDesignation(dso, name));

        var bundled = _notes.Bundled(key, lang);
        var fetched = bundled == null ? _notes.Cached(key, lang) : null;
        if (bundled == null && fetched == null && fetchOnline)
            StartFetch(name, key, lang, WikipediaTitles(dso, name));

        return new ObjectCard {
            TargetName = name,
            ThumbnailPath = ResolveThumbnail(dso, name),
            Language = lang,
            Copy = ObjectCardText.Compose(ToFacts(dso, name), bundled, fetched, templates)
        };
    }

    // --- Catalogue --------------------------------------------------

    private async Task<DsoCatalog.DsoObject?> LookupAsync(string name, CancellationToken ct) {
        try {
            var hit = await _catalog.GetByNameAsync(name, ct).ConfigureAwait(false);
            if (hit != null) return hit;
            // A common name never matches the designation column, so "Lagoon
            // Nebula" only gets anywhere through the search.
            var found = await _catalog.SearchAsync(name, 1, ct).ConfigureAwait(false);
            return found.Count > 0 ? found[0] : null;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Catalogue lookup failed for {Name}", name);
            return null;
        }
    }

    internal static ObjectCardFacts ToFacts(DsoCatalog.DsoObject? d, string fallbackName) {
        // A hand typed target the catalogue does not know still gets a card,
        // with the name the operator gave it and nothing invented around it.
        if (d == null) return new ObjectCardFacts { CatalogId = fallbackName };
        return new ObjectCardFacts {
            // common_name is a comma separated list; the first is the one people use.
            CommonName = FirstOf(d.CommonName),
            CatalogId = d.Name,
            Aliases = d.Aliases?.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToArray()
                      ?? Array.Empty<string>(),
            Type = d.Type,
            Constellation = d.Constellation,
            Magnitude = d.Magnitude,
            SizeArcmin = d.SizeArcmin
        };
    }

    private static string? FirstOf(string? csv) {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 ? parts[0] : null;
    }

    /// <summary>The cutout, from the downloaded pack or the bundled subset.
    /// Tries the catalogue designation and then the aliases, because the files
    /// are named after designations and an object usually has several.</summary>
    private string? ResolveThumbnail(DsoCatalog.DsoObject? d, string rawName) {
        foreach (var candidate in Candidates(d, rawName)) {
            var slug = DsoThumbSlug.For(candidate);
            if (slug.Length == 0) continue;
            var path = _thumbs.Resolve(slug);
            if (path != null) return path;
        }
        return null;
    }

    /// <summary>The first of the candidate names that is a designation, which
    /// is what both the cutouts and the notes are filed under.</summary>
    private static string FirstDesignation(DsoCatalog.DsoObject? d, string rawName) {
        foreach (var candidate in Candidates(d, rawName))
            if (DsoThumbSlug.For(candidate).Length > 0) return candidate;
        return rawName;
    }

    private static IEnumerable<string> Candidates(DsoCatalog.DsoObject? d, string rawName) {
        if (d != null) {
            yield return d.Name;
            foreach (var a in d.Aliases ?? Array.Empty<string>()) yield return a;
        }
        yield return rawName;
    }

    // --- Online lookup ----------------------------------------------

    private void StartFetch(string targetName, string key, string lang, IReadOnlyList<string> titles) {
        if (_notes.MissIsFresh(key, lang)) return;
        var token = key + "|" + lang;
        lock (_lock) { if (!_inFlight.Add(token)) return; }
        // Detached on purpose. Nothing waits on this, and the card that is on
        // air already has a sentence in it.
        _ = Task.Run(async () => {
            try {
                var extract = await FetchAsync(titles, lang).ConfigureAwait(false);
                // A smaller Wikipedia often has no article where the English
                // one does. An English description beats no description.
                if (extract == null && lang != "en")
                    extract = await FetchAsync(titles, "en").ConfigureAwait(false);
                _notes.Remember(key, lang, extract);
                if (extract != null) { try { NoteFetched?.Invoke(targetName); } catch { } }
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Description lookup failed for {Title}", titles[0]);
            } finally {
                lock (_lock) { _inFlight.Remove(token); }
            }
        });
    }

    /// <summary>
    /// The pages to ask for, best first.
    ///
    /// <para>The designation comes first, and that is not the obvious order.
    /// The common name reads like the better guess, but the catalogue's common
    /// names are in English, and only the English Wikipedia has them: asking
    /// pt.wikipedia.org for "Orion Nebula" is a 404, while "M42" and "NGC 1976"
    /// both redirect to the Portuguese article. Designations are the one thing
    /// every language edition agrees on, so they are what a broadcast in
    /// Portuguese, Spanish, French or German has to ask with. Verified against
    /// the live endpoint.</para>
    /// </summary>
    internal static IReadOnlyList<string> WikipediaTitles(DsoCatalog.DsoObject? d, string rawName) {
        var titles = new List<string>();
        void Add(string? s) {
            if (string.IsNullOrWhiteSpace(s)) return;
            var v = s.Trim();
            if (!titles.Contains(v, StringComparer.OrdinalIgnoreCase)) titles.Add(v);
        }
        Add(d?.Name);
        Add(FirstOf(d?.CommonName));
        Add(rawName);
        // Two attempts per language is the budget. A third is another round
        // trip for an object that is looking less and less likely to be
        // written about, and the miss gets cached either way.
        return titles.Count > 2 ? titles.GetRange(0, 2) : titles;
    }

    private async Task<string?> FetchAsync(IReadOnlyList<string> titles, string lang) {
        foreach (var title in titles) {
            var extract = await FetchOneAsync(title, lang).ConfigureAwait(false);
            if (extract != null) return extract;
        }
        return null;
    }

    private async Task<string?> FetchOneAsync(string title, string lang) {
        // The REST summary endpoint: one small JSON, follows redirects, and the
        // lightest thing Wikipedia offers for exactly this question.
        var url = $"https://{lang}.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(title)}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // Wikimedia refuses requests without a descriptive agent, and being
        // identifiable is the polite half of using someone else's service.
        req.Headers.UserAgent.ParseAdd(UserAgent());

        using var resp = await _http.CreateClient().SendAsync(req, cts.Token).ConfigureAwait(false);
        // 404 is the normal answer for a PGC number nobody has written about.
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        var summary = JsonSerializer.Deserialize<WikiSummary>(body, _json);
        if (summary == null || string.IsNullOrWhiteSpace(summary.Extract)) return null;
        // A disambiguation page is a list of links, and reading one of those
        // out on a broadcast is worse than the generated sentence.
        if (string.Equals(summary.Type, "disambiguation", StringComparison.OrdinalIgnoreCase)) return null;
        return ObjectNoteStore.Shorten(summary.Extract);
    }

    private static string UserAgent() {
        var v = typeof(ObjectCardService).Assembly.GetName().Version?.ToString() ?? "1.0";
        return $"NINA.Polaris/{v} (https://github.com/DanWBR/NINA.Polaris)";
    }

    private sealed class WikiSummary {
        public string? Extract { get; set; }
        public string? Type { get; set; }
    }
}
