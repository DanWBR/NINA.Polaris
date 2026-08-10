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
using System.Text.Json;

namespace NINA.Polaris.Services;

/// <summary>
/// Keeps the comet orbital elements current from the JPL Small-Body Database.
///
/// WHY. The bundled comets.json is a snapshot of ten periodic comets taken at
/// build time. Its real weakness is not drift but ABSENCE: a comet discovered
/// after the release can never appear, and a newly discovered bright comet is
/// exactly the one an operator wants to point at. A refresh is what turns the
/// SKY comet list from a museum piece into something worth opening.
///
/// The host is frequently offline in the field, so every path here treats "no
/// internet" as normal and silent: the bundled snapshot keeps working, the UI
/// reports the age of what it has, and nothing blocks startup.
/// </summary>
public class CometElementsUpdater {
    // Perihelion window: a comet whose perihelion is far outside this is either
    // long gone or years away, and two-body propagation is least trustworthy
    // far from the elements' epoch anyway. Keeps the payload small on a link
    // that is often a phone hotspot.
    private const int PerihelionWindowDays = 550;

    // Sanity ceiling only. Elliptic, parabolic and hyperbolic orbits are all
    // propagated (see CometEphemerisService.SolveOrbit), which matters because
    // 67 of the 118 comets in a live ±550-day window have e >= 0.98 and they
    // include the bright long-period ones. This bound just rejects nonsense
    // that would still be nonsense after propagation.
    private const double MaxEccentricity = 5.0;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };

    private readonly CometEphemerisService _comets;
    private readonly ILogger<CometElementsUpdater> _logger;

    public CometElementsUpdater(CometEphemerisService comets, ILogger<CometElementsUpdater> logger) {
        _comets = comets;
        _logger = logger;
        if (!Http.DefaultRequestHeaders.UserAgent.Any())
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("NINA.Polaris/1.0 (+https://github.com/DanWBR/NINA.Polaris)");
    }

    /// <summary>Download, parse, filter and install. Returns how many elements
    /// were installed, or throws with a message fit to show the operator.</summary>
    public async Task<int> RefreshAsync(CancellationToken ct = default) {
        var now = DateTime.UtcNow;
        var url = BuildQueryUrl(now);
        _logger.LogInformation("Fetching comet elements from {Url}", url);

        using var resp = await Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"JPL SBDB returned {(int)resp.StatusCode} {resp.ReasonPhrase}");

        var body = await resp.Content.ReadAsStringAsync(ct);
        var parsed = Parse(body, now, out var skipped);
        if (parsed.Count == 0)
            throw new InvalidOperationException(
                "JPL SBDB returned no usable comets; keeping the current elements");

        _comets.ReplaceElements(parsed, "jpl", now);
        _logger.LogInformation("Comet elements refreshed: {Kept} kept, {Skipped} skipped", parsed.Count, skipped);
        return parsed.Count;
    }

    /// <summary>Build the SBDB query. The perihelion window is applied on JPL's
    /// side, so the host downloads ~12 KB instead of the full ~1200-comet table
    /// — this often runs over a phone hotspot. Verified against the live API:
    /// a +/-550-day window returns 118 rows.</summary>
    internal static string BuildQueryUrl(DateTime nowUtc) {
        // tp is a Julian Date in the SBDB, so the window is expressed in JD.
        var jdNow = ToJulianDate(nowUtc);
        var lo = (jdNow - PerihelionWindowDays).ToString("F4", CultureInfo.InvariantCulture);
        var hi = (jdNow + PerihelionWindowDays).ToString("F4", CultureInfo.InvariantCulture);
        var cdata = "{\"AND\":[\"tp|GE|" + lo + "\",\"tp|LE|" + hi + "\"]}";
        return "https://ssd-api.jpl.nasa.gov/sbdb_query.api"
             + "?fields=full_name,e,q,i,om,w,tp,M1,K1,H"
             + "&sb-kind=c"
             + "&sb-cdata=" + Uri.EscapeDataString(cdata);
    }

    /// <summary>Parse an SBDB query response into elements the ephemeris can
    /// propagate. Rows that are unusable are counted, not fatal: one malformed
    /// comet must not cost the operator the other three hundred.</summary>
    internal static List<CometElements> Parse(string json, DateTime nowUtc, out int skipped) {
        skipped = 0;
        var result = new List<CometElements>();
        using var doc = JsonDocument.Parse(json);
        // Anything that is not an SBDB object yields nothing rather than
        // throwing. TryGetProperty raises InvalidOperationException on a string
        // or array root, and the relay path feeds this whatever a phone
        // received: a captive-portal page, or the table double-encoded as a
        // JSON string. Returning empty lets the caller answer "no usable
        // comets" and keep the elements it already had.
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
        if (!doc.RootElement.TryGetProperty("fields", out var fieldsEl)
            || !doc.RootElement.TryGetProperty("data", out var dataEl)) return result;
        if (fieldsEl.ValueKind != JsonValueKind.Array || dataEl.ValueKind != JsonValueKind.Array)
            return result;

        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fields = fieldsEl.EnumerateArray().ToList();
        for (var i = 0; i < fields.Count; i++) index[fields[i].GetString() ?? ""] = i;

        int Col(string name) => index.TryGetValue(name, out var i) ? i : -1;
        int iName = Col("full_name"), iE = Col("e"), iQ = Col("q"), iI = Col("i"),
            iOm = Col("om"), iW = Col("w"), iTp = Col("tp"), iM1 = Col("M1"),
            iK1 = Col("K1"), iH = Col("H");
        if (iName < 0 || iE < 0 || iQ < 0 || iI < 0 || iOm < 0 || iW < 0 || iTp < 0) return result;

        foreach (var row in dataEl.EnumerateArray()) {
            try {
                string? S(int i) => i >= 0 && i < row.GetArrayLength()
                    ? (row[i].ValueKind == JsonValueKind.String ? row[i].GetString() : row[i].ToString())
                    : null;
                double? D(int i) => double.TryParse(S(i), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                    ? v : null;

                var name = S(iName)?.Trim();
                var e = D(iE); var q = D(iQ); var inc = D(iI);
                var om = D(iOm); var w = D(iW); var tp = D(iTp);
                if (string.IsNullOrWhiteSpace(name) || e is null || q is null || inc is null
                    || om is null || w is null || tp is null) { skipped++; continue; }

                if (e.Value >= MaxEccentricity || e.Value < 0 || q.Value <= 0) { skipped++; continue; }

                // M1/K1 are the cometary total-magnitude parameters; the
                // ephemeris calls them H and n. Fall back to the asteroid-style
                // H and the conventional n = 4 when a comet has no photometry.
                var h = D(iM1) ?? D(iH);
                var n = D(iK1);
                if (h is null) { skipped++; continue; }

                result.Add(new CometElements {
                    Name = name!,
                    Tperi = FromJulianDate(tp.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Q = q.Value,
                    E = e.Value,
                    I = inc.Value,
                    OmegaNode = om.Value,
                    ArgPeriapsis = w.Value,
                    H = h.Value,
                    N = n is > 0 and < 30 ? n.Value : 4.0
                });
            } catch {
                skipped++;
            }
        }
        return result;
    }

    private static double ToJulianDate(DateTime utc)
        => utc.ToOADate() + 2415018.5;

    private static DateTime FromJulianDate(double jd)
        => DateTime.SpecifyKind(DateTime.FromOADate(jd - 2415018.5), DateTimeKind.Utc);
}

/// <summary>
/// Refreshes the comet elements in the background: once shortly after start,
/// then daily, and only when the current set is older than <see cref="MaxAge"/>.
///
/// Every failure here is expected rather than exceptional. The host spends most
/// of its life on a field network with no route to the internet, so a failed
/// refresh is logged at debug and forgotten: the bundled or last-downloaded
/// elements keep serving and the UI shows their age. Nothing in this class is
/// allowed to delay startup or surface an error to the operator, who did not
/// ask for it.
/// </summary>
public class CometElementsRefreshWorker : BackgroundService {
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Period = TimeSpan.FromHours(24);

    private readonly CometElementsUpdater _updater;
    private readonly CometEphemerisService _comets;
    private readonly ILogger<CometElementsRefreshWorker> _logger;

    public CometElementsRefreshWorker(CometElementsUpdater updater, CometEphemerisService comets,
                                      ILogger<CometElementsRefreshWorker> logger) {
        _updater = updater;
        _comets = comets;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        // Let the app finish coming up first; equipment connect matters more
        // than comets, and on an SBC the two competing is noticeable.
        try { await Task.Delay(StartupDelay, stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested) {
            var age = _comets.FetchedAtUtc is { } t ? DateTime.UtcNow - t : TimeSpan.MaxValue;
            if (age >= MaxAge) {
                try {
                    await _updater.RefreshAsync(stoppingToken);
                } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                    return;
                } catch (Exception ex) {
                    _logger.LogDebug(ex, "Comet element refresh did not complete (offline?); "
                        + "keeping the current set");
                }
            }
            try { await Task.Delay(Period, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
