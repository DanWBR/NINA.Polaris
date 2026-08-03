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

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NINA.Polaris.Services;

/// <summary>
/// Generates + persists a self-signed TLS certificate so Polaris can
/// serve HTTPS out of the box on a LAN, no cert authority needed.
///
/// Motivation (GX-10): Chrome and other modern browsers gate WebGPU,
/// SharedArrayBuffer, and a handful of other powerful APIs behind a
/// "secure context" (HTTPS or localhost). When the client device is
/// reaching Polaris over the LAN (typical observatory setup: Pi/mini-PC
/// runs Polaris, a laptop/tablet runs the heavy in-browser inference),
/// plain HTTP via mDNS (`polaris-app.local`) gives up WebGPU access.
/// Self-signed HTTPS bridges that gap: the user clicks through the
/// browser's "cert not trusted" warning once per device, after which
/// WebGPU + SharedArrayBuffer light up.
///
/// What we cover with SAN entries (so Chrome accepts the cert for the
/// hostname / IP the user actually types into the URL bar):
///   • DNS: localhost, the machine's hostname, hostname.local,
///          polaris.local, polaris-app.local (mDNS aliases set by
///          MdnsService)
///   • IP:  every non-loopback / non-link-local IPv4 from every
///          active NIC + ::1 + 127.0.0.1
///
/// Persisted as PFX (empty password, file-system-permission-protected)
/// at <c>{LocalApplicationData}/NINA.Polaris/cert/polaris.pfx</c> so
/// the same cert survives restarts (otherwise the user would re-trust
/// it every reboot, terrible UX).
///
/// Auto-regenerates when:
///   • file is missing
///   • cert expires within 30 days
///   • the SAN entry set no longer matches the current host (the user
///     moved the box to a new network with new IPs), keyed by a hash
///     of the SAN list stored next to the PFX
/// </summary>
public class SelfSignedCertService {
    private readonly ILogger<SelfSignedCertService> _logger;
    private readonly IConfiguration _config;
    private readonly string _certDir;
    private readonly string _certPath;
    private readonly string _sanListPath;
    private X509Certificate2? _cached;

    // Kestrel needs the cert before builder.Build() runs, so this service is
    // constructed by hand with a NullLogger and every decision it makes about
    // the user's certificate went nowhere: not to journald, not to the DEBUG
    // panel. That is the one subsystem that can silently invalidate every
    // client's stored trust, and it was the only one that logged nothing.
    // Buffer the lines here and let Program replay them once the real logging
    // pipeline exists (ReplayLogInto).
    private readonly List<(LogLevel Level, string Message, object?[] Args, Exception? Error)>
        _pendingLog = new();

    /// <summary>DNS names this host answers to that the CURRENT certificate does
    /// not cover. Empty in the normal case. Non-empty means a newer build added
    /// an alias and we deliberately kept the existing cert rather than void every
    /// browser's stored exception; surfaced by /api/tls/status.</summary>
    public IReadOnlyList<string> UncoveredNames { get; private set; } = Array.Empty<string>();

