using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Relay.Server;

/// <summary>
/// Persistent per-tenant configuration entry. Loaded from <c>tenants.json</c>
/// at startup (and hot-reloaded if the file changes), or falls back to the
/// legacy <c>Tenants:</c> section of <c>appsettings.json</c> when no file is
/// configured.
///
/// Rate limits use a token-bucket model: each tenant gets one bucket for
/// HTTP requests and one for bytes (both directions counted together).
/// A limit value of 0 means "unlimited".
/// </summary>
public class TenantConfig {
    /// <summary>Bearer token the client presents in the Auth frame.</summary>
    public string Token { get; set; } = "";

    /// <summary>Hostname slug used for subdomain / path-prefix routing.</summary>
    public string Hostname { get; set; } = "";

    /// <summary>If false the tunnel is rejected at auth time.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Sustained HTTP request rate; 0 = unlimited.</summary>
    public double RequestsPerSecond { get; set; } = 0;

    /// <summary>Bucket capacity for request bursts; 0 = unlimited.</summary>
    public double BurstRequests { get; set; } = 0;

    /// <summary>Sustained throughput (request + response bodies, both directions).</summary>
    public long BytesPerSecond { get; set; } = 0;

    /// <summary>Bucket capacity for byte bursts; 0 = unlimited.</summary>
    public long BurstBytes { get; set; } = 0;

    /// <summary>
    /// Hard monthly transfer cap in bytes (request + response counted together).
    /// 0 = no cap. Counter resets on the 1st of each UTC month. Once exceeded,
    /// new HTTP requests get 402 Payment Required and new tunnel connections
    /// are refused until the next reset.
    /// </summary>
    public long MonthlyBytes { get; set; } = 0;

    /// <summary>
    /// Optional token expiry. Auth is refused after this UTC timestamp.
    /// Useful for trial / temporary access. Null = never expires.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Optional SHA-1 thumbprint of the X.509 client certificate this tenant
    /// must present (hex, case-insensitive, no spaces or colons). When set,
    /// tunnel auth requires both the bearer token AND a matching client cert
    /// on the TLS connection. Null = bearer token alone.
    /// </summary>
    public string? ClientCertThumbprint { get; set; }

    /// <summary>Optional free-form note (owner/email/description).</summary>
    public string? Note { get; set; }
}

/// <summary>
/// JSON serialisation envelope. Stored as <c>{ "tenants": [ {...}, {...} ] }</c>
/// so the file can grow more top-level keys later without breaking schema.
/// </summary>
public class TenantConfigFile {
    [JsonPropertyName("tenants")]
    public List<TenantConfig> Tenants { get; set; } = new();
}

/// <summary>
/// Loads tenant configs from a JSON file and hot-reloads on change.
/// If no file path is configured (or the file is missing), falls back to
/// the legacy <c>Tenants:</c> section in <c>appsettings.json</c>.
/// </summary>
public class JsonTenantStore : IDisposable {
    private readonly string? _path;
    private readonly ILogger<JsonTenantStore> _logger;
    private readonly IConfiguration _config;
    private FileSystemWatcher? _watcher;
    private volatile Dictionary<string, TenantConfig> _byToken = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public JsonTenantStore(IConfiguration config, ILogger<JsonTenantStore> logger) {
        _config = config;
        _logger = logger;
        _path = config.GetValue<string?>("Relay:TenantsFile");

        Reload();

        if (!string.IsNullOrEmpty(_path)) {
            try {
                var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
                var name = Path.GetFileName(_path);
                if (!string.IsNullOrEmpty(dir)) {
                    Directory.CreateDirectory(dir);
                    _watcher = new FileSystemWatcher(dir, name) {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                        EnableRaisingEvents = true
                    };
                    _watcher.Changed += (_, _) => DebouncedReload();
                    _watcher.Created += (_, _) => DebouncedReload();
                    _watcher.Renamed += (_, _) => DebouncedReload();
                }
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Could not set up tenants.json watcher");
            }
        }
    }

    private DateTime _lastReload = DateTime.MinValue;
    private void DebouncedReload() {
        // Editors often save in 2-3 syscalls; coalesce to one reload
        var now = DateTime.UtcNow;
        if ((now - _lastReload).TotalMilliseconds < 250) return;
        _lastReload = now;
        Task.Delay(150).ContinueWith(_ => {
            try { Reload(); Changed?.Invoke(); } catch (Exception ex) {
                _logger.LogWarning(ex, "Tenants reload failed");
            }
        });
    }

    public void Reload() {
        var dict = new Dictionary<string, TenantConfig>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(_path) && File.Exists(_path)) {
            try {
                var text = File.ReadAllText(_path);
                var parsed = JsonSerializer.Deserialize<TenantConfigFile>(text, new JsonSerializerOptions {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
                if (parsed?.Tenants != null) {
                    foreach (var t in parsed.Tenants) {
                        if (string.IsNullOrWhiteSpace(t.Token) || string.IsNullOrWhiteSpace(t.Hostname)) continue;
                        dict[t.Token] = t;
                    }
                }
                _logger.LogInformation("Loaded {Count} tenants from {Path}", dict.Count, _path);
            } catch (Exception ex) {
                _logger.LogError(ex, "Failed to parse {Path}; keeping previous tenant set", _path);
                return; // Don't clobber a working config with a broken file
            }
        } else {
            // Fallback: appsettings.json Tenants section
            foreach (var c in _config.GetSection("Tenants").GetChildren()) {
                if (string.IsNullOrWhiteSpace(c.Key) || string.IsNullOrWhiteSpace(c.Value)) continue;
                dict[c.Key] = new TenantConfig {
                    Token = c.Key,
                    Hostname = c.Value!,
                    Enabled = true
                };
            }
            if (dict.Count > 0)
                _logger.LogInformation("Loaded {Count} tenants from appsettings.json (legacy Tenants section)", dict.Count);
        }

        _byToken = dict;
    }

    public bool TryGet(string token, out TenantConfig config) {
        if (_byToken.TryGetValue(token, out var c)) {
            config = c;
            return true;
        }
        config = null!;
        return false;
    }

    public IReadOnlyCollection<TenantConfig> All => _byToken.Values;

    /// <summary>
    /// Atomically replace the whole tenant set and persist to disk. Used by the
    /// admin API/UI to add/edit/remove tenants without hand-editing the file.
    /// </summary>
    public void Save(IEnumerable<TenantConfig> tenants) {
        if (string.IsNullOrEmpty(_path))
            throw new InvalidOperationException("Cannot save: no Relay:TenantsFile configured (using legacy appsettings)");
        var list = tenants.Where(t => !string.IsNullOrWhiteSpace(t.Token) && !string.IsNullOrWhiteSpace(t.Hostname)).ToList();
        var file = new TenantConfigFile { Tenants = list };
        var json = JsonSerializer.Serialize(file, new JsonSerializerOptions {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
        // FileSystemWatcher will catch this and call Reload(); call it eagerly
        // anyway so the change is observable immediately to the caller.
        Reload();
        Changed?.Invoke();
    }

    public void Dispose() {
        try { _watcher?.Dispose(); } catch { }
    }
}
