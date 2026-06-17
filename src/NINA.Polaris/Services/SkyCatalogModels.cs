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

// Data models / DTOs extracted from SkyCatalogService.cs for readability.
// Plain serialisable types owned by SkyCatalogService; no behaviour here.

namespace NINA.Polaris.Services;

public class CatalogFilter {
    public string? Query { get; set; }
    public string? Type { get; set; }
    public double? MinMagnitude { get; set; }
    public double? MaxMagnitude { get; set; }
    public double? MinDec { get; set; }
    public double? MaxDec { get; set; }
    /// <summary>CAT-3: catalog source filter ("NGC", "Arp", "Sh2", ...).
    /// Only meaningful when the expanded DsoCatalog DB is loaded;
    /// ignored by the legacy hardcoded fallback.</summary>
    public string? Catalog { get; set; }
    /// <summary>CAT-3: 3-letter IAU constellation abbreviation
    /// ("And", "Ori", ...). Only meaningful with DsoCatalog.</summary>
    public string? Constellation { get; set; }
}

public class CatalogObject {
    public string Name { get; set; } = "";
    public double Ra { get; set; }
    public double Dec { get; set; }
    public double Magnitude { get; set; }
    public string Type { get; set; } = "";
    public string? CommonName { get; set; }
    public string[] Aliases { get; set; } = [];
    /// <summary>CAT-3: source catalog ("NGC", "M", "Arp", ...). Empty
    /// when hand-built from the legacy hardcoded list. Useful for
    /// grouping in the Atlas filter UI.</summary>
    public string? Catalog { get; set; }
    /// <summary>CAT-3: identifier inside the source catalog ("7331",
    /// "31", "273"). Pairs with Catalog to reconstruct the full name.</summary>
    public string? CatalogId { get; set; }
    /// <summary>CAT-3: 3-letter IAU constellation abbreviation
    /// (e.g. "Cyg" for Cygnus). Null on hardcoded entries.</summary>
    public string? Constellation { get; set; }
    /// <summary>CAT-3: major-axis angular diameter in arcmin. Null
    /// when the source catalog didn't carry size info.</summary>
    public double? SizeArcmin { get; set; }

    public string RaFormatted {
        get {
            var h = (int)Ra;
            var m = (int)((Ra - h) * 60);
            var s = ((Ra - h) * 60 - m) * 60;
            return $"{h:D2}h {m:D2}m {s:00.0}s";
        }
    }

    public string DecFormatted {
        get {
            var sign = Dec >= 0 ? "+" : "-";
            var abs = Math.Abs(Dec);
            var d = (int)abs;
            var m = (int)((abs - d) * 60);
            var s = ((abs - d) * 60 - m) * 60;
            return $"{sign}{d}° {m:D2}' {s:00}\"";
        }
    }
}

/// <summary>Result of <see cref="SkyCatalogService.Identify"/>: the matched
/// object plus how far the pointing was from its centre, and whether the
/// pointing fell inside the object's catalogued extent.</summary>
public class IdentifyResult {
    public required CatalogObject Object { get; set; }
    public double SeparationArcmin { get; set; }
    public bool WithinExtent { get; set; }
}