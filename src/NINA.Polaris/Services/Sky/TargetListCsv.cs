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
using System.Text;
using System.Text.RegularExpressions;

namespace NINA.Polaris.Services.Sky;

/// <summary>
/// Reads a target list exported as CSV (Telescopius "Export list", and any
/// spreadsheet with a name and two coordinate columns) into favourites.
/// Columns are found by their header names, so the column order and the
/// extra columns each site adds do not matter. Coordinates come in whatever
/// form the export used: "05h 35m 17s", "05:35:17.3", "05 35 17", decimal
/// hours, decimal degrees (a right ascension above 24 can only be degrees).
/// </summary>
public static class TargetListCsv {

    public record ParsedTarget(string Name, string? CommonName, string? Type, double RaHours, double DecDeg);
    public record ParseResult(List<ParsedTarget> Targets, List<string> Errors, int Skipped);

    private static readonly string[] NameHeaders = { "catalogue entry", "catalog entry", "name", "object", "target", "designation", "id" };
    private static readonly string[] CommonHeaders = { "familiar name", "common name", "commonname", "alias", "other name" };
    private static readonly string[] TypeHeaders = { "type", "object type", "category" };
    private static readonly string[] RaHeaders = { "right ascension", "ra (j2000)", "ra j2000", "ra2000", "raj2000", "ra" };
    private static readonly string[] DecHeaders = { "declination", "dec (j2000)", "dec j2000", "dec2000", "dej2000", "dec", "de" };

    public static ParseResult Parse(string csv) {
        var targets = new List<ParsedTarget>();
        var errors = new List<string>();
        int skipped = 0;
        if (string.IsNullOrWhiteSpace(csv)) return new(targets, errors, 0);

        var rows = ReadRows(csv);
        if (rows.Count == 0) return new(targets, errors, 0);

        // Header row: the first row that carries a recognisable name column
        // and both coordinates. Telescopius puts a title line above it.
        int headerIdx = -1;
        int nameCol = -1, commonCol = -1, typeCol = -1, raCol = -1, decCol = -1;
        for (int i = 0; i < Math.Min(rows.Count, 5); i++) {
            var h = rows[i].Select(c => c.Trim().ToLowerInvariant()).ToList();
            int n = Find(h, NameHeaders), r = Find(h, RaHeaders), d = Find(h, DecHeaders);
            if (n >= 0 && r >= 0 && d >= 0) {
                headerIdx = i; nameCol = n; raCol = r; decCol = d;
                commonCol = Find(h, CommonHeaders); typeCol = Find(h, TypeHeaders);
                break;
            }
        }
        if (headerIdx < 0) {
            errors.Add("No header row with a name, a right ascension and a declination column was found.");
            return new(targets, errors, 0);
        }

        for (int i = headerIdx + 1; i < rows.Count; i++) {
            var row = rows[i];
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            string Cell(int c) => c >= 0 && c < row.Count ? row[c].Trim() : "";
            var name = Cell(nameCol);
            if (name.Length == 0) { skipped++; continue; }
            var ra = ParseRaHours(Cell(raCol));
            var dec = ParseDecDeg(Cell(decCol));
            if (ra == null || dec == null) {
                errors.Add($"Line {i + 1} ({name}): could not read '{Cell(raCol)}' / '{Cell(decCol)}'.");
                skipped++;
                continue;
            }
            var common = Cell(commonCol);
            var type = Cell(typeCol);
            targets.Add(new ParsedTarget(name, common.Length > 0 ? common : null, type.Length > 0 ? type : null, ra.Value, dec.Value));
        }
        return new(targets, errors, skipped);
    }

    private static int Find(List<string> headers, string[] wanted) {
        foreach (var w in wanted) {
            int i = headers.IndexOf(w);
            if (i >= 0) return i;
        }
        return -1;
    }

    /// <summary>Right ascension in hours from any of the usual spellings. A
    /// bare decimal above 24 is taken as degrees; a trailing "d" or a degree
    /// sign says degrees outright.</summary>
    public static double? ParseRaHours(string text) {
        var s = Clean(text);
        if (s.Length == 0) return null;
        bool degrees = Regex.IsMatch(s, @"[d°]\s*$", RegexOptions.IgnoreCase) && !Regex.IsMatch(s, @"[hH]");
        var v = ParseSexagesimal(s);
        if (v == null) return null;
        bool bare = Regex.IsMatch(s, @"^[+-]?\d+(\.\d+)?\s*[hHdD°]?$");
        if (degrees || (bare && v.Value > 24.0)) v = v.Value / 15.0;
        if (v < 0 || v >= 24.0) return null;
        return v;
    }

    public static double? ParseDecDeg(string text) {
        var s = Clean(text);
        if (s.Length == 0) return null;
        var v = ParseSexagesimal(s);
        if (v == null || v < -90 || v > 90) return null;
        return v;
    }

    private static string Clean(string s) =>
        (s ?? "").Trim().Trim('"').Replace('′', '\'').Replace('’', '\'')
            .Replace('″', '"').Replace('”', '"').Replace(' ', ' ').Trim();

    /// <summary>Value in the unit of the leading part: sexagesimal with the
    /// usual separators (: space h m s d ° ' ") or a plain decimal.</summary>
    internal static double? ParseSexagesimal(string s) {
        double sign = 1;
        if (s.StartsWith('-')) { sign = -1; s = s[1..].Trim(); }
        else if (s.StartsWith('+')) { s = s[1..].Trim(); }
        var dec = Regex.Match(s, @"^(\d+(?:\.\d+)?)\s*[hHdD°]?$");
        if (dec.Success) return sign * double.Parse(dec.Groups[1].Value, CultureInfo.InvariantCulture);
        var parts = Regex.Split(s, @"[\s:hHdD°mM'""sS]+").Where(p => p.Length > 0).ToArray();
        if (parts.Length < 2 || parts.Length > 3) return null;
        if (!parts.All(p => Regex.IsMatch(p, @"^\d+(?:\.\d+)?$"))) return null;
        double a = double.Parse(parts[0], CultureInfo.InvariantCulture);
        double b = double.Parse(parts[1], CultureInfo.InvariantCulture);
        double c = parts.Length == 3 ? double.Parse(parts[2], CultureInfo.InvariantCulture) : 0;
        if (b >= 60 || c >= 60) return null;
        return sign * (a + b / 60 + c / 3600);
    }

    /// <summary>RFC 4180-ish reader: quoted fields, doubled quotes, commas or
    /// semicolons (whichever the header uses more), CRLF or LF.</summary>
    internal static List<List<string>> ReadRows(string csv) {
        var firstLine = csv.Split('\n')[0];
        char sep = firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : ',';
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < csv.Length; i++) {
            char ch = csv[i];
            if (quoted) {
                if (ch == '"') {
                    if (i + 1 < csv.Length && csv[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                } else cell.Append(ch);
                continue;
            }
            // A quote opens a quoted field only at the start of the field; one
            // in the middle is an arcsecond mark in an unquoted declination.
            if (ch == '"') { if (cell.Length == 0) quoted = true; else cell.Append('"'); continue; }
            if (ch == sep) { row.Add(cell.ToString()); cell.Clear(); continue; }
            if (ch == '\r') continue;
            if (ch == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new List<string>(); continue; }
            cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }
}
