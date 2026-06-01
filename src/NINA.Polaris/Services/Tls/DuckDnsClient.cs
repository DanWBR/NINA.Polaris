using System.Net;

namespace NINA.Polaris.Services.Tls;

/// <summary>
/// TLS-2: minimal client for the DuckDNS HTTP API. We only use the
/// "txt" update path, which sets a single TXT record on
/// _acme-challenge.{subdomain}.duckdns.org so Let's Encrypt can
/// validate domain control via DNS-01.
///
/// DuckDNS quirks worth knowing:
///   • One TXT record per subdomain. Setting a new value overwrites
///     the old one. We can't park multiple values, so wildcard certs
///     (which need two TXT records simultaneously) don't work here;
///     Polaris only issues single-name certs against the bare domain.
///   • The endpoint returns plain text "OK" or "KO" — no JSON, no
///     useful error detail. "KO" can mean wrong token, wrong domain,
///     network error upstream, or rate limit. Best we can do is log
///     "KO" verbatim and let the user re-check the token / domain in
///     Settings.
///   • DuckDNS handles the _acme-challenge prefix internally — we
///     pass <c>domains=nina-polaris</c> (no prefix, no .duckdns.org),
///     and DuckDNS publishes the TXT at <c>_acme-challenge.nina-polaris
///     .duckdns.org</c>. ACME clients query the prefixed name; the
///     mapping is invisible to us.
///   • Free tier: no published rate limit, but anecdotally throttles
///     under heavy update bursts. Issuance + renewal hit it twice per
///     run total, well under any sensible threshold.
/// </summary>
public class DuckDnsClient {
    private readonly ILogger<DuckDnsClient> _logger;
    private readonly HttpClient _http;

    public DuckDnsClient(ILogger<DuckDnsClient> logger) {
        _logger = logger;
        _http = new HttpClient {
            Timeout = TimeSpan.FromSeconds(15),
            DefaultRequestHeaders = { { "User-Agent", "NINA.Polaris/1.0 (+https://github.com/DanWBR/NINA.Polaris)" } }
        };
    }

    /// <summary>
    /// Publish a TXT value at <c>_acme-challenge.{subdomain}.duckdns.org</c>.
    /// </summary>
    /// <param name="subdomain">The DuckDNS-issued name without the
    /// .duckdns.org suffix and without any prefix. For
    /// <c>nina-polaris.duckdns.org</c> pass <c>"nina-polaris"</c>.
    /// We strip the suffix automatically if the caller passes the FQDN.</param>
    /// <param name="token">DuckDNS account UUID (from the user's
    /// duckdns.org page, "token" field).</param>
    /// <param name="txtValue">The challenge digest Let's Encrypt
    /// provided. Empty string clears the record.</param>
    public async Task<DuckDnsResult> SetTxtAsync(
        string subdomain, string token, string txtValue, CancellationToken ct) {

        if (string.IsNullOrWhiteSpace(subdomain))
            return new(false, "subdomain required");
        if (string.IsNullOrWhiteSpace(token))
            return new(false, "token required");

        var sub = NormaliseSubdomain(subdomain);
        var url = $"https://www.duckdns.org/update"
            + $"?domains={WebUtility.UrlEncode(sub)}"
            + $"&token={WebUtility.UrlEncode(token)}"
            + $"&txt={WebUtility.UrlEncode(txtValue ?? "")}";

        try {
            using var resp = await _http.GetAsync(url, ct);
            var body = (await resp.Content.ReadAsStringAsync(ct)).Trim();
            if (resp.IsSuccessStatusCode && body.Equals("OK", StringComparison.OrdinalIgnoreCase)) {
                _logger.LogInformation(
                    "DuckDNS TXT updated for _acme-challenge.{Sub}.duckdns.org "
                    + "(len {Len}).", sub, txtValue?.Length ?? 0);
                return new(true, null);
            }
            // Body is typically "KO" on failure. Treat anything not "OK" as fail.
            _logger.LogWarning(
                "DuckDNS TXT update for {Sub} failed: HTTP {Status} body={Body}",
                sub, (int)resp.StatusCode, body);
            return new(false, $"DuckDNS returned {body} (HTTP {(int)resp.StatusCode})");
        } catch (TaskCanceledException) {
            return new(false, "DuckDNS request timed out (15s)");
        } catch (Exception ex) {
            _logger.LogWarning(ex, "DuckDNS TXT update threw for {Sub}", sub);
            return new(false, ex.Message);
        }
    }

