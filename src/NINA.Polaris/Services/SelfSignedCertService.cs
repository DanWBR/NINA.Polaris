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
    private readonly string _certDir;
    private readonly string _certPath;
    private readonly string _sanHashPath;
    private X509Certificate2? _cached;

    public SelfSignedCertService(IConfiguration config, ILogger<SelfSignedCertService> logger) {
        _logger = logger;
        _certDir = config.GetValue("Server:Https:CertDir",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA.Polaris", "cert"))!;
        Directory.CreateDirectory(_certDir);
        _certPath = Path.Combine(_certDir, "polaris.pfx");
        _sanHashPath = Path.Combine(_certDir, "polaris.san");
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
        var sanHash = ComputeSanHash(sanList);

        // Cheap "is the existing cert still good?" check before
        // touching the disk. Missing PFX / missing hash sidecar /
        // mismatch / expiry-soon all trigger regeneration.
        if (File.Exists(_certPath) && File.Exists(_sanHashPath)) {
            try {
                var existingHash = File.ReadAllText(_sanHashPath).Trim();
                if (existingHash == sanHash) {
                    var existing = LoadFromDisk();
                    if (existing != null
                        && existing.NotAfter > DateTime.UtcNow.AddDays(30)
                        && IsValidRootCa(existing)) {
                        _logger.LogInformation(
                            "HTTPS cert reused (valid until {Expiry:yyyy-MM-dd}, "
                            + "fingerprint {Thumbprint}). SAN entries: {Count}.",
                            existing.NotAfter, existing.Thumbprint, sanList.Count);
                        _cached = existing;
                        return _cached;
                    }
                    if (existing != null && !IsValidRootCa(existing)) {
                        // GX-12q3: old cert is a leaf (CA:FALSE) or
                        // missing KeyCertSign, install-as-trusted-root
                        // workflow won't work with it on Chrome. Force
                        // regeneration so users get the fixed cert
                        // automatically after an app update.
                        _logger.LogInformation(
                            "HTTPS cert lacks CA:TRUE / KeyCertSign, "
                            + "regenerating so Chrome accepts it as a trusted root.");
                    }
                } else {
                    _logger.LogInformation(
                        "HTTPS cert SAN entries changed (host moved networks?), regenerating.");
                }
            } catch (Exception ex) {
                _logger.LogWarning(ex, "HTTPS cert reload failed, regenerating.");
            }
        }

        _cached = Generate(sanList);
        try {
            File.WriteAllBytes(_certPath, _cached.Export(X509ContentType.Pfx));
            File.WriteAllText(_sanHashPath, sanHash);
        } catch (Exception ex) {
            // Cert was generated in memory and is usable for this run
            // even if we can't persist, log + soldier on. Next boot
            // will retry.
            _logger.LogWarning(ex, "HTTPS cert persisted-write failed; using in-memory copy this run.");
        }
        _logger.LogInformation(
            "HTTPS cert generated (valid until {Expiry:yyyy-MM-dd}, "
            + "fingerprint {Thumbprint}). SAN entries: {Count}.",
            _cached.NotAfter, _cached.Thumbprint, sanList.Count);
        return _cached;
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
    /// shows the DNS list so the user knows which URLs are valid.</summary>
    public IReadOnlyList<string> SanEntries() => BuildSanList();

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
            return new X509Certificate2(_certPath, (string?)null,
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

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter  = DateTimeOffset.UtcNow.AddYears(5);
        var rawCert   = req.CreateSelfSigned(notBefore, notAfter);

        // CreateSelfSigned returns a cert with an in-memory ephemeral
        // key. Round-tripping through PFX export+import binds the key
        // to the X509Certificate2 in a way that survives MachineKeySet
        // persistence on Windows and avoids a "no private key"
        // surprise when Kestrel tries to use it for TLS.
        var pfxBytes = rawCert.Export(X509ContentType.Pfx);
        return new X509Certificate2(pfxBytes, (string?)null,
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

        // Always-on DNS aliases.
        names.Add("localhost");
        var hostName = Dns.GetHostName();
        if (!string.IsNullOrWhiteSpace(hostName)) {
            names.Add(hostName);
            // hostname.local, the form mDNS responders typically expose
            names.Add(hostName + ".local");
        }
        names.Add("polaris.local");
        names.Add("polaris-app.local");

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

    private static string ComputeSanHash(IReadOnlyList<string> sanList) {
        var joined = string.Join("\n", sanList);
        var bytes = System.Text.Encoding.UTF8.GetBytes(joined);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
