using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace NINA.Polaris.Services.Auth;

/// <summary>
/// AUTH-1: server-side state for the local-auth feature. Owns the
/// password hash (lives on UserProfile so it survives restart) and
/// an in-memory session store (intentionally non-persistent: server
/// restart invalidates all sessions, which is also our "I forgot
/// the password, SSH in and reset" lever).
///
/// Hashing: PBKDF2-SHA256, 100k iterations, 16-byte random salt per
/// password. AuthHashAlgo on the profile is parsed so a future move
/// to Argon2id can read old hashes without forcing a logout. All
/// password comparisons go through CryptographicOperations.
/// FixedTimeEquals to defeat timing attacks.
///
/// Sessions: 32-byte random base64-url token. SessionInfo tracks
/// LastActivityAt; ValidateToken bumps it on every hit so an
/// active session never times out. A 10-minute sweeper purges
/// stale entries.
///
/// Rate limit: per-IP failed-attempt counter, max 5 failures per
/// minute then exponential backoff capped at 1h. Successful login
/// clears the bucket for that IP. Server restart resets the
/// limiter (acceptable: lockouts are a usability speed-bump, not
/// a hard security boundary; the password hash is the real wall).
/// </summary>
public class AuthService : IDisposable {
    private readonly ProfileService _profile;
    private readonly ILogger<AuthService> _logger;
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private readonly ConcurrentDictionary<string, AttemptBucket> _attempts = new();
    private readonly Timer _sweeper;
    private readonly string _sessionStorePath;
    private readonly SemaphoreSlim _persistLock = new(1, 1);

    private const int Pbkdf2Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int TokenBytes = 32;
    private const int MaxFailuresPerMinute = 5;
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxLockout = TimeSpan.FromHours(1);