    public SelfSignedCertService(IConfiguration config, ILogger<SelfSignedCertService> logger) {
        _logger = logger;
        // Kept so the SAN list can honour an Mdns:InstanceName override; the
        // cert has to carry whatever name mDNS actually advertises.
        _config = config;
        _certDir = config.GetValue("Server:Https:CertDir",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA.Polaris", "cert"))!;
        Directory.CreateDirectory(_certDir);
        _certPath = Path.Combine(_certDir, "polaris.pfx");
        _sanListPath = Path.Combine(_certDir, "polaris.san");
    }

    /// <summary>Emit and remember. The remembered copy is replayed into the real
    /// logger once the host is built; see <see cref="ReplayLogInto"/>.</summary>
    private void Log(LogLevel level, string message, params object?[] args) {
        _pendingLog.Add((level, message, args, null));
        _logger.Log(level, message, args);
    }

    private void LogError(Exception ex, string message, params object?[] args) {
        _pendingLog.Add((LogLevel.Warning, message, args, ex));
        _logger.LogWarning(ex, message, args);
    }

    /// <summary>Replay everything decided before the logging pipeline existed.
    /// Call once, after builder.Build(). Idempotent: the buffer is cleared.</summary>
    public void ReplayLogInto(ILogger logger) {
        foreach (var (level, message, args, error) in _pendingLog) {
            if (error != null) logger.Log(level, error, message, args);
            else               logger.Log(level, message, args);
        }
        _pendingLog.Clear();
    }

    /// <summary>
    /// Return a usable cert. Loads from disk when fresh + matching the
    /// current host; otherwise regenerates. Cached in memory for the
    /// process lifetime so Kestrel can be configured up-front and a
    /// later renewal cycle is the only thing that re-reads the file.
    /// </summary>
    public X509Certificate2 GetOrCreate() {
        if (_cached != null) return _cached;

        var sanList = BuildSanList();

        // Is the existing cert still good? Regenerating is never free: a new
        // certificate has a new fingerprint, which voids the exception every
        // browser and both mobile apps have stored, and Firefox will not extend
        // a freshly accepted exception to the WebSocket handshakes of the same
        // page load. So the bar for regenerating is "the old cert cannot do the
        // job", not "something about it differs".
        if (File.Exists(_certPath)) {
            try {
                var existing = LoadFromDisk();
                if (existing != null) {
                    var reason = WhyRegenerate(existing, sanList, out var uncovered);
                    if (reason == null) {
                        UncoveredNames = uncovered;
                        if (uncovered.Count > 0) {
                            // A release added an alias the running cert predates.
                            // Everything the operator already uses still validates,
                            // so keep it: the new name starts working at the next
                            // natural renewal, when they have to re-trust anyway.
                            Log(LogLevel.Warning,
                                "HTTPS cert does not cover {Count} name(s) this host now answers to "
                                + "({Names}). Keeping the existing certificate so saved browser and "
                                + "app exceptions stay valid; the new names are picked up at renewal.",
                                uncovered.Count, string.Join(", ", uncovered));
                        }
                        Log(LogLevel.Information,
                            "HTTPS cert reused (valid until {Expiry:yyyy-MM-dd}, "
                            + "fingerprint {Thumbprint}). SAN entries: {Count}.",
                            existing.NotAfter, existing.Thumbprint, sanList.Count);
                        _cached = existing;
                        return _cached;
                    }
                    Log(LogLevel.Information,
                        "HTTPS cert regenerating: {Reason}. Clients that trusted the old "
                        + "certificate have to accept the new one once.", reason);
                }
            } catch (Exception ex) {
                LogError(ex, "HTTPS cert reload failed, regenerating.");
            }
        }
        UncoveredNames = Array.Empty<string>();

        _cached = Generate(sanList);
        try {
            File.WriteAllBytes(_certPath, _cached.Export(X509ContentType.Pfx));
            // The sidecar used to hold a hash of the SAN list and was the gate
            // for regenerating. The gate now reads the names out of the cert
            // itself, which cannot go stale, so the file is kept only as a
            // readable record of what this certificate covers: the first thing
            // worth seeing when someone reports "it will not validate".
            File.WriteAllLines(_sanListPath, sanList);
        } catch (Exception ex) {
            // Cert was generated in memory and is usable for this run
            // even if we can't persist, log + soldier on. Next boot
            // will retry.
            LogError(ex, "HTTPS cert persisted-write failed; using in-memory copy this run.");
        }
        Log(LogLevel.Information,
            "HTTPS cert generated (valid until {Expiry:yyyy-MM-dd}, "
            + "fingerprint {Thumbprint}). SAN entries: {Count}.",
            _cached.NotAfter, _cached.Thumbprint, sanList.Count);
        return _cached;
    }

    /// <summary>
    /// Why the on-disk certificate has to be replaced, or null to keep it.
    /// <paramref name="uncovered"/> receives the DNS names this host answers to
    /// that the cert omits; those alone are NOT a reason to regenerate.
    /// </summary>
    private static string? WhyRegenerate(
            X509Certificate2 cert, IReadOnlyList<string> required, out IReadOnlyList<string> uncovered) {
        uncovered = Array.Empty<string>();

        if ((cert.NotAfter - cert.NotBefore).TotalDays > 398) {
            // Apple (iOS / Safari, Chrome on iOS) rejects certs whose validity
            // exceeds 398 days. Old builds issued a 5-year cert.
            return "validity exceeds Apple's 398-day limit, so iOS refuses it";
        }
        if (cert.NotAfter <= DateTime.UtcNow.AddDays(30)) {
            return $"it expires {cert.NotAfter:yyyy-MM-dd}";
        }
        if (!IsValidRootCa(cert)) {
            // GX-12q3: old cert is a leaf (CA:FALSE) or missing KeyCertSign, so
            // the install-as-trusted-root workflow cannot work on Chrome.
            return "it lacks CA:TRUE / KeyCertSign, so Chrome will not accept it as a trusted root";
        }

        return WhyNamesForceRegeneration(CoveredNames(cert), required, out uncovered);
    }

    /// <summary>
    /// The name half of <see cref="WhyRegenerate"/>, split out so the policy can
    /// be tested without synthesising a certificate for every case.
    /// </summary>
    internal static string? WhyNamesForceRegeneration(
            ISet<string> covered, IReadOnlyList<string> required, out IReadOnlyList<string> uncovered) {
        uncovered = Array.Empty<string>();
        var missing = required.Where(n => !covered.Contains(n)).ToList();

        // An IPv4 address this host is reachable at, missing from the cert,
        // means the machine moved networks: anyone typing that address gets a
        // name mismatch, and their old exception was pinned to an address that
        // no longer reaches this host anyway. Nothing is preserved by keeping it.
        //
        // IPv4 ONLY, deliberately. A global IPv6 address is not a stable
        // identity: privacy extensions rotate the interface id on a timer and
        // the ISP rotates the delegated prefix, so a host with working IPv6
        // grows and loses addresses on its own, with nothing about the
        // installation having changed. Counting those regenerated the
        // certificate every day or two, and every regeneration voids the
        // exception stored by every browser and both mobile apps. Nobody types
        // an IPv6 literal to reach their observatory either; the LAN IPv4
        // address and the mDNS name are what get used, and both still count.
        var movedTo = missing
            .Where(n => IPAddress.TryParse(n, out var ip)
                        && ip.AddressFamily == AddressFamily.InterNetwork)
            .ToList();
        if (movedTo.Count > 0) {
            return "this host is now reachable at "
                + string.Join(", ", movedTo) + ", which the certificate does not cover";
        }

        // Only names that cost nobody anything to omit: DNS aliases a newer
        // build started advertising, and IPv6 addresses that rotate on their
        // own. Keep the cert; the caller reports them instead.
        uncovered = missing;
        return null;
    }

    /// <summary>Every name in the cert's subjectAltName, DNS and IP alike,
    /// compared the same way <see cref="BuildSanList"/> builds them.</summary>
    private static HashSet<string> CoveredNames(X509Certificate2 cert) {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ext in cert.Extensions) {
            if (ext.Oid?.Value != "2.5.29.17") continue;   // subjectAltName
            var san = new X509SubjectAlternativeNameExtension(ext.RawData, ext.Critical);
            foreach (var dns in san.EnumerateDnsNames()) names.Add(dns);
            foreach (var ip in san.EnumerateIPAddresses()) names.Add(ip.ToString());
        }
        return names;
    }

