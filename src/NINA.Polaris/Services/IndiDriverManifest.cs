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

using System.Xml.Linq;

namespace NINA.Polaris.Services;

/// <summary>
/// Which manufacturer each INDI driver belongs to, read from the driver XML
/// files INDI installs next to its binaries.
///
/// <para>Why this exists: INDI labels a device by its PRODUCT and keeps the
/// brand in an attribute. "Sesto Senso 2" and "Rotator Lite V2" are the labels;
/// "Primaluce Lab" and "Wanderer Astro" are only in <c>manufacturer</c>. indi-web
/// serves the label, the binary and the family, and drops the brand, so an
/// operator looking for their Wanderer or PrimaLuceLab device found nothing and
/// concluded the drivers were missing. They were installed the whole time. This
/// puts the brand back.</para>
///
/// <para>The files are the ones INDI itself ships: <c>drivers.xml</c> for the
/// core set, plus one XML per third-party driver package. Every entry looks
/// like <c>&lt;device label="ZWO CCD" manufacturer="ZWO"&gt;&lt;driver
/// name="ZWO CCD"&gt;indi_asi_ccd&lt;/driver&gt;&lt;/device&gt;</c>.</para>
///
/// <para>Entirely best effort. On Windows, on a host where INDI is installed
/// somewhere else, or when the files cannot be read, every lookup returns null
/// and the picker shows exactly what it showed before.</para>
/// </summary>
public class IndiDriverManifest {

    private readonly ILogger<IndiDriverManifest> _logger;
    private readonly object _lock = new();

    private Dictionary<string, string>? _byBinary;   // indi_asi_ccd  -> ZWO
    private Dictionary<string, string>? _byLabel;    // "ZWO CCD"     -> ZWO
    private DateTime _loadedUtc;

    /// <summary>Where INDI keeps its driver XML. The second path is where a
    /// hand-built driver lands (packaging/indi/build-driver-deb.sh), which is
    /// how a board gets a driver its distribution does not package.</summary>
    public static readonly string[] SearchDirs = {
        "/usr/share/indi", "/usr/local/share/indi",
    };

    /// <summary>Re-read after this long. The set changes only when someone runs
    /// apt, so this is about not holding a stale map for a whole night, not
    /// about keeping up with anything.</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(10);

    public IndiDriverManifest(ILogger<IndiDriverManifest> logger) {
        _logger = logger;
    }

    /// <summary>The manufacturer for a driver, by binary name first (exact and
    /// unambiguous) and by label second (what indi-web reports). Null when the
    /// XML does not say, which is common enough: about a hundred of the core
    /// entries carry no manufacturer at all.</summary>
    public string? ManufacturerFor(string? binary, string? label) {
        EnsureLoaded();
        lock (_lock) {
            if (_byBinary == null || _byLabel == null) return null;
            if (!string.IsNullOrWhiteSpace(binary)
                    && _byBinary.TryGetValue(binary.Trim(), out var m)) return m;
            if (!string.IsNullOrWhiteSpace(label)
                    && _byLabel.TryGetValue(label.Trim(), out var m2)) return m2;
            return null;
        }
    }

    /// <summary>How many devices the installed XML describes. Not the same as
    /// the number of drivers indi-web reports (one binary can serve several
    /// devices), and used only as a rough sense of how complete the install
    /// is.</summary>
    public int KnownDeviceCount {
        get { EnsureLoaded(); lock (_lock) return _byLabel?.Count ?? 0; }
    }

    private void EnsureLoaded() {
        lock (_lock) {
            if (_byBinary != null && DateTime.UtcNow - _loadedUtc < Ttl) return;
        }
        var byBinary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byLabel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in SearchDirs) {
            try {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.xml")) {
                    string text;
                    try { text = File.ReadAllText(file); } catch { continue; }
                    Parse(text, byBinary, byLabel);
                }
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Could not read the INDI driver XML in {Dir}", dir);
            }
        }
        lock (_lock) {
            _byBinary = byBinary;
            _byLabel = byLabel;
            _loadedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Pull the device entries out of one INDI driver XML. Separated from the
    /// file walk so it can be tested against the real shapes: the core
    /// drivers.xml, a third-party file, an entry with no manufacturer, and the
    /// skeleton files that carry no devices at all.
    ///
    /// Tolerant on purpose. These files are installed by a dozen different
    /// packages and one of them being malformed must not cost the others.
    /// </summary>
    internal static void Parse(string xml,
                               Dictionary<string, string> byBinary,
                               Dictionary<string, string> byLabel) {
        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch { return; }

        foreach (var device in doc.Descendants("device")) {
            var manufacturer = (string?)device.Attribute("manufacturer");
            if (string.IsNullOrWhiteSpace(manufacturer)) continue;
            manufacturer = manufacturer.Trim();

            var label = ((string?)device.Attribute("label"))?.Trim();
            if (!string.IsNullOrWhiteSpace(label)) byLabel.TryAdd(label!, manufacturer);

            foreach (var driver in device.Elements("driver")) {
                var binary = driver.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(binary)) byBinary.TryAdd(binary!, manufacturer);
                // The driver's own name is a third thing indi-web may report.
                var name = ((string?)driver.Attribute("name"))?.Trim();
                if (!string.IsNullOrWhiteSpace(name)) byLabel.TryAdd(name!, manufacturer);
            }
        }
    }
}