    public AuthService(ProfileService profile, ILogger<AuthService> logger) {
        _profile = profile;
        _logger = logger;
        _sessionStorePath = Path.Combine(profile.DataDir, "auth-sessions.json");
        // Restore sessions from disk so a `systemctl restart polaris`
        // doesn't invalidate every logged-in browser. Without this,
        // any redeploy boots every device out + the user has to
        // re-type the password — and worse, in-flight <img>/<ws>
        // requests fail silently with 401 because they can't
        // intercept and prompt for re-login the way JSON fetches can.
        LoadSessionsFromDisk();
        _sweeper = new Timer(_ => SweepExpired(), null,
            TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    public bool IsConfigured =>
        !string.IsNullOrEmpty(_profile.Active.AuthPasswordHash);

    public bool IsEnabled => _profile.Active.AuthEnabled;

    public int SessionTimeoutHours =>
        Math.Max(1, _profile.Active.AuthSessionTimeoutHours);

    private TimeSpan SessionTtl => TimeSpan.FromHours(SessionTimeoutHours);

    /// <summary>Validates a session token and bumps its activity timestamp.
    /// Returns false for unknown / expired tokens. Loopback bypass is
    /// the middleware's responsibility, not this method's.</summary>
    public bool ValidateToken(string? token) {
        if (string.IsNullOrEmpty(token)) return false;
        if (!_sessions.TryGetValue(token, out var s)) return false;
        if (DateTime.UtcNow - s.LastActivityAt > SessionTtl) {
            _sessions.TryRemove(token, out _);
            return false;
        }
        s.LastActivityAt = DateTime.UtcNow;
        return true;
    }

    /// <summary>First-run: sets the password when none exists yet. Returns
    /// a session token on success, null when already configured (caller
    /// should redirect to /login).</summary>
    public string? SetInitialPassword(string password) {
        if (IsConfigured) return null;
        ValidatePasswordStrength(password);
        var (hash, salt) = HashPassword(password);
        _profile.Active.AuthPasswordHash = hash;
        _profile.Active.AuthPasswordSalt = salt;
        _profile.Active.AuthHashAlgo = "pbkdf2-sha256-100000";
        _profile.Save();
        _logger.LogInformation("Auth: initial password set");
        return CreateSession();
    }

    /// <summary>Change password after authenticating with the current one.
    /// Invalidates all other sessions (forces other devices to log in
    /// again with the new password). Returns the new session token to
    /// the caller so they don't get bumped to login themselves.</summary>
    public string? ChangePassword(
            string current, string newPassword, string keepSessionToken) {
        if (!IsConfigured) return null;
        if (!VerifyPassword(current)) return null;
        ValidatePasswordStrength(newPassword);
        var (hash, salt) = HashPassword(newPassword);
        _profile.Active.AuthPasswordHash = hash;
        _profile.Active.AuthPasswordSalt = salt;
        _profile.Save();
        // Drop every session except the caller's, force re-login on
        // other devices.
        foreach (var k in _sessions.Keys.ToArray()) {
            if (k != keepSessionToken) _sessions.TryRemove(k, out _);
        }
        _logger.LogInformation("Auth: password changed; {Count} other sessions invalidated",
            _sessions.Count - 1);
        PersistSessions();
        return keepSessionToken;
    }

    /// <summary>Authenticate with the password. Returns null on failure
    /// (caller surfaces 401 + increments rate-limit). On success
    /// returns a new session token and clears the rate-limit bucket.</summary>
    public string? Login(string password, IPAddress? remoteIp) {
        var ipKey = remoteIp?.ToString() ?? "unknown";
        if (IsRateLimited(ipKey, out var retryAfter)) {
            _logger.LogWarning(
                "Auth: login rate-limited for {Ip} (retry in {RetryS}s)",
                ipKey, (int)retryAfter.TotalSeconds);
            return null;
        }
        if (!IsConfigured || !VerifyPassword(password)) {
            RegisterFailure(ipKey);
            return null;
        }
        _attempts.TryRemove(ipKey, out _);
        return CreateSession();
    }

    /// <summary>Drop a single session. Idempotent.</summary>
    public void Logout(string token) {
        if (string.IsNullOrEmpty(token)) return;
        if (_sessions.TryRemove(token, out _)) PersistSessions();
    }

    /// <summary>Toggle auth on or off, requires current password.</summary>
    public bool SetEnabled(string currentPassword, bool enabled) {
        if (!IsConfigured) return false;
        if (!VerifyPassword(currentPassword)) return false;
        _profile.Active.AuthEnabled = enabled;
        _profile.Save();
        _logger.LogInformation("Auth: enabled toggled to {Enabled}", enabled);
        return true;
    }

    /// <summary>Read-only snapshot for the /api/auth/status endpoint.</summary>
    public AuthStatusSnapshot GetStatus(string? presentedToken) => new(
        Configured: IsConfigured,
        Enabled: IsEnabled,
        Authenticated: ValidateToken(presentedToken),
        SessionTimeoutHours: SessionTimeoutHours);

    /// <summary>Cookie name for the HttpOnly session cookie, kept on the
    /// service so middleware + endpoints stay in sync.</summary>
    public const string CookieName = "polaris_session";

    public int ActiveSessionCount => _sessions.Count;

    public void Dispose() {
        _sweeper.Dispose();
    }

    // ----- internals --------------------------------------------------

    private string CreateSession() {
        var token = NewToken();
        _sessions[token] = new SessionInfo {
            Token = token,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        PersistSessions();
        return token;
    }

    // ----- session persistence ---------------------------------------
    //
    // Sessions used to be in-memory only, with the documented "I forgot
    // the password" workaround being to restart the server. That had
    // two real-world failure modes:
    //
    //   1. Any redeploy (apt upgrade, systemctl restart) silently
    //      invalidated every logged-in browser. The user had to
    //      re-type the password on every device, and any in-flight
    //      <img> / <iframe> / WebSocket request returned 401 with no
    //      automatic re-prompt (they can't intercept 401 like JSON
    //      fetch handlers do).
    //   2. The password-reset workaround stayed: deleting the file
    //      below has the same effect as the old "restart server"
    //      trick, just with a clearer mental model ("I'm wiping the
    //      session store" vs "I'm restarting a daemon").
    //
    // Format is a tiny JSON: an array of { Token, CreatedAt,
    // LastActivityAt }. File mode 0600 from the systemd unit's
    // umask + chmod in postinst; nothing here forces it explicitly
    // because the data dir already inherits the right permissions
    // from ProfileService. Writes are best-effort: a disk error
    // logs a warning but doesn't break login.

    private void LoadSessionsFromDisk() {
        try {
            if (!File.Exists(_sessionStorePath)) return;
            var json = File.ReadAllText(_sessionStorePath);
            var entries = JsonSerializer.Deserialize<List<SessionInfo>>(json);
            if (entries == null) return;
            var cutoff = DateTime.UtcNow - SessionTtl;
            foreach (var s in entries) {
                if (string.IsNullOrEmpty(s.Token)) continue;
                if (s.LastActivityAt < cutoff) continue;   // drop stale
                _sessions[s.Token] = s;
            }
            _logger.LogInformation(
                "Auth: restored {Count} session(s) from {Path}",
                _sessions.Count, _sessionStorePath);
        } catch (Exception ex) {
            _logger.LogWarning(ex,
                "Auth: failed to load session store at {Path}, " +
                "starting fresh", _sessionStorePath);
        }
    }

    private void PersistSessions() {
        // Best-effort, fire-and-forget on a background thread so a
        // slow SD card doesn't block the login response. The
        // semaphore serialises writes so two concurrent logins can't
        // tear the file.
        _ = Task.Run(async () => {
            await _persistLock.WaitAsync();
            try {
                var snapshot = _sessions.Values.ToList();
                var json = JsonSerializer.Serialize(snapshot);
                // Write-temp + rename so a kill mid-write doesn't
                // leave half a file on disk (which would silently
                // make every session look invalid on next boot).
                var tmp = _sessionStorePath + ".tmp";
                await File.WriteAllTextAsync(tmp, json);
                File.Move(tmp, _sessionStorePath, overwrite: true);
            } catch (Exception ex) {
                _logger.LogWarning(ex,
                    "Auth: failed to persist sessions to {Path}",
                    _sessionStorePath);
            } finally {
                _persistLock.Release();
            }
        });
    }

    private static string NewToken() {
        Span<byte> bytes = stackalloc byte[TokenBytes];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static (string hash, string salt) HashPassword(string password) {
        var saltBytes = new byte[SaltBytes];
        RandomNumberGenerator.Fill(saltBytes);
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(
            password, saltBytes, Pbkdf2Iterations,
            HashAlgorithmName.SHA256, HashBytes);
        return (Convert.ToBase64String(hashBytes),
                Convert.ToBase64String(saltBytes));
    }

    private bool VerifyPassword(string password) {
        var hashStr = _profile.Active.AuthPasswordHash;
        var saltStr = _profile.Active.AuthPasswordSalt;
        if (string.IsNullOrEmpty(hashStr) || string.IsNullOrEmpty(saltStr))
            return false;
        var expected = Convert.FromBase64String(hashStr);
        var salt = Convert.FromBase64String(saltStr);
        // PBKDF2 only path for v1. AuthHashAlgo is parsed for future
        // migration but only the default is implemented today.
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Pbkdf2Iterations,
            HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(derived, expected);
    }

    private static void ValidatePasswordStrength(string password) {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password is required");
        if (password.Length < 8)
            throw new ArgumentException("Password must be at least 8 characters");
    }

    private void SweepExpired() {
        var cutoff = DateTime.UtcNow - SessionTtl;
        bool removedAny = false;
        foreach (var kv in _sessions.ToArray()) {
            if (kv.Value.LastActivityAt < cutoff) {
                if (_sessions.TryRemove(kv.Key, out _)) removedAny = true;
            }
        }
        // Sweeper also takes the opportunity to flush LastActivityAt
        // bumps that accumulated since the last persist. ValidateToken
        // intentionally doesn't write per-request (would thrash the
        // SD card), so a 10-min sweeper cadence is good enough for
        // crash-survival of "user was active right before restart".
        PersistSessions();
        _ = removedAny;
        // Bucket cleanup, keep only the last hour's data.
        var attemptCutoff = DateTime.UtcNow - MaxLockout;
        foreach (var kv in _attempts.ToArray()) {
            if (kv.Value.LockedUntil < attemptCutoff && kv.Value.WindowStart < attemptCutoff) {
                _attempts.TryRemove(kv.Key, out _);
            }
        }
    }

    private bool IsRateLimited(string ipKey, out TimeSpan retryAfter) {
        retryAfter = TimeSpan.Zero;
        if (!_attempts.TryGetValue(ipKey, out var b)) return false;
        if (b.LockedUntil > DateTime.UtcNow) {
            retryAfter = b.LockedUntil - DateTime.UtcNow;
            return true;
        }
        return false;
    }

    private void RegisterFailure(string ipKey) {
        var now = DateTime.UtcNow;
        _attempts.AddOrUpdate(ipKey,
            _ => new AttemptBucket {
                WindowStart = now, Failures = 1, LockedUntil = DateTime.MinValue
            },
            (_, b) => {
                if (now - b.WindowStart > AttemptWindow) {
                    b.WindowStart = now;
                    b.Failures = 1;
                } else {
                    b.Failures++;
                }
                if (b.Failures > MaxFailuresPerMinute) {
                    // Exponential backoff: 1m, 2m, 4m, ... capped at 1h.
                    var over = b.Failures - MaxFailuresPerMinute;
                    var lockMinutes = Math.Min((int)MaxLockout.TotalMinutes,
                        (int)Math.Pow(2, Math.Min(6, over - 1)));
                    b.LockedUntil = now + TimeSpan.FromMinutes(lockMinutes);
                }
                return b;
            });
    }

    private sealed class SessionInfo {
        public string Token { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime LastActivityAt { get; set; }
    }

    private sealed class AttemptBucket {
        public DateTime WindowStart { get; set; }
        public int Failures { get; set; }
        public DateTime LockedUntil { get; set; }
    }
}

public record AuthStatusSnapshot(
    bool Configured,
    bool Enabled,
    bool Authenticated,
    int SessionTimeoutHours);
