using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Relay.Server;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class RelayAuditLogTests {
    private string _path = null!;

    [SetUp]
    public void SetUp() {
        _path = Path.Combine(Path.GetTempPath(), $"audit-{Guid.NewGuid():N}.log");
    }

    [TearDown]
    public void TearDown() {
        try { File.Delete(_path); } catch { }
        try { File.Delete(_path + ".1"); } catch { }
    }

    private IConfiguration BuildConfig(Dictionary<string, string?>? extra = null) {
        var dict = new Dictionary<string, string?> {
            ["Audit:Enabled"] = "true",
            ["Audit:Path"] = _path,
            ["Audit:RingBufferSize"] = "100",
            ["Audit:MaxFileBytes"] = "1048576"
        };
        if (extra != null) foreach (var kv in extra) dict[kv.Key] = kv.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Test]
    public void Record_AppendsToRingBuffer() {
        using var log = new AuditLog(BuildConfig(), NullLogger<AuditLog>.Instance);
        log.Record(new AuditRecord { Tenant = "alice", Method = "GET", Path = "/", Status = 200 });
        log.Record(new AuditRecord { Tenant = "bob",   Method = "POST", Path = "/", Status = 201 });
        var snap = log.Snapshot().ToArray();
        Assert.That(snap.Length, Is.EqualTo(2));
        Assert.That(snap[0].Tenant, Is.EqualTo("alice"));
        Assert.That(snap[1].Tenant, Is.EqualTo("bob"));
    }

    [Test]
    public void Snapshot_FiltersByTenant() {
        using var log = new AuditLog(BuildConfig(), NullLogger<AuditLog>.Instance);
        log.Record(new AuditRecord { Tenant = "alice", Status = 200 });
        log.Record(new AuditRecord { Tenant = "bob",   Status = 200 });
        log.Record(new AuditRecord { Tenant = "alice", Status = 404 });
        var alice = log.Snapshot("alice").ToArray();
        Assert.That(alice.Length, Is.EqualTo(2));
        Assert.That(alice.All(r => r.Tenant == "alice"), Is.True);
    }

    [Test]
    public void Snapshot_LimitReturnsMostRecent() {
        using var log = new AuditLog(BuildConfig(), NullLogger<AuditLog>.Instance);
        for (int i = 0; i < 10; i++)
            log.Record(new AuditRecord { Tenant = "t", Status = i });
        var last3 = log.Snapshot(limit: 3).ToArray();
        Assert.That(last3.Length, Is.EqualTo(3));
        Assert.That(last3[0].Status, Is.EqualTo(7));
        Assert.That(last3[2].Status, Is.EqualTo(9));
    }

    [Test]
    public void RingBuffer_CapsSize() {
        // Ring size = 5
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Audit:Enabled"] = "true",
            ["Audit:Path"] = _path,
            ["Audit:RingBufferSize"] = "5"
        }).Build();
        using var log = new AuditLog(cfg, NullLogger<AuditLog>.Instance);
        for (int i = 0; i < 20; i++)
            log.Record(new AuditRecord { Tenant = "t", Status = i });
        var snap = log.Snapshot().ToArray();
        Assert.That(snap.Length, Is.LessThanOrEqualTo(5));
        // Oldest should have been dropped — last entry is status=19
        Assert.That(snap.Last().Status, Is.EqualTo(19));
    }

    [Test]
    public void Disabled_DoesNotRecord() {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Audit:Enabled"] = "false",
            ["Audit:Path"] = _path
        }).Build();
        using var log = new AuditLog(cfg, NullLogger<AuditLog>.Instance);
        log.Record(new AuditRecord { Tenant = "alice", Status = 200 });
        Assert.That(log.Snapshot().Count(), Is.EqualTo(0));
        Assert.That(log.Enabled, Is.False);
    }

    [Test]
    public async Task FlushesToDisk() {
        using (var log = new AuditLog(BuildConfig(), NullLogger<AuditLog>.Instance)) {
            log.Record(new AuditRecord { Tenant = "alice", Method = "GET", Path = "/foo", Status = 200 });
            // Give the background writer a beat
            await Task.Delay(100);
        }
        // Dispose forces flush
        Assert.That(File.Exists(_path), Is.True);
        var lines = File.ReadAllLines(_path);
        Assert.That(lines.Length, Is.GreaterThanOrEqualTo(1));
        Assert.That(lines[0], Does.Contain("alice"));
        Assert.That(lines[0], Does.Contain("/foo"));
    }
}

[TestFixture]
public class RelayTenantConfigMtlsTests {
    [Test]
    public void TenantConfig_ClientCertThumbprint_DefaultsNull() {
        var t = new TenantConfig();
        Assert.That(t.ClientCertThumbprint, Is.Null);
    }

    [Test]
    public void TenantConfig_ClientCertThumbprint_Roundtrips() {
        var t = new TenantConfig { ClientCertThumbprint = "ABC123" };
        Assert.That(t.ClientCertThumbprint, Is.EqualTo("ABC123"));
    }
}
