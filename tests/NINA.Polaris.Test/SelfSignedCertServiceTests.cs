using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the cert generation + reuse path. We don't try to verify TLS
/// handshakes here (that needs a live Kestrel), instead we check the
/// PFX is valid, contains a private key, has the SAN extension wired,
/// and is reused across calls when nothing changes about the host.
/// </summary>
[TestFixture]
public class SelfSignedCertServiceTests {
    private string _tempDir = "";

    [SetUp]
    public void SetUp() {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "polaris-cert-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(_tempDir)) {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private SelfSignedCertService MakeService() {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Server:Https:CertDir"] = _tempDir,
            })
            .Build();
        return new SelfSignedCertService(config,
            NullLogger<SelfSignedCertService>.Instance);
    }

    [Test]
    public void GetOrCreate_GeneratesValidCertWithPrivateKey() {
        var svc = MakeService();
        var cert = svc.GetOrCreate();

        Assert.That(cert, Is.Not.Null);
        Assert.That(cert.HasPrivateKey, Is.True,
            "Kestrel needs the private key to handshake; PFX export+import must preserve it.");
        Assert.That(cert.NotAfter, Is.GreaterThan(DateTime.UtcNow.AddYears(4)),
            "5-year validity (give or take a leap day), make sure we're not creating a same-day-expiry cert.");
        Assert.That(cert.NotBefore, Is.LessThan(DateTime.UtcNow),
            "NotBefore is back-dated so clock-skew on the client doesn't reject the cert.");
        Assert.That(cert.Subject, Does.Contain("NINA.Polaris"));
    }

    [Test]
    public void GetOrCreate_PersistsPfxAndSanHashSidecar() {
        var svc = MakeService();
        svc.GetOrCreate();

        Assert.That(File.Exists(Path.Combine(_tempDir, "polaris.pfx")),
            "PFX must be on disk so subsequent boots can reuse without browser re-trust.");
        Assert.That(File.Exists(Path.Combine(_tempDir, "polaris.san")),
            "SAN-hash sidecar is the marker for 'did the host's name list change?'.");
    }

    [Test]
    public void GetOrCreate_ReusesCertAcrossCallsWithinProcess() {
        var svc = MakeService();
        var first  = svc.GetOrCreate();
        var second = svc.GetOrCreate();
        Assert.That(second, Is.SameAs(first),
            "In-memory cache short-circuits, same instance both calls.");
        Assert.That(second.Thumbprint, Is.EqualTo(first.Thumbprint));
    }

    [Test]
    public void GetOrCreate_NewServiceInstanceReusesCertFromDisk() {
        // Simulate a process restart: same temp dir, fresh service.
        var svc1 = MakeService();
        var firstFingerprint = svc1.Fingerprint;

        var svc2 = MakeService();
        Assert.That(svc2.Fingerprint, Is.EqualTo(firstFingerprint),
            "Reused PFX must produce the same fingerprint or the user re-trusts on every boot.");
    }

    [Test]
    public void SanEntries_AlwaysIncludesLocalhostAndPolarisAliases() {
        var svc = MakeService();
        var entries = svc.SanEntries();

        Assert.That(entries, Has.Member("localhost"));
        Assert.That(entries, Has.Member("polaris.local"));
        Assert.That(entries, Has.Member("polaris-app.local"));
        Assert.That(entries, Has.Member("127.0.0.1"));
        Assert.That(entries, Has.Member("::1"));
    }

    [Test]
    public void Fingerprint_IsColonSeparatedHex() {
        var svc = MakeService();
        var fp = svc.Fingerprint;
        // SHA-1 is 20 bytes → 40 hex chars → 19 colons → 59 chars
        Assert.That(fp.Length, Is.EqualTo(59),
            "Fingerprint format must match what Chrome shows in cert details.");
        Assert.That(fp, Does.Match(@"^[0-9A-Fa-f]{2}(:[0-9A-Fa-f]{2}){19}$"));
    }
}
