using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Headless.Services;

public class ProfileService {
    private static readonly JsonSerializerOptions JsonOpts = new() {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _profileDir;
    private readonly string _activeProfilePath;
    private readonly ILogger<ProfileService> _logger;

    private UserProfile _activeProfile = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public UserProfile Active => _activeProfile;

    public ProfileService(IConfiguration config, ILogger<ProfileService> logger) {
        _logger = logger;

        var baseDir = config.GetValue("Profiles:Directory",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA.Headless", "profiles"))!;

        _profileDir = baseDir;
        _activeProfilePath = Path.Combine(baseDir, "active.json");

        Directory.CreateDirectory(_profileDir);
        Load();
    }

    public List<ProfileSummary> ListProfiles() {
        var files = Directory.GetFiles(_profileDir, "*.json")
            .Where(f => !f.EndsWith("active.json"))
            .ToList();

        var profiles = new List<ProfileSummary>();
        foreach (var file in files) {
            try {
                var json = File.ReadAllText(file);
                var p = JsonSerializer.Deserialize<UserProfile>(json, JsonOpts);
                if (p != null) {
                    profiles.Add(new ProfileSummary {
                        Id = Path.GetFileNameWithoutExtension(file),
                        Name = p.Name,
                        LastModified = File.GetLastWriteTimeUtc(file)
                    });
                }
            } catch { }
        }

        return profiles;
    }

    public void Load() {
        if (!File.Exists(_activeProfilePath)) {
            _activeProfile = new UserProfile { Name = "Default" };
            Save();
            _logger.LogInformation("Created default profile at {Path}", _activeProfilePath);
            return;
        }

        try {
            var json = File.ReadAllText(_activeProfilePath);
            _activeProfile = JsonSerializer.Deserialize<UserProfile>(json, JsonOpts)
                ?? new UserProfile { Name = "Default" };
            _logger.LogInformation("Loaded profile: {Name}", _activeProfile.Name);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to load profile, using defaults");
            _activeProfile = new UserProfile { Name = "Default" };
        }
    }

    public void Save() {
        _saveLock.Wait();
        try {
            var json = JsonSerializer.Serialize(_activeProfile, JsonOpts);
            File.WriteAllText(_activeProfilePath, json);
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to save profile");
        } finally {
            _saveLock.Release();
        }
    }

    public void SaveAs(string name) {
        var id = SanitizeFileName(name);
        var path = Path.Combine(_profileDir, id + ".json");
        _activeProfile.Name = name;

        try {
            var json = JsonSerializer.Serialize(_activeProfile, JsonOpts);
            File.WriteAllText(path, json);
            File.WriteAllText(_activeProfilePath, json);
            _logger.LogInformation("Profile saved as: {Name} ({Path})", name, path);
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to save profile as {Name}", name);
        }
    }

    public bool LoadProfile(string id) {
        var path = Path.Combine(_profileDir, id + ".json");
        if (!File.Exists(path)) return false;

        try {
            var json = File.ReadAllText(path);
            _activeProfile = JsonSerializer.Deserialize<UserProfile>(json, JsonOpts)
                ?? new UserProfile { Name = "Default" };
            Save();
            _logger.LogInformation("Switched to profile: {Name}", _activeProfile.Name);
            return true;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to load profile {Id}", id);
            return false;
        }
    }

    public void UpdateSettings(Action<UserProfile> update) {
        update(_activeProfile);
        Save();
    }

    private static string SanitizeFileName(string name) {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Where(c => !invalid.Contains(c)).ToArray())
            .Replace(' ', '_').ToLowerInvariant();
    }
}

public class UserProfile {
    public string Name { get; set; } = "Default";

    // Observatory location
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Altitude { get; set; }

    // Camera optics
    public double SensorWidthMm { get; set; } = 23.5;
    public double SensorHeightMm { get; set; } = 15.7;
    public double FocalLengthMm { get; set; } = 478;
    public int SensorPixelsX { get; set; } = 6248;
    public int SensorPixelsY { get; set; } = 4176;

    // Default imaging settings
    public double DefaultExposure { get; set; } = 30;
    public int DefaultGain { get; set; } = 100;
    public int DefaultBinning { get; set; } = 1;

    // INDI connection
    public string IndiHost { get; set; } = "localhost";
    public int IndiPort { get; set; } = 7624;

    // Equipment selection (remembered)
    public string? LastCamera { get; set; }
    public string? LastTelescope { get; set; }
    public string? LastFocuser { get; set; }
    public string? LastFilterWheel { get; set; }

    // Plate solver
    public string? AstapPath { get; set; }
    public double SolveToleranceArcsec { get; set; } = 30;

    // Image output
    public string ImageOutputDir { get; set; } = "";
    public string ImageNamePattern { get; set; } = "{target}_{filter}_{exposure}s_{date}_{seq}";
    public string ImageFormat { get; set; } = "fits";
}

public class ProfileSummary {
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime LastModified { get; set; }
}