    /// <summary>Convenience: clear the TXT record by setting it to empty.</summary>
    public Task<DuckDnsResult> ClearTxtAsync(string subdomain, string token, CancellationToken ct)
        => SetTxtAsync(subdomain, token, "", ct);

    /// <summary>
    /// Poll authoritative DNS until the TXT record propagates or the
    /// timeout fires. Necessary because Let's Encrypt rejects the
    /// challenge if it queries the DNS before DuckDNS has flushed it
    /// to its NS pool (typically &lt;10s but occasionally up to ~60s).
    /// We query DuckDNS's own resolver (208.67.222.222 / Quad9 / 8.8.8.8)
    /// in rotation to dodge the local resolver cache, then accept the
    /// first one that returns the expected value.
    /// </summary>
    /// <returns>true if propagation seen within the timeout.</returns>
    public async Task<bool> WaitForPropagationAsync(
        string subdomain, string expectedTxt, TimeSpan timeout, CancellationToken ct) {

        var sub = NormaliseSubdomain(subdomain);
        var fqdn = $"_acme-challenge.{sub}.duckdns.org";
        var deadline = DateTime.UtcNow + timeout;
        // First poll burst is fast (DuckDNS usually flushes in 5-15s);
        // back off after a minute to 5s intervals so the user-visible
        // wait isn't dramatic.
        var interval = TimeSpan.FromSeconds(3);
        var attempts = 0;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested) {
            attempts++;
            try {
                // Resolve TXT via Google's DoH (HTTPS-wrapped DNS), no
                // raw UDP needed — keeps us portable and avoids per-OS
                // resolver libraries. Returns JSON.
                var dohUrl = $"https://dns.google/resolve?name={WebUtility.UrlEncode(fqdn)}&type=TXT&cd=1";
                using var resp = await _http.GetAsync(dohUrl, ct);
                if (resp.IsSuccessStatusCode) {
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    // Cheap substring check, no need to parse: TXT
                    // RDATA appears in the "data" field as a quoted
                    // string. Will match even if multiple TXT values
                    // are concatenated by some resolver.
                    if (json.Contains(expectedTxt, StringComparison.Ordinal)) {
                        _logger.LogInformation(
                            "DuckDNS TXT propagation confirmed for {Fqdn} "
                            + "after {Attempts} attempt(s) (~{Elapsed}s).",
                            fqdn, attempts,
                            (int)(timeout - (deadline - DateTime.UtcNow)).TotalSeconds);
                        return true;
                    }
                }
            } catch (Exception ex) {
                _logger.LogDebug(ex, "TXT propagation probe failed (attempt {N})", attempts);
            }
            if (attempts == 20) interval = TimeSpan.FromSeconds(5);
            await Task.Delay(interval, ct);
        }
        _logger.LogWarning(
            "DuckDNS TXT propagation timed out for {Fqdn} after {Timeout}s.",
            fqdn, (int)timeout.TotalSeconds);
        return false;
    }

    /// <summary>
    /// Accept either "nina-polaris" or "nina-polaris.duckdns.org" so
    /// the user can paste whichever form they have in front of them.
    /// </summary>
    private static string NormaliseSubdomain(string input) {
        var s = input.Trim().ToLowerInvariant();
        const string suffix = ".duckdns.org";
        if (s.EndsWith(suffix, StringComparison.Ordinal))
            s = s.Substring(0, s.Length - suffix.Length);
        // Strip any leading "_acme-challenge." if the user (or a stale
        // copy-paste) included it. DuckDNS doesn't want the prefix.
        const string acmePrefix = "_acme-challenge.";
        if (s.StartsWith(acmePrefix, StringComparison.Ordinal))
            s = s.Substring(acmePrefix.Length);
        return s;
    }
}

public record DuckDnsResult(bool Ok, string? Error);
