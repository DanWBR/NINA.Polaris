using Microsoft.Data.Sqlite;

namespace NINA.Polaris.Services.Sky;

/// <summary>
/// Bundled APASS DR10 star catalog used by the Photometric Color
/// Calibration (PCC) workflow (CCALB-3). Catalog ships as a SQLite
/// file under <c>wwwroot/catalogs/apass/apass.db</c>, populated
/// once by <c>scripts/download-apass.py</c> on the deployment host.
/// The .db file is gitignored (~80 MB) so the repo stays light;
/// publishing copies it via the csproj Content Include rule.
///
/// Schema:
///   stars(id INTEGER PRIMARY KEY, ra REAL, dec REAL,
///         mag_v REAL, mag_b REAL, b_v REAL, source TEXT)
///   stars_idx VIRTUAL TABLE USING rtree(id, min_ra, max_ra,
///                                       min_dec, max_dec)
///
/// Cone search: bounding box via R*tree (O(log n) on millions of
/// rows), then exact great-circle distance filter on the candidate
/// rows. Fast enough on a Raspberry Pi 2 (~1 ms per query on a
/// 5-million-row catalog).
///
/// If the .db file is missing, <see cref="IsAvailable"/> returns
/// false and <see cref="QueryRegionAsync"/> throws a clear error
/// telling the user to run the download script.
/// </summary>
public class ApassCatalog {
    private readonly string _dbPath;
    private readonly ILogger<ApassCatalog> _logger;
    private long? _starCountCache;

    public ApassCatalog(IWebHostEnvironment env, ILogger<ApassCatalog> logger) {
        _logger = logger;
        var webRoot = env.WebRootPath
            ?? Path.Combine(env.ContentRootPath, "wwwroot");
        _dbPath = Path.Combine(webRoot, "catalogs", "apass", "apass.db");
    }

    /// <summary>Absolute path to the catalog DB on disk.</summary>
    public string DbPath => _dbPath;

    /// <summary>True when the catalog has been populated (the
    /// download script has run + the .db file is on disk).</summary>
    public bool IsAvailable => File.Exists(_dbPath);

    /// <summary>
    /// Total star count, cached after the first query so the status
    /// endpoint can show "5.2M stars indexed" without a per-request
    /// COUNT.
    /// </summary>
    public long StarCount {
        get {
            if (_starCountCache.HasValue) return _starCountCache.Value;
            if (!IsAvailable) return 0;
            try {
                using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM stars";
                var v = cmd.ExecuteScalar();
                _starCountCache = Convert.ToInt64(v ?? 0L);
                return _starCountCache.Value;
            } catch (Exception ex) {
                _logger.LogWarning(ex, "APASS star count query failed");
                return 0;
            }
        }
    }

    /// <summary>
    /// One catalog entry. RA/Dec in degrees, magnitudes in the V
    /// + B Johnson bands. B-V is the colour index (B mag minus V
    /// mag); for a G2V star (Sun-like) it's about 0.65. Null when
    /// the source catalog only provided one band.
    /// </summary>
    public record CatalogStar(double Ra, double Dec, double? MagV,
        double? MagB, double? Bv, string Source);

    /// <summary>
    /// Cone search: return all catalog stars within
    /// <paramref name="radiusDeg"/> of (<paramref name="raDeg"/>,
    /// <paramref name="decDeg"/>), optionally filtered by maximum
    /// V magnitude.
    /// </summary>
    public Task<List<CatalogStar>> QueryRegionAsync(double raDeg, double decDeg,
            double radiusDeg, double? magLimit = null,
            CancellationToken ct = default)
        => Task.Run<List<CatalogStar>>(() => QueryRegionSync(raDeg, decDeg, radiusDeg, magLimit), ct);

