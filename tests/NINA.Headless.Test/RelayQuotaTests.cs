using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Relay.Server;
using NUnit.Framework;

namespace NINA.Headless.Test;

[TestFixture]
public class RelayQuotaTests {
    private string _tenantsPath = null!;
    private string _usagePath = null!;

    [SetUp]
    public void SetUp() {
        _tenantsPath = Path.Combine(Path.GetTempPath(), $"tenants-{Guid.NewGuid():N}.json");
        _usagePath = Path.Combine(Path.GetTempPath(), $"usage-{Guid.NewGuid():N}.json");
    }

    [TearDown]
    public void TearDown() {
        try { File.Delete(_tenantsPath); } catch { }
        try { File.Delete(_usagePath); } catch { }
    }

    private IConfiguration BuildConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Relay:TenantsFile"] = _tenantsPath,
            ["Relay:UsageStateFile"] = _usagePath
        }).Build();

    [Test]
    public void UsageStore_ChargeAccumulates() {
        using var store = new TenantUsageStore(BuildConfig(), NullLogger<TenantUsageStore>.Instance);
        Assert.That(store.Charge("tok", 1000), Is.EqualTo(1000));
        Assert.That(store.Charge("tok", 500),  Is.EqualTo(1500));
        Assert.That(store.BytesThisMonth("tok"), Is.EqualTo(1500));
    }

    [Test]
    public void UsageStore_PersistsAcrossInstances() {
        using (var s1 = new TenantUsageStore(BuildConfig(), NullLogger<TenantUsageStore>.Instance)) {
            s1.Charge("tok", 1234);
            // Dispose flushes
        }
        using var s2 = new TenantUsageStore(BuildConfig(), NullLogger<TenantUsageStore>.Instance);
        Assert.That(s2.BytesThisMonth("tok"), Is.EqualTo(1234));
    }

    [Test]
    public void UsageStore_Reset_ZeroesCurrentMonth() {
        using var store = new TenantUsageStore(BuildConfig(), NullLogger<TenantUsageStore>.Instance);
        store.Charge("tok", 9999);
        store.Reset("tok");
        Assert.That(store.BytesThisMonth("tok"), Is.EqualTo(0));
    }

    [Test]
    public void TenantRegistry_RejectsExpiredToken() {
        File.WriteAllText(_tenantsPath, """
        { "tenants": [
            { "token": "expired", "hostname": "old", "expiresAt": "2000-01-01T00:00:00Z" },
            { "token": "valid",   "hostname": "ok",  "expiresAt": "2099-12-31T23:59:59Z" }
        ] }
        """);
        using var jsonStore = new JsonTenantStore(BuildConfig(), NullLogger<JsonTenantStore>.Instance);
        var registry = new TenantRegistry(jsonStore);

        Assert.That(registry.TryAuthenticate("expired", out _, out _, out var reason), Is.False);
        Assert.That(reason, Does.Contain("expired"));

        Assert.That(registry.TryAuthenticate("valid", out var hostname, out _, out _), Is.True);
        Assert.That(hostname, Is.EqualTo("ok"));
    }

    [Test]
    public void TenantRegistry_RejectsDisabledToken() {
        File.WriteAllText(_tenantsPath, """
        { "tenants": [
            { "token": "off", "hostname": "h", "enabled": false }
        ] }
        """);
        using var jsonStore = new JsonTenantStore(BuildConfig(), NullLogger<JsonTenantStore>.Instance);
        var registry = new TenantRegistry(jsonStore);

        Assert.That(registry.TryAuthenticate("off", out _, out _, out var reason), Is.False);
        Assert.That(reason, Does.Contain("disabled"));
    }

    [Test]
    public void UsageStore_DistinctTenants_DontInterfere() {
        using var store = new TenantUsageStore(BuildConfig(), NullLogger<TenantUsageStore>.Instance);
        store.Charge("alice", 100);
        store.Charge("bob", 50);
        Assert.That(store.BytesThisMonth("alice"), Is.EqualTo(100));
        Assert.That(store.BytesThisMonth("bob"), Is.EqualTo(50));
    }
}
