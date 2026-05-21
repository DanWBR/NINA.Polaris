using NINA.Relay.Server;
using NUnit.Framework;

namespace NINA.Headless.Test;

[TestFixture]
public class RelayRateLimiterTests {
    [Test]
    public void Unlimited_AlwaysAdmits() {
        var lim = new TenantRateLimiter(new TenantConfig { RequestsPerSecond = 0, BytesPerSecond = 0 });
        for (int i = 0; i < 1000; i++) {
            Assert.That(lim.TryAcquire(10_000_000, out _), Is.True, "iteration " + i);
        }
    }

    [Test]
    public void RequestBucket_DrainsAndRefuses() {
        // 1 req/s, burst 2
        var lim = new TenantRateLimiter(new TenantConfig {
            RequestsPerSecond = 1, BurstRequests = 2, BytesPerSecond = 0
        });
        Assert.That(lim.TryAcquire(0, out _), Is.True);
        Assert.That(lim.TryAcquire(0, out _), Is.True);
        Assert.That(lim.TryAcquire(0, out var r), Is.False);
        Assert.That(r.LimitedBy, Is.EqualTo("request rate"));
        Assert.That(r.RetryAfterSeconds, Is.GreaterThan(0));
    }

    [Test]
    public void ByteBucket_DrainsAndRefuses() {
        // 100 bytes/s, burst 200
        var lim = new TenantRateLimiter(new TenantConfig {
            RequestsPerSecond = 0, BytesPerSecond = 100, BurstBytes = 200
        });
        Assert.That(lim.TryAcquire(150, out _), Is.True);
        Assert.That(lim.TryAcquire(100, out var r), Is.False);
        Assert.That(r.LimitedBy, Is.EqualTo("bandwidth"));
    }

    [Test]
    public void TryAcquire_FailureDoesNotConsumeTokens() {
        var lim = new TenantRateLimiter(new TenantConfig {
            RequestsPerSecond = 0, BytesPerSecond = 100, BurstBytes = 100
        });
        // First request bigger than burst → rejected, no tokens consumed
        Assert.That(lim.TryAcquire(500, out _), Is.False);
        // Smaller request still fits because tokens weren't drained
        Assert.That(lim.TryAcquire(50, out _), Is.True);
    }

    [Test]
    public async Task Refill_RestoresTokensOverTime() {
        var lim = new TenantRateLimiter(new TenantConfig {
            RequestsPerSecond = 10, BurstRequests = 2, BytesPerSecond = 0
        });
        // Burn the bucket
        Assert.That(lim.TryAcquire(0, out _), Is.True);
        Assert.That(lim.TryAcquire(0, out _), Is.True);
        Assert.That(lim.TryAcquire(0, out _), Is.False);
        // After 150ms at 10/s we should have ~1.5 tokens again
        await Task.Delay(200);
        Assert.That(lim.TryAcquire(0, out _), Is.True);
    }

    [Test]
    public void BurstDefaults_ToTwiceTheRate_WhenUnspecified() {
        var lim = new TenantRateLimiter(new TenantConfig {
            RequestsPerSecond = 5, BurstRequests = 0, BytesPerSecond = 1000, BurstBytes = 0
        });
        Assert.That(lim.RequestBurst, Is.EqualTo(10));
        Assert.That(lim.ByteBurst, Is.EqualTo(2000));
    }

    [Test]
    public void ChargeBytes_DrainsBucketWithoutBlocking() {
        var lim = new TenantRateLimiter(new TenantConfig {
            RequestsPerSecond = 0, BytesPerSecond = 100, BurstBytes = 200
        });
        // Charge enough to drain past the bucket
        lim.ChargeBytes(500);
        // Now even a tiny request should fail (bucket is negative)
        Assert.That(lim.TryAcquire(50, out var r), Is.False);
        Assert.That(r.LimitedBy, Is.EqualTo("bandwidth"));
    }
}
