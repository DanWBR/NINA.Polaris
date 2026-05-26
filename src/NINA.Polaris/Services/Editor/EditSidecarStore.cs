using System.Text.Json;
using NINA.Image.Editor;

namespace NINA.Polaris.Services.Editor;

/// <summary>
/// Reads + writes Lightroom-style sidecar JSON describing the edits
/// applied to a source file. Sidecar lives next to the source as
/// <c>{source}.edit.json</c>; reopening the editor for that source
/// hydrates the sliders to the saved state, strictly non-destructive,
/// the original FITS/PNG/etc. is never touched.
///
/// Atomic writes via the "write to temp + rename" pattern (Windows
/// File.Move overwrites since .NET Core 3+ when destination exists is
/// handled via the overload with `overwrite: true`).
///
/// If the source's directory isn't writable (read-only mount, etc.) the
/// sidecar falls back to <c>{AppData}/Polaris/sidecars/{md5(path)}.edit.json</c>.
/// </summary>
public class EditSidecarStore {
    private readonly ILogger<EditSidecarStore> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new() {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public EditSidecarStore(ILogger<EditSidecarStore> logger) {
        _logger = logger;
    }

    /// <summary>
    /// Read the sidecar for <paramref name="sourcePath"/>. Returns null
    /// if no sidecar exists; returns default <see cref="EditParams"/>
    /// (with no edits) if the sidecar exists but parsing fails.
    /// </summary>
    public EditParams? Load(string sourcePath) {
        var path = ResolveSidecarPath(sourcePath, mustExist: false);
        if (path == null || !File.Exists(path)) return null;

        try {
            var json = File.ReadAllText(path);
            var doc = JsonSerializer.Deserialize<SidecarFile>(json, JsonOpts);
            if (doc == null) return null;
            if (doc.Version != 1) {
                _logger.LogWarning("Editor sidecar at {Path} has unsupported version {V}; ignoring.",
                    path, doc.Version);
                return null;
            }
            return doc.Edits;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Editor sidecar parse failed for {Path}; returning null.", path);
            return null;
        }
    }

    /// <summary>
    /// Write the sidecar. Returns the absolute path the sidecar was
    /// written to (may be the AppData fallback if the source's
    /// directory wasn't writable).
    /// </summary>
    public string? Save(string sourcePath, EditParams edits) {
        var path = ResolveSidecarPath(sourcePath, mustExist: true);
        if (path == null) return null;

        var doc = new SidecarFile(
            Version: 1,
            Source: Path.GetFileName(sourcePath),
            SavedAt: DateTime.UtcNow,
            Edits: edits);

        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, JsonOpts));
            // Move with overwrite, atomic on the same volume.
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return path;
        } catch (Exception ex) {
            _logger.LogError(ex, "Editor sidecar write failed for {Source} -> {Path}", sourcePath, path);
            return null;
        }
    }

    /// <summary>
    /// Pick the sidecar path. Tries source-adjacent first; falls back to
    /// AppData when that's not writable (e.g. read-only SMB mount).
    /// </summary>
    private string? ResolveSidecarPath(string sourcePath, bool mustExist) {
        if (string.IsNullOrWhiteSpace(sourcePath)) return null;
        var adjacent = sourcePath + ".edit.json";

        // Quick writeability check: do we have permission to create the
        // adjacent file? We try to create-and-delete a probe.
        try {
            var dir = Path.GetDirectoryName(sourcePath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) {
                var probe = Path.Combine(dir, ".polaris-write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllBytes(probe, Array.Empty<byte>());
                File.Delete(probe);
                return adjacent;
            }
        } catch {
            // fallthrough to AppData
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var fallbackDir = Path.Combine(appData, "Polaris", "sidecars");
        var hash = HashPath(sourcePath);
        return Path.Combine(fallbackDir, hash + ".edit.json");
    }

    private static string HashPath(string s) {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var bytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private record SidecarFile(int Version, string Source, DateTime SavedAt, EditParams Edits);
}