    private List<CatalogStar> QueryRegionSync(double raDeg, double decDeg,
            double radiusDeg, double? magLimit) {
        if (!IsAvailable) {
            throw new InvalidOperationException(
                $"APASS catalog not found at {_dbPath}. " +
                "Run `python scripts/download-apass.py` on the server to " +
                "populate it (~80 MB download).");
        }
        // Bounding-box pre-filter via R*tree. For a small radius
        // near the celestial pole, RA-bound widens dramatically as
        // 1/cos(Dec). Clamp to a sane max so we don't end up
        // scanning the whole catalog for a 1-degree query at Dec=89°.
        double cosDec = Math.Cos(Math.Max(-89.0, Math.Min(89.0, decDeg)) * Math.PI / 180.0);
        if (cosDec < 0.01) cosDec = 0.01;
        double raPad = Math.Min(180.0, radiusDeg / cosDec);
        double decPad = radiusDeg;

        double minDec = Math.Max(-90.0, decDeg - decPad);
        double maxDec = Math.Min( 90.0, decDeg + decPad);
        double minRa = raDeg - raPad;
        double maxRa = raDeg + raPad;
        // RA wrap-around at 0/360. If the box crosses the seam we
        // run two queries and merge.
        bool wrap = minRa < 0 || maxRa >= 360;

        var results = new List<CatalogStar>();
        using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        conn.Open();

        if (!wrap) {
            QueryRange(conn, minRa, maxRa, minDec, maxDec, raDeg, decDeg,
                radiusDeg, magLimit, results);
        } else {
            // Normalise into two non-wrapping ranges and union.
            double leftLo = (minRa + 360.0) % 360.0;
            double leftHi = 360.0;
            double rightLo = 0.0;
            double rightHi = (maxRa + 360.0) % 360.0;
            QueryRange(conn, leftLo, leftHi, minDec, maxDec, raDeg, decDeg,
                radiusDeg, magLimit, results);
            QueryRange(conn, rightLo, rightHi, minDec, maxDec, raDeg, decDeg,
                radiusDeg, magLimit, results);
        }
        return results;
    }

    private void QueryRange(SqliteConnection conn,
            double minRa, double maxRa, double minDec, double maxDec,
            double centerRa, double centerDec, double radiusDeg,
            double? magLimit, List<CatalogStar> results) {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.ra, s.dec, s.mag_v, s.mag_b, s.b_v, s.source
            FROM stars s
            JOIN stars_idx idx ON s.id = idx.id
            WHERE idx.min_ra <= $maxRa AND idx.max_ra >= $minRa
              AND idx.min_dec <= $maxDec AND idx.max_dec >= $minDec
              AND ($magLimit IS NULL OR s.mag_v <= $magLimit)";
        cmd.Parameters.AddWithValue("$minRa", minRa);
        cmd.Parameters.AddWithValue("$maxRa", maxRa);
        cmd.Parameters.AddWithValue("$minDec", minDec);
        cmd.Parameters.AddWithValue("$maxDec", maxDec);
        cmd.Parameters.AddWithValue("$magLimit", (object?)magLimit ?? DBNull.Value);

        double radRadius = radiusDeg * Math.PI / 180.0;
        double cosRadius = Math.Cos(radRadius);
        double sinCDec = Math.Sin(centerDec * Math.PI / 180.0);
        double cosCDec = Math.Cos(centerDec * Math.PI / 180.0);

        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) {
            double ra  = rdr.GetDouble(0);
            double dec = rdr.GetDouble(1);
            // Exact angular-distance check via cosine of the
            // great-circle angle. cos(theta) >= cos(R) ⇔ theta <= R.
            double dra = (ra - centerRa) * Math.PI / 180.0;
            double sinDec = Math.Sin(dec * Math.PI / 180.0);
            double cosDec = Math.Cos(dec * Math.PI / 180.0);
            double cosTheta = sinCDec * sinDec + cosCDec * cosDec * Math.Cos(dra);
            if (cosTheta < cosRadius) continue;

            double? magV = rdr.IsDBNull(2) ? null : rdr.GetDouble(2);
            double? magB = rdr.IsDBNull(3) ? null : rdr.GetDouble(3);
            double? bv   = rdr.IsDBNull(4) ? null : rdr.GetDouble(4);
            string source = rdr.IsDBNull(5) ? "APASS" : rdr.GetString(5);
            results.Add(new CatalogStar(ra, dec, magV, magB, bv, source));
        }
    }
}
