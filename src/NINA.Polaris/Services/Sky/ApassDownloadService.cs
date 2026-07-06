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
using Microsoft.Data.Sqlite;

namespace NINA.Polaris.Services.Sky;

/// <summary>
/// In-app downloader for the APASS DR9 star catalog the Photometric Color
/// Calibration (PCC/SPCC) workflow consumes, so users never have to run a
/// script on the box. This is a C# port of <c>scripts/download-apass.py</c>:
/// it streams the catalog from CDS/VizieR's TAP service one Dec stripe at a
/// time and builds the same SQLite + R*tree schema <see cref="ApassCatalog"/>
/// reads, into the always-writable data dir. Single-flight, cancellable,
/// progress-reporting; writes to a temp file and atomically moves it into
/// place so a partial download never shadows a good catalog.
/// </summary>
public class ApassDownloadService {
    // CDS VizieR TAP endpoint serving APASS DR9 (II/336/apass9). Stable, free,
    // no API key. Each ADQL query streams back as TSV.
    private const string TapUrl = "https://tapvizier.cds.unistra.fr/TAPVizieR/tap/sync";
    private const int MaxRec = 2_000_000;
    private const double MagLimit = 13.0;   // ~5.3M stars, ~80 MB (matches the script default)
    private const double StripeDeg = 5.0;

    private readonly IHttpClientFactory _http;
    private readonly ApassCatalog _catalog;
    private readonly ILogger<ApassDownloadService> _logger;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    public ApassDownloadService(IHttpClientFactory http, ApassCatalog catalog,
            ILogger<ApassDownloadService> logger) {
        _http = http;
        _catalog = catalog;
        _logger = logger;
    }

    public enum DownloadState { Idle, Running, Done, Error }

    public record StatusDto(string State, double Progress, string Message,
        long Stars, bool Installed);

    private volatile DownloadState _state = DownloadState.Idle;
    private volatile string _message = "";
    private double _progress;
    private long _stars;

    public StatusDto Status() => new(
        _state.ToString().ToLowerInvariant(),
        Math.Round(_progress, 3),
        _message,
        _state == DownloadState.Done ? _stars : _catalog.StarCount,
        _catalog.IsAvailable);

    /// <summary>Kick off a download if one isn't already running. Returns false
    /// if a download is already in progress.</summary>
    public bool Start() {
        lock (_lock) {
            if (_state == DownloadState.Running) return false;
            _state = DownloadState.Running;
            _progress = 0;
            _stars = 0;
            _message = "Starting…";
            _cts = new CancellationTokenSource();
        }
        var ct = _cts!.Token;
        _ = Task.Run(() => RunAsync(ct), ct);
        return true;
    }

    /// <summary>Cancel an in-progress download.</summary>
    public void Cancel() {
        lock (_lock) { _cts?.Cancel(); }
    }

    private async Task RunAsync(CancellationToken ct) {
        var target = _catalog.WritableDbPath;
        var tmp = target + ".tmp";
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(tmp)) File.Delete(tmp);

            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(6);

            using (var conn = new SqliteConnection($"Data Source={tmp}")) {
                conn.Open();
                using (var init = conn.CreateCommand()) {
                    init.CommandText = @"
                        CREATE TABLE stars (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            ra REAL NOT NULL, dec REAL NOT NULL,
                            mag_v REAL, mag_b REAL, b_v REAL, source TEXT NOT NULL);
                        CREATE VIRTUAL TABLE stars_idx USING rtree(
                            id, min_ra, max_ra, min_dec, max_dec);";
                    init.ExecuteNonQuery();
                }

                long total = 0;
                long nextId = 1;
                for (double decLo = -90.0; decLo < 90.0; decLo += StripeDeg) {
                    ct.ThrowIfCancellationRequested();
                    double decHi = Math.Min(90.0, decLo + StripeDeg);
                    _progress = (decLo + 90.0) / 180.0;
                    _message = $"Fetching declination {decLo:+0.#}° to {decHi:+0.#}°… {total:N0} stars so far";

                    var tsv = await FetchStripeAsync(client, decLo, decHi, ct);
                    int inserted = IngestStripe(conn, tsv, ref nextId);
                    total += inserted;
                }

                ct.ThrowIfCancellationRequested();
                _message = "Optimizing index…";
                using (var analyze = conn.CreateCommand()) {
                    analyze.CommandText = "ANALYZE";
                    analyze.ExecuteNonQuery();
                }
                _stars = total;
            }
            // Release SQLite file handles/pool before the move.
            SqliteConnection.ClearAllPools();

            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
            _catalog.InvalidateCache();

