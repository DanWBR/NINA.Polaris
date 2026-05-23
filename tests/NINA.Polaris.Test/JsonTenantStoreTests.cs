using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Relay.Server;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class JsonTenantStoreTests {
    private string _tmpPath = null!;

    [SetUp]
    public void SetUp() {
        _tmpPath = Path.Combine(Path.GetTempPath(), $"tenants-{Guid.NewGuid():N}.json");
    }

    [TearDown]
    public void TearDown() {
        try { File.Delete(_tmpPath); } catch { }
    }

    private IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Test]
    public void LoadsTenantsFromJsonFile() {
        File.WriteAllText(_tmpPath, """
        {
          "tenants": [
            { "token": "tok1", "hostname": "alice", "enabled": true, "requestsPerSecond": 10 },
            { "token": "tok2", "hostname": "bob",   "enabled": false }
          ]
        }
        """);
        var cfg = BuildConfig(new() { ["Relay:TenantsFile"] = _tmpPath });
        using var store = new JsonTenantStore(cfg, NullLogger<JsonTenantStore>.Instance);

        Assert.That(store.TryGet("tok1", out var t1), Is.True);
        Assert.That(t1.Hostname, Is.EqualTo("alice"));
        Assert.That(t1.RequestsPerSecond, Is.EqualTo(10));

        Assert.That(store.TryGet("tok2", out var t2), Is.True);
        Assert.That(t2.Enabled, Is.False);

        Assert.That(store.TryGet("nope", out _), Is.False);
    }

    [Test]
    public void FallsBackToAppsettingsTenantsSection_WhenNoFile() {
        var cfg = BuildConfig(new() {
            ["Tenants:abc"] = "alice",
            ["Tenants:def"] = "bob"
        });
        using var store = new JsonTenantStore(cfg, NullLogger<JsonTenantStore>.Instance);

        Assert.That(store.TryGet("abc", out var t1), Is.True);
        Assert.That(t1.Hostname, Is.EqualTo("alice"));
        Assert.That(t1.Enabled, Is.True);
        Assert.That(store.TryGet("def", out var t2), Is.True);
        Assert.That(t2.Hostname, Is.EqualTo("bob"));
    }

    [Test]
    public void BrokenJson_KeepsPreviousTenants() {
        File.WriteAllText(_tmpPath, """
        { "tenants": [ { "token": "tok1", "hostname": "alice" } ] }
        """);
        var cfg = BuildConfig(new() { ["Relay:TenantsFile"] = _tmpPath });
        using var store = new JsonTenantStore(cfg, NullLogger<JsonTenantStore>.Instance);
        Assert.That(store.TryGet("tok1", out _), Is.True);

        // Now corrupt and reload
        File.WriteAllText(_tmpPath, "{ this is not json");
        store.Reload();

        // Previous tenant set must still be valid
        Assert.That(store.TryGet("tok1", out _), Is.True);
    }

    [Test]
    public void SkipsEntriesMissingTokenOrHostname() {
        File.WriteAllText(_tmpPath, """
        {
          "tenants": [
            { "token": "",      "hostname": "alice" },
            { "token": "tok2",  "hostname": "" },
            { "token": "tok3",  "hostname": "carol" }
          ]
        }
        """);
        var cfg = BuildConfig(new() { ["Relay:TenantsFile"] = _tmpPath });
        using var store = new JsonTenantStore(cfg, NullLogger<JsonTenantStore>.Instance);

        Assert.That(store.All.Count, Is.EqualTo(1));
        Assert.That(store.TryGet("tok3", out _), Is.True);
    }
}
