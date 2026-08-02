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

    private SelfSignedCertService MakeService(Dictionary<string, string?>? extra = null) {
        var settings = new Dictionary<string, string?> {
            ["Server:Https:CertDir"] = _tempDir,
        };
        foreach (var kv in extra ?? new Dictionary<string, string?>()) settings[kv.Key] = kv.Value;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
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
        // 397-day validity. Apple rejects a TLS cert whose span exceeds 398 days
        // (ERR_CERT_VALIDITY_TOO_LONG on iOS / Chrome-iOS), so the original 5-year
        // cert was deliberately shortened; this assertion was left behind on that
        // change and still demanded >4 years. Pin BOTH ends now: long enough that
        // we are not issuing a near-instantly-expiring cert, short enough to stay
        // under Apple's cap.
        var validityDays = (cert.NotAfter - cert.NotBefore).TotalDays;
        Assert.That(validityDays, Is.GreaterThan(180),
            "cert must not be near-instantly expiring");
        Assert.That(validityDays, Is.LessThan(398),
            "must stay under Apple's 398-day limit or iOS refuses the cert");
        Assert.That(cert.NotBefore, Is.LessThan(DateTime.UtcNow),
            "NotBefore is back-dated so clock-skew on the client doesn't reject the cert.");
        Assert.That(cert.Subject, Does.Contain("NINA.Polaris"));
    }

    [Test]
    public void GetOrCreate_PersistsPfxAndReadableSanSidecar() {
        var svc = MakeService();
        svc.GetOrCreate();

        Assert.That(File.Exists(Path.Combine(_tempDir, "polaris.pfx")),
            "PFX must be on disk so subsequent boots can reuse without browser re-trust.");

        // The sidecar used to hold a hash and gate regeneration. The gate now
        // reads the names out of the certificate, so the file exists purely as
        // a readable record of what this cert covers, which is the first thing
        // worth seeing when someone reports "it will not validate".
        var sidecar = Path.Combine(_tempDir, "polaris.san");
        Assert.That(File.Exists(sidecar), "SAN sidecar records what the cert covers.");
        Assert.That(File.ReadAllLines(sidecar), Does.Contain("localhost"),
            "sidecar must be the plain name list, not an opaque digest.");
    }

    [Test]
    public void GetOrCreate_KeepsCertWhenOnlyNewDnsAliasesAppear() {
        // A release that adds an alias must NOT void every browser's and both
        // mobile apps' stored exception. Simulate it: issue a cert, then hand a
        // fresh service an extra Mdns:InstanceName that the cert predates.
        var first = MakeService().GetOrCreate();

        var svc = MakeService(new Dictionary<string, string?> {
            ["Mdns:InstanceName"] = "an-alias-the-cert-predates"
        });
        var second = svc.GetOrCreate();

        Assert.That(second.Thumbprint, Is.EqualTo(first.Thumbprint),
            "An additive name change must reuse the cert; regenerating breaks every saved exception.");
        Assert.That(svc.UncoveredNames, Is.Not.Empty,
            "The names the cert does not cover are reported instead of acted on.");
        Assert.That(svc.UncoveredNames, Has.Some.Contains("an-alias-the-cert-predates"));
    }

    // A host with working IPv6 gains and loses global addresses on its own:
    // privacy extensions rotate the interface id on a timer, the ISP rotates the
    // delegated prefix. Treating that as "the machine moved" regenerated the
    // certificate every day or two, and every regeneration voids the exception
    // stored by every browser and both mobile apps. A changed IPv4 address is a
    // different thing: the old one genuinely no longer reaches this host.
    private static readonly ISet<string> Covered = new HashSet<string>(
        new[] { "localhost", "polaris.local", "127.0.0.1", "192.168.1.103",
                "2001:db8:1:2:aaaa:bbbb:cccc:dddd" },
        StringComparer.OrdinalIgnoreCase);

    [Test]
    public void RotatedGlobalIPv6_DoesNotForceRegeneration() {
        var required = Covered.Concat(new[] { "2001:db8:1:2:dc3b:8761:1712:cc28" }).ToList();

        var reason = SelfSignedCertService.WhyNamesForceRegeneration(
            Covered, required, out var uncovered);

        Assert.That(reason, Is.Null,
            "A rotated IPv6 must not cost every client its stored certificate exception.");
        Assert.That(uncovered, Does.Contain("2001:db8:1:2:dc3b:8761:1712:cc28"),
            "It is still reported, just not acted on.");
    }

    [Test]
    public void NewIPv4Address_ForcesRegeneration() {
        var required = Covered.Concat(new[] { "10.9.9.9" }).ToList();

        var reason = SelfSignedCertService.WhyNamesForceRegeneration(
            Covered, required, out _);

        Assert.That(reason, Does.Contain("10.9.9.9"),
            "A new IPv4 address means the host moved; the old cert cannot serve it.");
    }

    [Test]
    public void NewDnsAlias_DoesNotForceRegeneration() {
        var required = Covered.Concat(new[] { "polaris-app-5fb6.local" }).ToList();

        var reason = SelfSignedCertService.WhyNamesForceRegeneration(
            Covered, required, out var uncovered);

        Assert.That(reason, Is.Null,
            "Adding an alias in a release must not void every saved exception in the fleet.");
        Assert.That(uncovered, Does.Contain("polaris-app-5fb6.local"));
    }

    [Test]
    public void SanEntries_ReportsWhatTheCertCoversNotWhatTheHostWants() {
        MakeService().GetOrCreate();

        var svc = MakeService(new Dictionary<string, string?> {
            ["Mdns:InstanceName"] = "an-alias-the-cert-predates"
        });

        Assert.That(svc.SanEntries(), Has.None.Contains("an-alias-the-cert-predates"),
            "Settings shows which URLs actually validate, so it must read the certificate.");
        Assert.That(svc.SanEntries(), Does.Contain("localhost"));
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