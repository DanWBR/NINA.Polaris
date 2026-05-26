namespace NINA.Relay.Server;

/// <summary>
/// Per-tenant token-bucket rate limiter. Tracks two independent buckets:
/// one for HTTP request count and one for total bytes (request + response,
/// both directions). Each call to <see cref="TryAcquire"/> drains tokens
/// from the matching bucket(s); refills happen continuously at the
/// configured rate.
///
/// A limit value of 0 disables that particular bucket (effectively unlimited).
/// Thread-safe via a single lock per limiter, fine at the scale of one
/// limiter per active tenant.
/// </summary>
public class TenantRateLimiter {
    private readonly object _gate = new();
    private double _requestTokens;
    private double _byteTokens;
    private DateTime _lastRefill;

    public double RequestsPerSecond { get; }
    public double RequestBurst { get; }
    public long BytesPerSecond { get; }
    public long ByteBurst { get; }

    public TenantRateLimiter(TenantConfig cfg) {
        RequestsPerSecond = cfg.RequestsPerSecond;
        RequestBurst = cfg.BurstRequests > 0 ? cfg.BurstRequests
                       : (cfg.RequestsPerSecond > 0 ? cfg.RequestsPerSecond * 2 : 0);
        BytesPerSecond = cfg.BytesPerSecond;
        ByteBurst = cfg.BurstBytes > 0 ? cfg.BurstBytes
                    : (cfg.BytesPerSecond > 0 ? cfg.BytesPerSecond * 2 : 0);

        _requestTokens = RequestBurst;
        _byteTokens = ByteBurst;
        _lastRefill = DateTime.UtcNow;
    }

    private void Refill() {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastRefill).TotalSeconds;
        if (elapsed <= 0) return;
        _lastRefill = now;
        if (RequestsPerSecond > 0)
            _requestTokens = Math.Min(RequestBurst, _requestTokens + RequestsPerSecond * elapsed);
        if (BytesPerSecond > 0)
            _byteTokens = Math.Min(ByteBurst, _byteTokens + BytesPerSecond * elapsed);
    }

    /// <summary>
    /// Try to charge 1 request + <paramref name="bytes"/> bytes. Returns
    /// false if either bucket lacks enough tokens; on false, no tokens
    /// are consumed.
    /// </summary>
    public bool TryAcquire(long bytes, out RateLimitResult result) {
        lock (_gate) {
            Refill();

            // Check request bucket
            if (RequestsPerSecond > 0 && _requestTokens < 1) {
                var wait = (1 - _requestTokens) / RequestsPerSecond;
                result = new RateLimitResult(false, "request rate", wait);
                return false;
            }
            // Check byte bucket
            if (BytesPerSecond > 0 && _byteTokens < bytes) {
                var wait = bytes <= ByteBurst
                    ? (bytes - _byteTokens) / BytesPerSecond
                    : double.PositiveInfinity; // request larger than burst will never fit
                result = new RateLimitResult(false, "bandwidth", wait);
                return false;
            }

            if (RequestsPerSecond > 0) _requestTokens -= 1;
            if (BytesPerSecond > 0) _byteTokens -= bytes;
            result = new RateLimitResult(true, null, 0);
            return true;
        }
    }

    /// <summary>
    /// Charge bytes only (no request count). Used for response bodies after
    /// the request has already been admitted, counted but never blocks the
    /// in-flight response.
    /// </summary>
    public void ChargeBytes(long bytes) {
        lock (_gate) {
            Refill();
            if (BytesPerSecond > 0) _byteTokens -= bytes;
        }
    }
}

public record RateLimitResult(bool Allowed, string? LimitedBy, double RetryAfterSeconds);