            _progress = 1.0;
            _message = $"Installed {_stars:N0} stars.";
            _state = DownloadState.Done;
            _logger.LogInformation("APASS catalog downloaded: {Stars:N0} stars -> {Path}", _stars, target);
        } catch (OperationCanceledException) {
            _state = DownloadState.Idle;
            _message = "Cancelled.";
            TryDelete(tmp);
        } catch (Exception ex) {
            _state = DownloadState.Error;
            _message = "Download failed: " + ex.Message;
            _logger.LogWarning(ex, "APASS catalog download failed");
            TryDelete(tmp);
        }
    }

    private static void TryDelete(string path) {
        try { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static async Task<string> FetchStripeAsync(HttpClient client,
            double decLo, double decHi, CancellationToken ct) {
        // ADQL: pick the four columns we index. B-V has a hyphen, so it must
        // be double-quoted inside the ADQL. Empty stripes come back as a
        // header-only TSV in under a second.
        string adql =
            "SELECT RAJ2000, DEJ2000, Vmag, Bmag, \"B-V\" " +
            "FROM \"II/336/apass9\" " +
            $"WHERE Vmag <= {MagLimit.ToString("0.00", CultureInfo.InvariantCulture)} " +
            $"  AND DEJ2000 >= {decLo.ToString("0.0000", CultureInfo.InvariantCulture)} " +
            $"  AND DEJ2000 < {decHi.ToString("0.0000", CultureInfo.InvariantCulture)} " +
            "  AND RAJ2000 IS NOT NULL AND DEJ2000 IS NOT NULL";
        var form = new Dictionary<string, string> {
            ["REQUEST"] = "doQuery",
            ["LANG"] = "ADQL",
            ["FORMAT"] = "tsv",
            ["MAXREC"] = MaxRec.ToString(CultureInfo.InvariantCulture),
            ["QUERY"] = adql,
        };
        using var content = new FormUrlEncodedContent(form);
        using var resp = await client.PostAsync(TapUrl, content, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static int IngestStripe(SqliteConnection conn, string tsv, ref long nextId) {
        if (string.IsNullOrEmpty(tsv)) return 0;
        var lines = tsv.Split('\n');
        int inserted = 0;
        using var tx = conn.BeginTransaction();
        using var star = conn.CreateCommand();
        star.CommandText = "INSERT INTO stars(id, ra, dec, mag_v, mag_b, b_v, source) " +
                           "VALUES ($id, $ra, $dec, $v, $b, $bv, 'APASS')";
        var pId = star.CreateParameter(); pId.ParameterName = "$id"; star.Parameters.Add(pId);
        var pRa = star.CreateParameter(); pRa.ParameterName = "$ra"; star.Parameters.Add(pRa);
        var pDec = star.CreateParameter(); pDec.ParameterName = "$dec"; star.Parameters.Add(pDec);
        var pV = star.CreateParameter(); pV.ParameterName = "$v"; star.Parameters.Add(pV);
        var pB = star.CreateParameter(); pB.ParameterName = "$b"; star.Parameters.Add(pB);
        var pBv = star.CreateParameter(); pBv.ParameterName = "$bv"; star.Parameters.Add(pBv);

        using var idx = conn.CreateCommand();
        idx.CommandText = "INSERT INTO stars_idx(id, min_ra, max_ra, min_dec, max_dec) " +
                          "VALUES ($id, $ra, $ra, $dec, $dec)";
        var iId = idx.CreateParameter(); iId.ParameterName = "$id"; idx.Parameters.Add(iId);
        var iRa = idx.CreateParameter(); iRa.ParameterName = "$ra"; idx.Parameters.Add(iRa);
        var iDec = idx.CreateParameter(); iDec.ParameterName = "$dec"; idx.Parameters.Add(iDec);

        foreach (var raw in lines) {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var cols = line.Split('\t');
            if (cols.Length < 5) continue;
            // Header and any units/dashes line fail the numeric parse below and
            // are skipped — no need to special-case them.
            if (!double.TryParse(cols[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double ra)) continue;
            if (!double.TryParse(cols[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double dec)) continue;
            double? magV = ParseNullable(cols[2]);
            if (magV is null) continue;   // V mag is required by the calibration
            double? magB = ParseNullable(cols[3]);
            double? bv = ParseNullable(cols[4]);

            long id = nextId++;
            pId.Value = id; pRa.Value = ra; pDec.Value = dec;
            pV.Value = magV.Value; pB.Value = (object?)magB ?? DBNull.Value; pBv.Value = (object?)bv ?? DBNull.Value;
            star.ExecuteNonQuery();
            iId.Value = id; iRa.Value = ra; iDec.Value = dec;
            idx.ExecuteNonQuery();
            inserted++;
        }
        tx.Commit();
        return inserted;
    }

    private static double? ParseNullable(string s) {
        s = s.Trim();
        if (s.Length == 0) return null;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
    }
}
