using System.Security.Cryptography.X509Certificates;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Tls;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// TLS-1: HTTP surface for the certificate config (both the always-on
/// self-signed cert and the optional Let's Encrypt setup via DuckDNS
/// DNS-01).
///
/// Issue / renew / test-DNS endpoints land in TLS-5 once the
/// LetsEncryptService is wired. This phase only carries the read +
/// config-write surface so the UI panel can render real state.
/// </summary>
public static class TlsEndpoints {
    public static void MapTlsEndpoints(this IEndpointRouteBuilder app) {
        var g = app.MapGroup("/api/tls");

        // GET /api/tls/status
        // Single read for the Settings panel: bundled self-signed
        // cert + LE config + last issuance outcome. Never echoes the
        // DuckDNS token (only `hasToken: bool`).
        g.MapGet("/status", (
            SelfSignedCertService selfSigned,
            ProfileService profiles
        ) => {
            var cert = selfSigned.GetOrCreate();
            var ss = new SelfSignedCertInfo(
                Thumbprint: selfSigned.Fingerprint,
                FingerprintSha256: selfSigned.Fingerprint256,
                NotAfterUtc: cert.NotAfter.ToUniversalTime(),
                SubjectAlternativeNames: selfSigned.SanEntries()
            );

            var p = profiles.Active;
            // Cert-on-disk probe lives in LetsEncryptService later;
            // for now we report based on the persisted NotAfter field.
            // A renewed cert older than now → CertOnDisk=false so the
            // UI nudges the user to renew.
            var leCertOnDisk = p.LetsEncryptNotAfterUtc.HasValue
                && p.LetsEncryptNotAfterUtc.Value > DateTime.UtcNow;

            var le = new LetsEncryptInfo(
                Enabled: p.LetsEncryptEnabled,
                Domain: p.LetsEncryptDomain ?? "",
                HasToken: !string.IsNullOrEmpty(p.DuckDnsToken),
                Email: p.LetsEncryptEmail ?? "",
                UseStaging: p.LetsEncryptUseStaging,
                LastRenewalUtc: p.LetsEncryptLastRenewalUtc,
                NotAfterUtc: p.LetsEncryptNotAfterUtc,
                Status: p.LetsEncryptStatus,
                LastError: p.LetsEncryptLastError,
                CertOnDisk: leCertOnDisk
            );

            return Results.Ok(new TlsStatusDto(ss, le));
        });

        // PUT /api/tls/letsencrypt/config
        // Persists the LE/DuckDNS settings. Does NOT trigger issuance;
        // that's a separate POST /issue in TLS-5 so the user reviews
        // the config first and explicitly clicks the button. Token
        // semantics: null → keep current, "" → clear, "x" → replace.
        // Empty domain disables enable=true (silently); the UI also
        // gates the toggle, this is defence in depth.
        g.MapPut("/letsencrypt/config", (
            LetsEncryptConfigRequest req,
            ProfileService profiles
        ) => {
            if (req == null) return Results.BadRequest(new { error = "missing body" });

            // Basic validation
            if (req.Enabled && string.IsNullOrWhiteSpace(req.Domain))
                return Results.BadRequest(new { error = "domain required when enabled" });
            if (req.Enabled && string.IsNullOrWhiteSpace(req.Email))
                return Results.BadRequest(new { error = "email required when enabled" });

            var p = profiles.Active;
            p.LetsEncryptEnabled = req.Enabled;
            p.LetsEncryptDomain = (req.Domain ?? "").Trim();
            p.LetsEncryptEmail = (req.Email ?? "").Trim();
            p.LetsEncryptUseStaging = req.UseStaging;
            if (req.DuckDnsToken != null) {
                // Caller explicitly sent the field (even if empty).
                // null in JSON means "leave alone".
                p.DuckDnsToken = req.DuckDnsToken.Trim();
            }
            profiles.Save();
            return Results.Ok(new { ok = true });
        });

        // ─── TLS-A1: Self-signed root cert download + install help ────

        // GET /api/tls/ca.crt
        // Returns the bundled self-signed cert in PEM format (the form
        // every OS and browser cert-import dialog accepts). DER is
        // also offered via ?format=der for older Windows tooling.
        // Exempt from auth — the cert is by design public (it's what
        // the user is about to install as a trust anchor). The
        // fingerprint stays in /status so the user can verify against
        // an out-of-band channel before trusting it.
        g.MapGet("/ca.crt", (HttpContext ctx, SelfSignedCertService selfSigned) => {
            var format = (ctx.Request.Query["format"].ToString() ?? "pem").ToLowerInvariant();
            var cert = selfSigned.GetOrCreate();
            if (format == "der") {
                var der = cert.Export(X509ContentType.Cert);
                return Results.File(der, "application/pkix-cert", "polaris-root.cer");
            }
            // Default PEM. Modern browsers + OS dialogs accept the
            // "-----BEGIN CERTIFICATE-----" wrapper without DER
            // round-tripping. Use the BCL helper so line endings and
            // base64 width match RFC 7468 exactly.
            var pem = cert.ExportCertificatePem();
            var bytes = System.Text.Encoding.ASCII.GetBytes(pem);
            return Results.File(bytes, "application/x-pem-file", "polaris-root.crt");
        });

        // GET /api/tls/install-instructions?os=ios
        // Returns step-by-step install steps for a given OS (or all
        // of them when ?os is omitted). The UI renders these as the
        // body of the install wizard so translators and OS-specific
        // updates land in one place (CertInstallInstructions.cs)
        // rather than spread across the frontend.
        g.MapGet("/install-instructions", (HttpContext ctx) => {
            var os = ctx.Request.Query["os"].ToString();
            if (string.IsNullOrWhiteSpace(os)) {
                return Results.Ok(new { guides = CertInstallInstructions.All });
            }
            var guide = CertInstallInstructions.ForOs(os);
            // Unknown OS: return the full list so the UI can let the
            // user pick instead of showing an error. Common case is
            // ?os=fuchsia / random user-agent we don't classify.
            if (guide == null) {
                return Results.Ok(new { guides = CertInstallInstructions.All });
            }
            return Results.Ok(new { guides = new[] { guide } });
        });
    }
}
