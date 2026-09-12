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
using System.IO.Compression;
using System.Text;

namespace NINA.Polaris.Services;

/// <summary>
/// An offline place-name index: every town of 5,000 people or more, with its
/// coordinates, bundled into the host.
///
/// The rig at a dark site has no internet, and the phone or tablet driving it
/// may have no GPS either. Then the Observatory card's address search, which
/// asks Nominatim, has nothing to answer with, and the operator is left typing
/// coordinates from memory. This answers the same search from a table shipped
/// in the assembly, so the nearest town is always one tap away, and the
/// operator adjusts from there.
///
/// Data: GeoNames cities5000, admin1Codes and countryInfo (CC BY 4.0,
/// geonames.org), reduced to name, ASCII name, first-level division and its
/// abbreviation where one is customary, country, coordinates and population. About 1.6 MB compressed, read once on first use.
/// </summary>
public sealed class CityGazetteer {
    public sealed record City(string Name, string Ascii, string Admin1, string Admin1Abbr, string CountryCode,
                              string Country, double Latitude, double Longitude, long Population) {
        public string DisplayName =>
            string.IsNullOrEmpty(Admin1) ? $"{Name}, {Country}" : $"{Name}, {Admin1}, {Country}";
    }

    /// <summary>A row plus the folded keys it is matched on. Folding (accent
    /// strip, case, punctuation) is done once at load rather than per query,
    /// since a query walks all 70,000 rows and a Pi has better things to do
    /// than normalise Unicode two hundred thousand times per keystroke.</summary>
    internal sealed class Entry {
        public readonly City City;
        public readonly string NameKey, AsciiKey, AdminKey, AdminAbbr, CountryKey, CodeKey;
        public Entry(City c) {
            City = c;
            NameKey = Fold(c.Name);
            AsciiKey = string.IsNullOrEmpty(c.Ascii) ? NameKey : Fold(c.Ascii);
            AdminKey = Fold(c.Admin1);
            AdminAbbr = c.Admin1Abbr.ToLowerInvariant();
            CountryKey = Fold(c.Country);
            CodeKey = c.CountryCode.ToLowerInvariant();
        }
    }

    private readonly Lazy<IReadOnlyList<Entry>> _entries;

    public CityGazetteer() : this(LoadEmbedded) { }

