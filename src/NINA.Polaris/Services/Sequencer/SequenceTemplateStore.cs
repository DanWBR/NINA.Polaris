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

namespace NINA.Polaris.Services.Sequencer;

/// <summary>
/// Disk-backed library of reusable sequence fragments. Stored as one JSON
/// file per template under <c>Sequencer:TemplateDir</c> (default
/// <c>./sequencer-templates</c>). Each template is a full
/// <see cref="SequenceDocument"/>, but only its root container's children
/// + triggers + conditions are spliced in by <see cref="TemplatedContainer"/>.
/// </summary>
public class SequenceTemplateStore {
    private readonly string _dir;
    private readonly ILogger<SequenceTemplateStore> _logger;

    public SequenceTemplateStore(IConfiguration config, ProfileService profiles,
                                 ILogger<SequenceTemplateStore> logger) {
        _logger = logger;
        var configured = config.GetValue<string?>("Sequencer:TemplateDir");
        if (!string.IsNullOrWhiteSpace(configured)) {
            // Respect an explicit config path verbatim (absolute or relative).
            _dir = configured!;
        } else {
            // Default under the writable per-user data dir, NOT the process
            // working directory. As a systemd service that CWD is the (root-
            // owned) install dir /opt/polaris, so a relative "sequencer-templates"
            // failed to create with UnauthorizedAccessException.
            _dir = Path.Combine(profiles.DataDir, "sequencer-templates");
        }
        try { Directory.CreateDirectory(_dir); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not create template dir {Dir}", _dir); }
    }

    public string Dir => _dir;

    public IEnumerable<string> List() {
        if (!Directory.Exists(_dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(_dir, "*.json")
            .Select(p => Path.GetFileNameWithoutExtension(p)!)
            .OrderBy(n => n);
    }

    public SequenceDocument? Load(string name) {
        var path = ResolvePath(name);
        if (!File.Exists(path)) return null;
        try {
            return SequenceJson.Deserialize(File.ReadAllText(path));
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Could not load template {Name}", name);
            return null;
        }
    }

    public void Save(string name, SequenceDocument doc) {
        doc.UpdatedAt = DateTime.UtcNow;
        var path = ResolvePath(name);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, SequenceJson.Serialize(doc));
        File.Move(tmp, path, overwrite: true);
        _logger.LogInformation("Saved template {Name} → {Path}", name, path);
    }

    public bool Delete(string name) {
        var path = ResolvePath(name);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        _logger.LogInformation("Deleted template {Name}", name);
        return true;
    }

    private string ResolvePath(string name) {
        // Defensive: strip any path separators so callers can't read arbitrary files
        var safe = string.Concat(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'));
        if (string.IsNullOrWhiteSpace(safe)) throw new ArgumentException("Empty template name");
        return Path.Combine(_dir, safe + ".json");
    }
}