    /// <summary>The cert's SHA-1 thumbprint (uppercase hex, colon-separated).
    /// Kept for backwards compatibility but modern browsers (Chrome 90+,
    /// Firefox 100+, Safari 14+) only show SHA-256 in their cert-details
    /// dialog, use <see cref="Fingerprint256"/> for those.</summary>
    public string Fingerprint {
        get {
            var c = GetOrCreate();
            var raw = c.Thumbprint ?? "";
            // Format as XX:XX:... matching Chrome's UI
            return string.Join(":", Enumerable.Range(0, raw.Length / 2)
                .Select(i => raw.Substring(i * 2, 2)));
        }
    }

    /// <summary>The cert's SHA-256 thumbprint (lowercase hex,
    /// no separators), the only fingerprint format modern browsers
    /// show in their cert-details dialog. User compares this against
    /// Polaris Settings to verify the cert their browser sees is the
    /// one Polaris generated (not a man-in-the-middle's).
    ///
    /// Format matches what Chrome displays: 64 hex chars, lowercase,
    /// no colons, copy-paste friendly. (Chrome's UI elides whitespace
    /// when you double-click to select.)</summary>
    public string Fingerprint256 {
        get {
            var c = GetOrCreate();
            var hash = System.Security.Cryptography.SHA256.HashData(c.RawData);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }

    /// <summary>SAN entries baked into the cert (DNS + IP). Settings UI
    /// shows the DNS list so the user knows which URLs are valid.
    ///
    /// Read out of the certificate rather than recomputed from the host, which
    /// is what the doc above always claimed and what the operator needs: after
    /// an upgrade adds an alias, the two lists differ, and the one that decides
    /// whether a URL validates is this one.</summary>
    public IReadOnlyList<string> SanEntries() =>
        CoveredNames(GetOrCreate()).OrderBy(x => x, StringComparer.Ordinal).ToList();

    /// <summary>
    /// GX-12q3: pre-flight check that the on-disk cert can actually
    /// function as a Chrome-trusted root anchor when installed.
    /// Returns false for the old leaf-cert (CA:FALSE) format so the
    /// next GetOrCreate call regenerates it with the correct
    /// extensions. Idempotent: a freshly-generated cert always passes.
    /// </summary>
    private static bool IsValidRootCa(X509Certificate2 cert) {
        var bc = cert.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .FirstOrDefault();
        if (bc == null || !bc.CertificateAuthority) return false;
        var ku = cert.Extensions
            .OfType<X509KeyUsageExtension>()
            .FirstOrDefault();
        if (ku == null) return false;
        // KeyCertSign is the bit that lets the cert sign other certs,
        // including itself (the root → leaf chain Chrome expects).
        return (ku.KeyUsages & X509KeyUsageFlags.KeyCertSign)
            == X509KeyUsageFlags.KeyCertSign;
    }

    // ─── internals ────────────────────────────────────────────────────

    private X509Certificate2? LoadFromDisk() {
        try {
            // X509KeyStorageFlags.PersistKeySet keeps the private key
            // alongside the PFX on Windows; without it the key may
            // get GC'd when the X509Certificate2 falls out of scope.
            return X509CertificateLoader.LoadPkcs12FromFile(_certPath, (string?)null,
                X509KeyStorageFlags.PersistKeySet
                | X509KeyStorageFlags.MachineKeySet
                | X509KeyStorageFlags.Exportable);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Existing PFX unreadable, regenerating.");
            return null;
        }
    }

    private X509Certificate2 Generate(IReadOnlyList<string> sanList) {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=NINA.Polaris (self-signed)",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        // GX-12q3 fix: previous version emitted CA:FALSE which made
        // the cert a leaf, useless as a trust anchor. Windows happily
        // imported it into "Trusted Root Certification Authorities"
        // but Chrome rejected the chain at validation time because a
        // non-CA cert can't sign anything, not even itself in browser
        // logic. Symptom: install succeeds, restart browser, still
        // shows "Not secure". Self-signed acting as both root + leaf
        // needs CA:TRUE + KeyCertSign permission.
        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: true,
                hasPathLengthConstraint: false,
                pathLengthConstraint: 0,
                critical: true));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                // KeyCertSign added: needed to satisfy Chrome's "this
                // cert is the trust anchor for the chain that ends in
                // itself" check when installed as Trusted Root.
                // DigitalSignature + KeyEncipherment: the TLS handshake
                // cipher suites need them.
                X509KeyUsageFlags.DigitalSignature
                | X509KeyUsageFlags.KeyEncipherment
                | X509KeyUsageFlags.KeyCertSign,
                critical: true));
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") /* TLS Server Auth */ },
                critical: true));
        // Subject Key Identifier, helps Chrome match the leaf usage of
        // this cert against its own "self as root" entry in the trust
        // store. Generated from the public key, deterministic.
        req.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(req.PublicKey, critical: false));

        // SAN, the part that decides which hostnames/IPs Chrome accepts.
        var sanBuilder = new SubjectAlternativeNameBuilder();
        foreach (var name in sanList) {
            if (IPAddress.TryParse(name, out var ip)) sanBuilder.AddIpAddress(ip);
            else sanBuilder.AddDnsName(name);
        }
        req.CertificateExtensions.Add(sanBuilder.Build());

        // Apple (iOS / Safari, and Chrome on iOS) REJECTS TLS certs whose total
        // validity exceeds 398 days (ERR_CERT_VALIDITY_TOO_LONG), so a multi-year
        // self-signed cert makes Polaris unreachable from an iPhone/iPad. Keep the
        // span under 398 days: NotBefore is backdated 1 day for clock skew, so 396
        // days ahead gives a 397-day total. GetOrCreate auto-renews when under 30
        // days remain, so the shorter life is covered on the next start.
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter  = DateTimeOffset.UtcNow.AddDays(396);
        var rawCert   = req.CreateSelfSigned(notBefore, notAfter);

        // CreateSelfSigned returns a cert with an in-memory ephemeral
        // key. Round-tripping through PFX export+import binds the key
        // to the X509Certificate2 in a way that survives MachineKeySet
        // persistence on Windows and avoids a "no private key"
        // surprise when Kestrel tries to use it for TLS.
        var pfxBytes = rawCert.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfxBytes, (string?)null,
            X509KeyStorageFlags.PersistKeySet
            | X509KeyStorageFlags.MachineKeySet
            | X509KeyStorageFlags.Exportable);
    }

    /// <summary>Enumerate every DNS name + IPv4/IPv6 a Polaris client
    /// might legitimately use to reach this host. Order is irrelevant
    /// (the cert lists them all); we de-dupe + sort so the SAN hash
    /// is stable across reboots when the network hasn't changed.</summary>
    private List<string> BuildSanList() {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Every DNS name this device answers to, from the single source that
        // also drives the mDNS advertisement.
        //
        // This list used to be assembled here by hand, and it omitted the one
        // name that matters most: mDNS advertises polaris-app-{shortId}.local
        // so that cloned cards do not collide, while the cert carried only a
        // bare polaris-app.local. A client that DISCOVERED the device and
        // connected to the name discovery handed it therefore always hit a
        // certificate error, and the more the fleet relied on unique names the
        // worse it got. Two components deciding the same fact independently.
        foreach (var n in DeviceIdentity.DnsNames(_config)) names.Add(n);

        // Loopback always (covers 127.0.0.1 + ::1 paths).
        names.Add("127.0.0.1");
        names.Add("::1");

        // All active NICs' non-link-local, non-loopback addresses.
        try {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()) {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                foreach (var ua in props.UnicastAddresses) {
                    var addr = ua.Address;
                    if (IPAddress.IsLoopback(addr)) continue;
                    // Skip link-local (169.254.x.x, fe80::/10), those
                    // don't route across the LAN and rarely show up in
                    // a user's URL bar.
                    if (addr.IsIPv6LinkLocal) continue;
                    if (addr.AddressFamily == AddressFamily.InterNetwork) {
                        var b = addr.GetAddressBytes();
                        if (b[0] == 169 && b[1] == 254) continue;
                    }
                    names.Add(addr.ToString());
                }
            }
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Network interface enumeration failed; SAN list will be host-aliases-only.");
        }

        return names.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

}