    /// <summary>For tests: supply the table directly.</summary>
    internal CityGazetteer(Func<IReadOnlyList<City>> loader) {
        _entries = new Lazy<IReadOnlyList<Entry>>(
            () => loader().Select(c => new Entry(c)).ToList(),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int Count => _entries.Value.Count;

    /// <summary>Towns matching <paramref name="query"/>, best first.
    ///
    /// Accents and case do not matter ("mossoro" finds Mossoró). Extra words
    /// narrow by state or country ("mossoro rn", "santiago chile"), so a name
    /// shared by many places can be pinned down. Among equal matches the
    /// bigger town comes first, which is what someone typing "San Jose"
    /// almost always means.</summary>
    public IReadOnlyList<City> Search(string query, int limit = 5) {
        var terms = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return Array.Empty<City>();
        limit = Math.Clamp(limit, 1, 50);

        var scored = new List<(int Score, City City)>();
        foreach (var e in _entries.Value) {
            var score = ScoreCity(e, terms);
            if (score > 0) scored.Add((score, e.City));
        }
        return scored
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.City.Population)
            .Take(limit)
            .Select(s => s.City)
            .ToList();
    }

    /// <summary>The biggest towns inside a latitude/longitude box, for the map
    /// picker's labels. A box that crosses the antimeridian (west > east) is
    /// honoured. Bigger first, so at a given zoom the labels that fit are the
    /// ones a person would recognise.</summary>
    public IReadOnlyList<City> InBox(double south, double north, double west, double east, int limit = 100) {
        limit = Math.Clamp(limit, 1, 1000);
        bool wraps = west > east;
        var hits = new List<City>();
        foreach (var e in _entries.Value) {
            var c = e.City;
            if (c.Latitude < south || c.Latitude > north) continue;
            bool inLon = wraps ? (c.Longitude >= west || c.Longitude <= east)
                               : (c.Longitude >= west && c.Longitude <= east);
            if (!inLon) continue;
            hits.Add(c);
        }
        // The table is population-sorted on disk, but do not rely on it.
        return hits.OrderByDescending(c => c.Population).Take(limit).ToList();
    }

    /// <summary>How well one town answers the terms. 0 = not a match. The
    /// first term must be found in the town's name; every other term must be
    /// found in the name, the division, the division's abbreviation ("rn" for
    /// Rio Grande do Norte, "ca" for California), the country or its code. Exact name beats prefix
    /// beats substring, and each qualifier that fits adds a little, so
    /// "santiago chile" outranks the other Santiagos.</summary>
    internal static int ScoreCity(City c, string[] terms) => ScoreCity(new Entry(c), terms);

    internal static int ScoreCity(Entry e, string[] terms) {
        var name = e.NameKey;
        var ascii = e.AsciiKey;
        var joined = string.Join(' ', terms);

        int best = 0;
        foreach (var n in new[] { name, ascii }) {
            int s;
            if (n == joined) s = 100;
            else if (n == terms[0]) s = 90;
            else if (n.StartsWith(joined + " ", StringComparison.Ordinal)) s = 80;
            else if (n.StartsWith(terms[0], StringComparison.Ordinal)) s = 60;
            else if (n.Contains(terms[0], StringComparison.Ordinal)) s = 30;
            else s = 0;
            if (s > best) best = s;
        }
        if (best == 0) return 0;

        // The remaining terms must all land somewhere on the row.
        var admin = e.AdminKey;
        var adminAbbr = e.AdminAbbr;
        var country = e.CountryKey;
        var cc = e.CodeKey;
        for (int i = 1; i < terms.Length; i++) {
            var t = terms[i];
            var ok = name.Contains(t, StringComparison.Ordinal)
                  || ascii.Contains(t, StringComparison.Ordinal)
                  || admin.Contains(t, StringComparison.Ordinal)
                  || (adminAbbr.Length > 0 && adminAbbr == t)
                  || country.Contains(t, StringComparison.Ordinal)
                  || cc == t;
            if (!ok) return 0;
            best += 5;   // a qualifier that fits is a better answer than one without
        }
        return best;
    }

    /// <summary>Lower-case, accents stripped, punctuation to spaces, one space
    /// between words: the form both the query and the table are compared in.</summary>
    internal static string Fold(string s) {
        if (string.IsNullOrEmpty(s)) return "";
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        bool space = false;
        foreach (var ch in d) {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) { sb.Append(char.ToLowerInvariant(ch)); space = false; }
            else if (!space && sb.Length > 0) { sb.Append(' '); space = true; }
        }
        return sb.ToString().Trim();
    }

    // ----- loading -----

    private static IReadOnlyList<City> LoadEmbedded() {
        var asm = typeof(CityGazetteer).Assembly;
        var countries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var cs = asm.GetManifestResourceStream("NINA.Polaris.Resources.geo.countries.tsv")
                ?? throw new InvalidOperationException("countries.tsv resource missing")) {
            using var r = new StreamReader(cs, Encoding.UTF8);
            string? line;
            while ((line = r.ReadLine()) != null) {
                var p = line.Split('\t');
                if (p.Length >= 2) countries[p[0]] = p[1];
            }
        }
        using var gz = asm.GetManifestResourceStream("NINA.Polaris.Resources.geo.cities5000.tsv.gz")
            ?? throw new InvalidOperationException("cities5000.tsv.gz resource missing");
        using var inflate = new GZipStream(gz, CompressionMode.Decompress);
        return Parse(inflate, countries);
    }

    internal static IReadOnlyList<City> Parse(Stream tsv, IReadOnlyDictionary<string, string> countries) {
        var list = new List<City>(70_000);
        using var r = new StreamReader(tsv, Encoding.UTF8);
        string? line;
        while ((line = r.ReadLine()) != null) {
            var p = line.Split('\t');
            if (p.Length < 8) continue;
            if (!double.TryParse(p[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) continue;
            if (!double.TryParse(p[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) continue;
            long.TryParse(p[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pop);
            var cc = p[4];
            list.Add(new City(p[0], p[1], p[2], p[3], cc,
                countries.TryGetValue(cc, out var cn) ? cn : cc, lat, lon, pop));
        }
        return list;
    }
}
