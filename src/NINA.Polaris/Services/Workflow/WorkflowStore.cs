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

namespace NINA.Polaris.Services.Workflow;

/// <summary>
/// Disk-backed library of saved "Auto Workflow" definitions — the ordered list
/// of post-processing steps the STUDIO Auto Workflow tab applies to a source
/// image. Stored as one JSON file per workflow under
/// <c>{profiles.DataDir}/workflows</c>.
///
/// The server is intentionally SCHEMA-AGNOSTIC: the workflow document format
/// (step <c>$type</c> discriminators + per-step params) is owned by the client
/// (app.js step-type registry), so this store round-trips the raw JSON text
/// verbatim. That keeps adding a new step type a client-only change. Mirrors
/// <see cref="Sequencer.SequenceTemplateStore"/> (atomic temp+rename write,
/// name sanitisation).
/// </summary>
public class WorkflowStore {
    private readonly string _dir;
    private readonly ILogger<WorkflowStore> _logger;

    public WorkflowStore(ProfileService profiles, ILogger<WorkflowStore> logger) {
        _logger = logger;
        _dir = Path.Combine(profiles.DataDir, "workflows");
        try { Directory.CreateDirectory(_dir); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not create workflow dir {Dir}", _dir); }
    }

    public string Dir => _dir;

    public IEnumerable<string> List() {
        if (!Directory.Exists(_dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(_dir, "*.json")
            .Select(p => Path.GetFileNameWithoutExtension(p)!)
            .OrderBy(n => n);
    }

    /// <summary>Raw workflow JSON text, or null when the file is missing.</summary>
    public string? Load(string name) {
        var path = ResolvePath(name);
        if (!File.Exists(path)) return null;
        try {
            return File.ReadAllText(path);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Could not load workflow {Name}", name);
            return null;
        }
    }

    public void Save(string name, string json) {
        var path = ResolvePath(name);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
        _logger.LogInformation("Saved workflow {Name} → {Path}", name, path);
    }

    /// <summary>
    /// Seed the built-in "Standard" default workflow on first run so the
    /// Auto Workflow Load list is not empty out of the box. Guarded by a
    /// one-time marker file so a user who deletes or edits "Standard" does
    /// not get it resurrected on the next launch. Called once at startup
    /// (NOT from the constructor, so unit tests get a clean store).
    /// </summary>
    public void SeedDefaults() {
        try {
            var marker = Path.Combine(_dir, ".seeded-standard-v1");
            if (File.Exists(marker)) return;
            var path = ResolvePath("Standard");
            if (!File.Exists(path)) {
                Save("Standard", StandardWorkflowJson);
                _logger.LogInformation("Seeded default Auto Workflow 'Standard'");
            }
            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Could not seed default workflow");
        }
    }

    // The "Standard" starter pipeline: mirrors a typical PixInsight+Lightroom
    // flow end to end (auto-crop the stacking borders → BGE → decon → denoise
    // → auto-stretch → the Lightroom-style light/colour/detail adjustments →
    // JPG 90%). Shape matches what the client saves (version/name/steps with
    // $type/enabled/params); params match each step's registry defaults, with
    // the auto-tunable light/colour items left on Auto so a plain Run produces
    // a good result, and gentle non-zero texture/NR/sharpen at the end.
    private const string StandardWorkflowJson = """
    {
      "version": 1,
      "name": "Standard",
      "steps": [
        { "$type": "autocrop",    "enabled": true, "params": { "threshold": 0, "margin": 0 } },
        { "$type": "bge",         "enabled": true, "params": { "correction": "Subtraction", "smoothing": 1.0 } },
        { "$type": "detail",      "enabled": true, "params": { "strength": 0.5, "psfPixels": 4.0, "auto": true } },
        { "$type": "denoise",     "enabled": true, "params": { "strength": 0.5 } },
        { "$type": "autostretch", "enabled": true, "params": {} },
        { "$type": "exposure",    "enabled": true, "params": { "value": 0, "auto": true } },
        { "$type": "contrast",    "enabled": true, "params": { "value": 0, "auto": true } },
        { "$type": "blacks",      "enabled": true, "params": { "value": 0, "auto": true } },
        { "$type": "whites",      "enabled": true, "params": { "value": 0, "auto": true } },
        { "$type": "temp",        "enabled": true, "params": { "value": 6500 } },
        { "$type": "tint",        "enabled": true, "params": { "value": 0 } },
        { "$type": "vibrance",    "enabled": true, "params": { "value": 0, "auto": true } },
        { "$type": "saturation",  "enabled": true, "params": { "value": 0, "auto": true } },
        { "$type": "texture",     "enabled": true, "params": { "value": 0.15 } },
        { "$type": "noisereduce", "enabled": true, "params": { "value": 0.12 } },
        { "$type": "sharpen",     "enabled": true, "params": { "value": 0.15 } },
        { "$type": "export",      "enabled": true, "params": { "format": "jpg", "quality": 90 } }
      ]
    }
    """;

    public bool Delete(string name) {
        var path = ResolvePath(name);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        _logger.LogInformation("Deleted workflow {Name}", name);
        return true;
    }

    private string ResolvePath(string name) {
        // Defensive: strip any path separators so callers can't read/write arbitrary files.
        var safe = string.Concat(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' '));
        safe = safe.Trim();
        if (string.IsNullOrWhiteSpace(safe)) throw new ArgumentException("Empty workflow name");
        return Path.Combine(_dir, safe + ".json");
    }
}
