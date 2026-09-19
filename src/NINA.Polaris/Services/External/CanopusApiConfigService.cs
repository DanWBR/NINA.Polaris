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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Polaris.Services.External;

/// <summary>Provider, model and API key for the "Cloud API with your key"
/// Canopus backend (Anthropic, OpenAI or any OpenAI-compatible server).</summary>
public sealed record CanopusApiConfig(string Provider, string BaseUrl, string Model, string ApiKey) {
    public static readonly CanopusApiConfig Default = new("anthropic", "", "", "");
    public bool HasKey => !string.IsNullOrEmpty(ApiKey);
}

/// <summary>Patch semantics for every field: null keeps the stored value. For
/// the key, "" clears it and anything else replaces it, like the relay token.</summary>
public sealed record CanopusApiConfigUpdate(string? Provider = null, string? BaseUrl = null,
                                            string? Model = null, string? ApiKey = null);

/// <summary>
/// Stores the cloud API configuration in its own file under the data dir,
/// <c>canopus/api-config.json</c>, NOT in the profile: <c>GET /api/system/profile</c>
/// returns the profile verbatim to every client, and the key must never leave the
/// host. Callers that answer HTTP use <see cref="GetPublic"/>, which carries
/// <c>hasKey</c> and never the key itself.
/// </summary>
public sealed class CanopusApiConfigService {
    public static readonly string[] Providers = { "anthropic", "openai", "compatible" };

    private static readonly JsonSerializerOptions _json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly string _suggestionsPath;
    private readonly ILogger<CanopusApiConfigService> _logger;
    private readonly object _lock = new();
    private CanopusApiConfig? _cached;
    private Dictionary<string, string[]>? _suggestions;

    public CanopusApiConfigService(ProfileService profiles, ILogger<CanopusApiConfigService> logger)
        : this(Path.Combine(profiles.DataDir, "canopus", "api-config.json"),
               Path.Combine(AppContext.BaseDirectory, "canopus", "shared", "models", "api-models.json"), logger) { }

    /// <summary>Explicit paths, for tests.</summary>
    public CanopusApiConfigService(string path, string suggestionsPath, ILogger<CanopusApiConfigService> logger) {
        _path = path;
        _suggestionsPath = suggestionsPath;
        _logger = logger;
    }

    public string FilePath => _path;

    public CanopusApiConfig Get() {
        lock (_lock) {
            if (_cached != null) return _cached;
            try {
                if (File.Exists(_path)) {
                    var stored = JsonSerializer.Deserialize<StoredConfig>(File.ReadAllText(_path), _json);
                    if (stored != null)
                        _cached = new CanopusApiConfig(
                            NormalizeProvider(stored.Provider) ?? "anthropic",
                            stored.BaseUrl?.Trim() ?? "", stored.Model?.Trim() ?? "", stored.ApiKey ?? "");
                }
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Canopus API config unreadable at {Path}; using defaults", _path);
            }
            return _cached ??= CanopusApiConfig.Default;
        }
    }

    public bool HasKey => Get().HasKey;

    /// <summary>Apply a patch and persist. Throws <see cref="ArgumentException"/>
    /// on an unknown provider.</summary>
    public CanopusApiConfig Update(CanopusApiConfigUpdate req) {
        lock (_lock) {
            var cur = Get();
            var provider = cur.Provider;
            if (req.Provider != null) {
                provider = NormalizeProvider(req.Provider)
                    ?? throw new ArgumentException($"Unknown provider '{req.Provider}'. Use anthropic, openai or compatible.");
            }
            var next = new CanopusApiConfig(
                provider,
                req.BaseUrl != null ? req.BaseUrl.Trim() : cur.BaseUrl,
                req.Model != null ? req.Model.Trim() : cur.Model,
                req.ApiKey == null ? cur.ApiKey : req.ApiKey.Trim());
            Save(next);
            _cached = next;
            return next;
        }
    }

    /// <summary>What the Settings panel is allowed to see.</summary>
    public object GetPublic() {
        var c = Get();
        return new {
            provider = c.Provider,
            baseUrl = c.BaseUrl,
            model = c.Model,
            hasKey = c.HasKey,
            suggestions = Suggestions(),
        };
    }

    /// <summary>Null when the API backend can start; otherwise the user-facing
    /// reason shown in Settings and by the proxy.</summary>
    public string? Validate() {
        var c = Get();
        if (!c.HasKey) return "Add an API key in Settings, Assistant.";
        if (c.Provider == "compatible" && string.IsNullOrWhiteSpace(c.BaseUrl))
            return "Enter the base URL of the OpenAI-compatible server.";
        if (string.IsNullOrWhiteSpace(c.Model)) return "Choose a model.";
        return null;
    }

    public Dictionary<string, string[]> Suggestions() {
        if (_suggestions != null) return _suggestions;
        var result = new Dictionary<string, string[]>();
        try {
            if (File.Exists(_suggestionsPath)) {
                using var doc = JsonDocument.Parse(File.ReadAllText(_suggestionsPath));
                foreach (var p in Providers) {
                    if (doc.RootElement.TryGetProperty(p, out var arr) && arr.ValueKind == JsonValueKind.Array)
                        result[p] = arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray();
                }
            }
        } catch (Exception ex) {
            _logger.LogDebug(ex, "api-models.json unreadable");
        }
        foreach (var p in Providers) result.TryAdd(p, Array.Empty<string>());
        return _suggestions = result;
    }

    public static string? NormalizeProvider(string? p) {
        var v = p?.Trim().ToLowerInvariant();
        return v != null && Providers.Contains(v) ? v : null;
    }

    private void Save(CanopusApiConfig c) {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(
            new StoredConfig { Provider = c.Provider, BaseUrl = c.BaseUrl, Model = c.Model, ApiKey = c.ApiKey }, _json));
        if (!OperatingSystem.IsWindows()) {
            try { File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch (Exception ex) { _logger.LogDebug(ex, "chmod 600 failed for {Path}", tmp); }
        }
        File.Move(tmp, _path, overwrite: true);
    }

    private sealed class StoredConfig {
        public string? Provider { get; set; }
        public string? BaseUrl { get; set; }
        public string? Model { get; set; }
        [JsonPropertyName("apiKey")] public string? ApiKey { get; set; }
    }
}
