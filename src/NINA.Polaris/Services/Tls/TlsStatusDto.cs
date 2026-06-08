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

namespace NINA.Polaris.Services.Tls;

/// <summary>
/// Snapshot returned by GET /api/tls/status. Combines both the always-on
/// self-signed cert state and the optional Let's Encrypt config so the
/// Settings UI can render one panel without two round-trips.
/// </summary>
public record TlsStatusDto(
    SelfSignedCertInfo SelfSigned,
    LetsEncryptInfo LetsEncrypt
);

/// <summary>
/// Read-only view of the bundled self-signed cert. Fingerprints help
/// the user verify (out-of-band) that the cert their browser is seeing
/// is the one Polaris generated.
/// </summary>
public record SelfSignedCertInfo(
    string Thumbprint,            // SHA-1, colon-separated, legacy format
    string FingerprintSha256,     // 64 lowercase hex chars, modern browser UI format
    DateTime NotAfterUtc,
    IReadOnlyList<string> SubjectAlternativeNames
);

/// <summary>
/// Read-only view of the Let's Encrypt config + last-known issuance
/// outcome. Token is intentionally elided so a GET from any client
/// can't exfiltrate it; PUT works against the persisted value or
/// accepts a new one to replace it.
/// </summary>
public record LetsEncryptInfo(
    bool Enabled,
    string Domain,
    bool HasToken,                // true when DuckDnsToken non-empty (we don't echo the value)
    string Email,
    bool UseStaging,
    DateTime? LastRenewalUtc,
    DateTime? NotAfterUtc,
    string? Status,
    string? LastError,
    bool CertOnDisk                // true when LE-issued PFX exists and is loadable
);

/// <summary>
/// PUT /api/tls/letsencrypt/config request body. Token is optional on
/// updates: omit to keep the persisted value (so the UI can show
/// "Token: configured ✓" without re-prompting on every save), supply
/// a non-empty string to replace, supply empty string to clear.
/// </summary>
public record LetsEncryptConfigRequest(
    bool Enabled,
    string Domain,
    string? DuckDnsToken,         // null = keep existing, empty = clear, non-empty = replace
    string Email,
    bool UseStaging
);