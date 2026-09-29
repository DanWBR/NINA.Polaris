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

using System.Text;
using System.Text.Json;
using NINA.Polaris.Services.Sky;

namespace NINA.Polaris.Services.Broadcast;

/// <summary>
/// The two places an object description can come from without asking anyone:
/// the texts Polaris ships, and the texts a previous night fetched and kept.
///
/// <para>Separated from <see cref="ObjectCardService"/> so the rules that
/// matter, which language wins, when a cached text has aged out, and how long
/// "there is no article about this" is believed, can be tested against real
/// files without a catalogue, a camera or a network.</para>
/// </summary>
public sealed class ObjectNoteStore {

    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>How long a fetched description is kept. These texts change on
    /// the scale of years, and the host is often offline when it needs one.</summary>
    public static readonly TimeSpan HitTtl = TimeSpan.FromDays(180);

    /// <summary>How long "Wikipedia has no article for this" is trusted.
    /// Shorter than a hit, but long enough that pointing at the same obscure
    /// PGC galaxy every night is not a request every night.</summary>
    public static readonly TimeSpan MissTtl = TimeSpan.FromDays(30);

    private readonly string _notesDir;
    private readonly string _cacheDir;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, string>> _bundled = new(StringComparer.Ordinal);

    public ObjectNoteStore(string notesDir, string cacheDir, ILogger logger) {
        _notesDir = notesDir;
        _cacheDir = cacheDir;
        _logger = logger;
    }

    /// <summary>The key a note is filed under: the catalogue designation,
    /// normalised the way the cutouts are, so "M 42", "m42" and "M42" are one
    /// entry. A name that is not a designation is filed under itself.</summary>
    public static string KeyFor(string name) {
        var slug = DsoThumbSlug.For(name);
        return slug.Length > 0 ? slug : (name ?? "").Trim().ToUpperInvariant();
    }

    // --- Bundled ----------------------------------------------------

    /// <summary>
    /// Our own text for this object, or null.
    ///
    /// <para>Falls back to English per entry rather than per file. The English
    /// set is the complete one and the others are filled in over time, so a
    /// half translated catalogue has to yield the English sentence for the
    /// entries it has not reached rather than nothing at all.</para>
    /// </summary>
    public string? Bundled(string key, string lang) {
        if (Notes(lang).TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text)) return text;
        if (lang != "en" && Notes("en").TryGetValue(key, out var en) && !string.IsNullOrWhiteSpace(en)) return en;
        return null;
    }

    private Dictionary<string, string> Notes(string lang) {
        lock (_lock) {
            if (_bundled.TryGetValue(lang, out var cached)) return cached;
            var loaded = Load(Path.Combine(_notesDir, $"object-notes.{lang}.json"));
            _bundled[lang] = loaded;
            return loaded;
        }
    }

    private Dictionary<string, string> Load(string path) {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try {
            if (!File.Exists(path)) return result;
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), _json);
            if (parsed == null) return result;
            // Keyed on the way in, so the file can be written "M 42" or "NGC
            // 0224" and still be found by the designation the catalogue uses.
            foreach (var (k, v) in parsed) result[KeyFor(k)] = v;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Object notes unreadable at {Path}", path);
        }
        return result;
    }

    // --- Cached fetches ---------------------------------------------

    public string CachePath(string key, string lang) =>
        Path.Combine(_cacheDir, lang, SafeFileName(key) + ".json");

    /// <summary>A previously fetched description, or null when there is none,
    /// it has aged out, or what was cached was a miss.</summary>
    public string? Cached(string key, string lang) {
        var entry = ReadEntry(key, lang);
        if (entry == null) return null;
        if (DateTime.UtcNow - entry.FetchedUtc > (entry.Found ? HitTtl : MissTtl)) return null;
        return entry.Found && !string.IsNullOrWhiteSpace(entry.Extract) ? entry.Extract : null;
    }

    /// <summary>True when the last lookup said there is nothing and that answer
    /// is still fresh. The reason the cache stores misses at all: without it,
    /// an object with no article is looked up again on every target change.</summary>
    public bool MissIsFresh(string key, string lang) {
        var entry = ReadEntry(key, lang);
        return entry is { Found: false } && DateTime.UtcNow - entry.FetchedUtc <= MissTtl;
    }

    /// <summary>Whether a lookup is worth making: nothing bundled, nothing
    /// usable cached, and no fresh miss.</summary>
    public bool ShouldFetch(string key, string lang) =>
        Bundled(key, lang) == null && Cached(key, lang) == null && !MissIsFresh(key, lang);

    /// <summary>Record the result, including a null one. Caching the absence is
    /// the point.</summary>
    public void Remember(string key, string lang, string? extract) {
        try {
            var path = CachePath(key, lang);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new CachedNote {
                Extract = extract ?? "",
                Found = extract != null,
                FetchedUtc = DateTime.UtcNow
            }));
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Could not cache the description for {Key}", key);
        }
    }

    private CachedNote? ReadEntry(string key, string lang) {
        try {
            var path = CachePath(key, lang);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<CachedNote>(File.ReadAllText(path), _json);
        } catch { return null; }
    }

    private static string SafeFileName(string key) {
        var sb = new StringBuilder(key.Length);
        foreach (var c in key) sb.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        var s = sb.ToString();
        return s.Length is > 0 and <= 64 ? s : (s.Length == 0 ? "_" : s[..64]);
    }

    /// <summary>
    /// Cut a fetched paragraph down to what fits on the card: whole sentences,
    /// about three lines. The alternative is either a card that overflows or
    /// type shrunk until nobody watching on a phone can read it.
    /// </summary>
    public static string Shorten(string text, int max = 320) {
        var t = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        while (t.Contains("  ", StringComparison.Ordinal)) t = t.Replace("  ", " ", StringComparison.Ordinal);
        if (t.Length <= max) return t;
        var cut = t[..max];
        var stop = cut.LastIndexOf(". ", StringComparison.Ordinal);
        // Only cut at a full stop if that leaves a sensible amount of text;
        // otherwise a first sentence of four words would be the whole card.
        if (stop > max / 3) return cut[..(stop + 1)];
        var space = cut.LastIndexOf(' ');
        return (space > 0 ? cut[..space] : cut).TrimEnd(',', ';', ' ', '.') + "...";
    }

    private sealed class CachedNote {
        public string Extract { get; set; } = "";
        public bool Found { get; set; }
        public DateTime FetchedUtc { get; set; }
    }
}
