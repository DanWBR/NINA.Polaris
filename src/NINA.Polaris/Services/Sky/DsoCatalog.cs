using Microsoft.Data.Sqlite;

namespace NINA.Polaris.Services.Sky;

/// <summary>
/// Bundled DSO catalog used by the SKY tab search, Atlas filter
/// panel, and Tonight's Best ranking. Ships as a SQLite + R*tree
/// database under <c>wwwroot/catalogs/dso/dso.db</c>, generated
/// once by <c>scripts/build-dso-catalog.py</c> and committed to
/// the repo (~2.6 MB).
///
/// Schema (matches scripts/build-dso-catalog.py):
///   objects(id, catalog, catalog_id, name, common_name, type,
///           ra_hours REAL, dec_deg REAL, magnitude REAL,
///           size_arcmin REAL, constellation TEXT, aliases TEXT)
///   objects_idx VIRTUAL TABLE USING rtree(
///       id, min_ra, max_ra, min_dec, max_dec)
///
/// Note RA is in hours (0..24), Dec in degrees. The R*tree mirrors
/// these directly; cone searches do an R*tree bounding-box pre-filter
/// then a great-circle distance check per candidate (same trick as
/// <see cref="ApassCatalog"/>).
///
/// If the .db is missing, <see cref="IsAvailable"/> returns false
/// and every query short-circuits to an empty result. The legacy
/// hardcoded <c>SkyCatalogService</c> still runs in that case
/// (CAT-3 wires it as fallback).
/// </summary>
public class DsoCatalog {
    private readonly string _dbPath;
    private readonly ILogger<DsoCatalog> _logger;
    private long? _objectCountCache;

    public DsoCatalog(IWebHostEnvironment env, ILogger<DsoCatalog> logger) {
        _logger = logger;
        var webRoot = env.WebRootPath
            ?? Path.Combine(env.ContentRootPath, "wwwroot");
        _dbPath = Path.Combine(webRoot, "catalogs", "dso", "dso.db");
    }

    public string DbPath => _dbPath;

    public bool IsAvailable => File.Exists(_dbPath);

    /// <summary>Total row count, cached. Used for status / sanity checks.</summary>
    public long ObjectCount {
        get {
            if (_objectCountCache.HasValue) return _objectCountCache.Value;
            if (!IsAvailable) return 0;
            try {
                using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM objects";
                var v = cmd.ExecuteScalar();
                _objectCountCache = Convert.ToInt64(v ?? 0L);
                return _objectCountCache.Value;
            } catch (Exception ex) {
                _logger.LogWarning(ex, "DSO catalog count query failed");
                return 0;
            }
        }
    }

    /// <summary>One DSO entry as exposed to callers. RA in hours (0..24),
    /// Dec in degrees. Magnitude / size / constellation / aliases are
    /// nullable for catalogs that don't carry them (e.g. Sh2 has no
    /// magnitude, ACO has no size).</summary>
    public record DsoObject(
        string Name, string? CommonName, string Type,
        double RaHours, double DecDeg,
        double? Magnitude, double? SizeArcmin,
        string? Constellation, string Catalog, string CatalogId,
        string[] Aliases);

    /// <summary>Filter shape consumed by <see cref="FilterAsync"/>.</summary>
    public record DsoFilter(
        string? Query = null, string? Type = null, string? Catalog = null,
        string? Constellation = null,
        double? MinMagnitude = null, double? MaxMagnitude = null,
        double? MinDec = null, double? MaxDec = null,
        int Limit = 100);

    /// <summary>Exact name lookup (case-insensitive). Returns null on miss.</summary>
    public Task<DsoObject?> GetByNameAsync(string name, CancellationToken ct = default)
        => Task.Run(() => GetByNameSync(name), ct);

