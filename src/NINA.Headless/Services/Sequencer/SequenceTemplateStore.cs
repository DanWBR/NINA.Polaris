namespace NINA.Headless.Services.Sequencer;

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

    public SequenceTemplateStore(IConfiguration config, ILogger<SequenceTemplateStore> logger) {
        _dir = config.GetValue<string?>("Sequencer:TemplateDir") ?? "sequencer-templates";
        _logger = logger;
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
