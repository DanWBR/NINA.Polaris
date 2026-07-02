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