    private DsoObject? GetByNameSync(string name) {
        if (!IsAvailable || string.IsNullOrWhiteSpace(name)) return null;
        try {
            using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            // Lookup against both the canonical `name` and the
            // pipe-separated `aliases` blob, so a search for "M31"
            // matches the NGC 224 entry whose aliases include "M31"
            // (and vice versa). LIKE pattern bounded by pipes
            // mimics whole-token match.
            cmd.CommandText = @"
                SELECT catalog, catalog_id, name, common_name, type,
                       ra_hours, dec_deg, magnitude, size_arcmin,
                       constellation, aliases
                FROM objects
                WHERE name = $name COLLATE NOCASE
                   OR aliases LIKE $aliasLike COLLATE NOCASE
                LIMIT 1";
            cmd.Parameters.AddWithValue("$name", name.Trim());
            cmd.Parameters.AddWithValue("$aliasLike", $"%{name.Trim()}%");
            using var rdr = cmd.ExecuteReader();
            return rdr.Read() ? ReadDso(rdr) : null;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "DSO GetByName('{Name}') failed", name);
            return null;
        }
    }

    /// <summary>Free-text search: prefix match on name OR substring on
    /// common_name, with magnitude-asc ranking so brighter alternatives
    /// come first when there are multiple hits.</summary>
    public Task<IReadOnlyList<DsoObject>> SearchAsync(string query, int limit = 20,
            CancellationToken ct = default)
        => Task.Run<IReadOnlyList<DsoObject>>(() => SearchSync(query, limit), ct);

    private IReadOnlyList<DsoObject> SearchSync(string query, int limit) {
        if (!IsAvailable || string.IsNullOrWhiteSpace(query)) return Array.Empty<DsoObject>();
        var q = query.Trim();
        try {
            using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            // ORDER BY name = $exact DESC pins exact-match rows first,
            // then magnitude ASC (NULLs last) for browse-friendly
            // ranking.
            cmd.CommandText = @"
                SELECT catalog, catalog_id, name, common_name, type,
                       ra_hours, dec_deg, magnitude, size_arcmin,
                       constellation, aliases
                FROM objects
                WHERE name LIKE $like COLLATE NOCASE
                   OR common_name LIKE $like COLLATE NOCASE
                   OR aliases LIKE $aliasLike COLLATE NOCASE
                ORDER BY (name = $exact COLLATE NOCASE) DESC,
                         CASE WHEN magnitude IS NULL THEN 1 ELSE 0 END,
                         magnitude ASC
                LIMIT $limit";
            cmd.Parameters.AddWithValue("$like", $"{q}%");
            cmd.Parameters.AddWithValue("$aliasLike", $"%{q}%");
            cmd.Parameters.AddWithValue("$exact", q);
            cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));
            return ReadAll(cmd);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "DSO Search('{Query}') failed", query);
            return Array.Empty<DsoObject>();
        }
    }

    /// <summary>Atlas filter: combine type / catalog / constellation /
    /// magnitude / declination range. Empty filter returns the
    /// brightest <paramref name="Limit"/> rows.</summary>
    public Task<IReadOnlyList<DsoObject>> FilterAsync(DsoFilter filter,
            CancellationToken ct = default)
        => Task.Run<IReadOnlyList<DsoObject>>(() => FilterSync(filter), ct);

    private IReadOnlyList<DsoObject> FilterSync(DsoFilter f) {
        if (!IsAvailable) return Array.Empty<DsoObject>();
        try {
            using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            var sql = @"
                SELECT catalog, catalog_id, name, common_name, type,
                       ra_hours, dec_deg, magnitude, size_arcmin,
                       constellation, aliases
                FROM objects
                WHERE 1=1";
            if (!string.IsNullOrWhiteSpace(f.Query)) {
                sql += " AND (name LIKE $like COLLATE NOCASE OR common_name LIKE $like COLLATE NOCASE)";
                cmd.Parameters.AddWithValue("$like", $"%{f.Query.Trim()}%");
            }
            if (!string.IsNullOrWhiteSpace(f.Type)) {
                sql += " AND type = $type COLLATE NOCASE";
                cmd.Parameters.AddWithValue("$type", f.Type.Trim());
            }
            if (!string.IsNullOrWhiteSpace(f.Catalog)) {
                sql += " AND catalog = $catalog COLLATE NOCASE";
                cmd.Parameters.AddWithValue("$catalog", f.Catalog.Trim());
            }
            if (!string.IsNullOrWhiteSpace(f.Constellation)) {
                sql += " AND constellation = $const COLLATE NOCASE";
                cmd.Parameters.AddWithValue("$const", f.Constellation.Trim());
            }
            if (f.MinMagnitude.HasValue) {
                sql += " AND magnitude IS NOT NULL AND magnitude >= $minMag";
                cmd.Parameters.AddWithValue("$minMag", f.MinMagnitude.Value);
            }
            if (f.MaxMagnitude.HasValue) {
                sql += " AND magnitude IS NOT NULL AND magnitude <= $maxMag";
                cmd.Parameters.AddWithValue("$maxMag", f.MaxMagnitude.Value);
            }
            if (f.MinDec.HasValue) {
                sql += " AND dec_deg >= $minDec";
                cmd.Parameters.AddWithValue("$minDec", f.MinDec.Value);
            }
            if (f.MaxDec.HasValue) {
                sql += " AND dec_deg <= $maxDec";
                cmd.Parameters.AddWithValue("$maxDec", f.MaxDec.Value);
            }
            sql += @"
                ORDER BY CASE WHEN magnitude IS NULL THEN 1 ELSE 0 END,
                         magnitude ASC
                LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", Math.Max(1, f.Limit));
            cmd.CommandText = sql;
            return ReadAll(cmd);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "DSO Filter failed");
            return Array.Empty<DsoObject>();
        }
    }

    /// <summary>Cone search: objects within <paramref name="radiusDeg"/>
    /// of the given (RA hours, Dec deg), optionally mag-limited.
    /// Uses R*tree pre-filter then great-circle exact distance.
    /// Useful for "what's in my FOV" + future Mosaic-suggest paths.</summary>
    public Task<IReadOnlyList<DsoObject>> QueryRegionAsync(
            double raHours, double decDeg, double radiusDeg,
            double? magLimit = null, int limit = 200,
            CancellationToken ct = default)
        => Task.Run<IReadOnlyList<DsoObject>>(
            () => QueryRegionSync(raHours, decDeg, radiusDeg, magLimit, limit), ct);

    private IReadOnlyList<DsoObject> QueryRegionSync(
            double raHours, double decDeg, double radiusDeg,
            double? magLimit, int limit) {
        if (!IsAvailable) return Array.Empty<DsoObject>();
        try {
            // R*tree bounds in catalog units: RA hours, Dec degrees.
            double cosDec = Math.Cos(Math.Max(-89.0, Math.Min(89.0, decDeg))
                                     * Math.PI / 180.0);
            if (cosDec < 0.01) cosDec = 0.01;
            // Pad RA bounds by radiusDeg expressed in hours (1 hr = 15°).
            double raPadHours = Math.Min(12.0, (radiusDeg / cosDec) / 15.0);
            double decPad = radiusDeg;

            double minDec = Math.Max(-90.0, decDeg - decPad);
            double maxDec = Math.Min( 90.0, decDeg + decPad);
            double minRa = raHours - raPadHours;
            double maxRa = raHours + raPadHours;

            using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
            conn.Open();
            var results = new List<DsoObject>();
            // Wrap split (RA crosses 0h or 24h).
            if (minRa < 0 || maxRa >= 24) {
                double leftLo = (minRa + 24.0) % 24.0;
                QueryConeRange(conn, leftLo, 24.0, minDec, maxDec,
                    raHours, decDeg, radiusDeg, magLimit, limit, results);
                double rightHi = (maxRa + 24.0) % 24.0;
                QueryConeRange(conn, 0.0, rightHi, minDec, maxDec,
                    raHours, decDeg, radiusDeg, magLimit, limit, results);
            } else {
                QueryConeRange(conn, minRa, maxRa, minDec, maxDec,
                    raHours, decDeg, radiusDeg, magLimit, limit, results);
            }
            return results;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "DSO QueryRegion failed");
            return Array.Empty<DsoObject>();
        }
    }

    private void QueryConeRange(SqliteConnection conn,
            double minRa, double maxRa, double minDec, double maxDec,
            double centerRaH, double centerDecDeg, double radiusDeg,
            double? magLimit, int limit, List<DsoObject> results) {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT catalog, catalog_id, name, common_name, type,
                   ra_hours, dec_deg, magnitude, size_arcmin,
                   constellation, aliases
            FROM objects o
            JOIN objects_idx idx ON o.id = idx.id
            WHERE idx.min_ra <= $maxRa AND idx.max_ra >= $minRa
              AND idx.min_dec <= $maxDec AND idx.max_dec >= $minDec
              AND ($magLimit IS NULL OR (o.magnitude IS NOT NULL AND o.magnitude <= $magLimit))";
        cmd.Parameters.AddWithValue("$minRa", minRa);
        cmd.Parameters.AddWithValue("$maxRa", maxRa);
        cmd.Parameters.AddWithValue("$minDec", minDec);
        cmd.Parameters.AddWithValue("$maxDec", maxDec);
        cmd.Parameters.AddWithValue("$magLimit", (object?)magLimit ?? DBNull.Value);

        double radR = radiusDeg * Math.PI / 180.0;
        double cosR = Math.Cos(radR);
        double centerRaDeg = centerRaH * 15.0;
        double sinCDec = Math.Sin(centerDecDeg * Math.PI / 180.0);
        double cosCDec = Math.Cos(centerDecDeg * Math.PI / 180.0);

        using var rdr = cmd.ExecuteReader();
        while (rdr.Read() && results.Count < limit) {
            double raH = rdr.GetDouble(5);
            double dec = rdr.GetDouble(6);
            double dra = (raH * 15.0 - centerRaDeg) * Math.PI / 180.0;
            double sinDec = Math.Sin(dec * Math.PI / 180.0);
            double cosDec = Math.Cos(dec * Math.PI / 180.0);
            double cosTheta = sinCDec * sinDec + cosCDec * cosDec * Math.Cos(dra);
            if (cosTheta < cosR) continue;
            results.Add(ReadDso(rdr));
        }
    }

    /// <summary>Distinct catalog IDs present in the DB
    /// ('NGC', 'IC', 'M', 'C', 'Arp', 'Sh2', 'HCG', 'AGC').
    /// Drives the Atlas filter's catalog dropdown.</summary>
    public Task<IReadOnlyList<string>> GetCatalogsAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<string>>(() => GetCatalogsSync(), ct);

    private IReadOnlyList<string> GetCatalogsSync() {
        if (!IsAvailable) return Array.Empty<string>();
        try {
            using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT catalog FROM objects ORDER BY catalog";
            var list = new List<string>();
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read()) list.Add(rdr.GetString(0));
            return list;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "DSO GetCatalogs failed");
            return Array.Empty<string>();
        }
    }

    /// <summary>Distinct type strings present in the DB. Drives the
    /// Atlas filter's type dropdown.</summary>
    public Task<IReadOnlyList<string>> GetTypesAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<string>>(() => GetTypesSync(), ct);

    private IReadOnlyList<string> GetTypesSync() {
        if (!IsAvailable) return Array.Empty<string>();
        try {
            using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT type FROM objects ORDER BY type";
            var list = new List<string>();
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read()) list.Add(rdr.GetString(0));
            return list;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "DSO GetTypes failed");
            return Array.Empty<string>();
        }
    }

    /// <summary>Stream every row brighter than <paramref name="magCap"/>
    /// (or all rows when null). Used by SkyCatalogService to lazy-build
    /// its in-memory AllObjects cache for TonightsBestService iteration.
    /// Mag cap defaults to 12 so the Pi 2/3 in-memory footprint stays
    /// bounded (~5k rows × ~200 B = ~1 MB).</summary>
    public Task<IReadOnlyList<DsoObject>> LoadAllAsync(double? magCap = 12.0,
            CancellationToken ct = default)
        => Task.Run<IReadOnlyList<DsoObject>>(() => LoadAllSync(magCap), ct);

    private IReadOnlyList<DsoObject> LoadAllSync(double? magCap) {
        if (!IsAvailable) return Array.Empty<DsoObject>();
        try {
            using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            if (magCap.HasValue) {
                cmd.CommandText = @"
                    SELECT catalog, catalog_id, name, common_name, type,
                           ra_hours, dec_deg, magnitude, size_arcmin,
                           constellation, aliases
                    FROM objects
                    WHERE magnitude IS NOT NULL AND magnitude <= $cap";
                cmd.Parameters.AddWithValue("$cap", magCap.Value);
            } else {
                cmd.CommandText = @"
                    SELECT catalog, catalog_id, name, common_name, type,
                           ra_hours, dec_deg, magnitude, size_arcmin,
                           constellation, aliases
                    FROM objects";
            }
            return ReadAll(cmd);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "DSO LoadAll failed");
            return Array.Empty<DsoObject>();
        }
    }

    // ---- helpers ----

    private static List<DsoObject> ReadAll(SqliteCommand cmd) {
        var list = new List<DsoObject>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) list.Add(ReadDso(rdr));
        return list;
    }

    private static DsoObject ReadDso(SqliteDataReader rdr) {
        string catalog    = rdr.GetString(0);
        string catalogId  = rdr.GetString(1);
        string name       = rdr.GetString(2);
        string? common    = rdr.IsDBNull(3) ? null : rdr.GetString(3);
        string type       = rdr.GetString(4);
        double raH        = rdr.GetDouble(5);
        double decD       = rdr.GetDouble(6);
        double? mag       = rdr.IsDBNull(7) ? null : rdr.GetDouble(7);
        double? size      = rdr.IsDBNull(8) ? null : rdr.GetDouble(8);
        string? constel   = rdr.IsDBNull(9) ? null : rdr.GetString(9);
        string? aliasesRaw= rdr.IsDBNull(10) ? null : rdr.GetString(10);
        var aliases       = string.IsNullOrEmpty(aliasesRaw)
            ? Array.Empty<string>()
            : aliasesRaw.Split('|', StringSplitOptions.RemoveEmptyEntries);
        return new DsoObject(name, common, type, raH, decD, mag, size,
            constel, catalog, catalogId, aliases);
    }
}
