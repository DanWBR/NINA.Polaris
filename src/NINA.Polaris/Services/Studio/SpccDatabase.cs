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

namespace NINA.Polaris.Services.Studio;

/// <summary>
/// Loads the SPCC reference data that <see cref="SpccService"/> integrates
/// against: the bundled generic filter/QE curve database
/// (<c>wwwroot/catalogs/spcc/curves.json</c>, always present) and the
/// optional spectral libraries that improve on the always-available
/// blackbody spectral source:
///   - Pickles stellar template library (<c>pickles.json</c>, produced by
///     <c>scripts/download-pickles.py</c>).
///   - Gaia DR3 sampled-spectra subset (<c>gaia-spcc.db</c>, produced by
///     <c>scripts/download-gaia-spcc.py</c>) — availability only for now;
///     per-star retrieval is a planned upgrade.
///
/// The curve database is intentionally generic + editable so any offline
/// install has a working SPCC out of the box; users drop their measured
/// filter/QE curves into the same JSON. Channel total response is built
/// per selection: OSC sensors carry the Bayer CFA in their r/g/b curves
/// (× an optional broadband filter); mono sensors combine one QE curve
/// with an R/G/B filter set.
/// </summary>
public class SpccDatabase {
    private readonly ILogger<SpccDatabase> _logger;
    private readonly string _spccDir;
    private readonly string _curvesPath;
    private readonly string _sirilPath;
    private readonly string _picklesPath;
    private readonly string _gaiaPath;

    private JsonDocument? _curves;
    private JsonDocument? _siril;
    private bool _sirilLoaded;
    private PicklesLibrary? _pickles;
    private bool _picklesLoaded;

    /// <summary>Standard integration grid: 380–720 nm at 5 nm. Broadband
    /// colour work doesn't need finer; narrowband is handled by the curve
    /// shapes, not the grid.</summary>
    public static readonly double[] Grid = BuildGrid(380, 720, 5);

    public SpccDatabase(IWebHostEnvironment env, ILogger<SpccDatabase> logger) {
        _logger = logger;
        var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        _spccDir = Path.Combine(webRoot, "catalogs", "spcc");
        _curvesPath = Path.Combine(_spccDir, "curves.json");
        _sirilPath = Path.Combine(_spccDir, "curves-siril.json");
        _picklesPath = Path.Combine(_spccDir, "pickles.json");
        _gaiaPath = Path.Combine(_spccDir, "gaia-spcc.db");
    }

    public bool CurvesAvailable => File.Exists(_curvesPath);
    public bool SirilAvailable => File.Exists(_sirilPath);
    public bool PicklesAvailable => File.Exists(_picklesPath);
    public bool GaiaAvailable => File.Exists(_gaiaPath);
    public string CurvesPath => _curvesPath;

    /// <summary>Best spectral source that is actually installed:
    /// gaia &gt; pickles &gt; blackbody. Blackbody is always available.</summary>
    public string BestSource => GaiaAvailable ? "gaia" : PicklesAvailable ? "pickles" : "blackbody";

    private JsonDocument Curves() {
        if (_curves != null) return _curves;
        if (!CurvesAvailable)
            throw new InvalidOperationException(
                $"SPCC curve database missing: {_curvesPath}");
        _curves = JsonDocument.Parse(File.ReadAllText(_curvesPath));
        return _curves;
    }

    /// <summary>The optional imported Siril SPCC curve database
    /// (<c>curves-siril.json</c>, GPLv3, produced by
    /// <c>scripts/download-siril-spcc.py</c>). Merged with the generic
    /// <c>curves.json</c> so its sensors/filters/white-refs appear in the same
    /// dropdowns. Missing or malformed = silently ignored.</summary>
    private JsonDocument? Siril() {
        if (_sirilLoaded) return _siril;
        _sirilLoaded = true;
        if (!SirilAvailable) return null;
        try {
            _siril = JsonDocument.Parse(File.ReadAllText(_sirilPath));
        } catch (Exception ex) {
            _logger.LogWarning(ex, "SPCC: failed to load Siril curve database {Path}", _sirilPath);
            _siril = null;
        }
        return _siril;
    }

    /// <summary>Enumerate the named array (sensors / filterSets / whiteRefs)
    /// across every loaded curve database (generic first, then Siril).</summary>
    private IEnumerable<JsonElement> EnumerateAll(string array) {
        foreach (var e in Curves().RootElement.GetProperty(array).EnumerateArray())
            yield return e;
        var siril = Siril();
        if (siril != null && siril.RootElement.TryGetProperty(array, out var arr))
            foreach (var e in arr.EnumerateArray())
                yield return e;
    }

    // ── UI options ───────────────────────────────────────────────────────

    public record CurveOption(string Id, string Name, string Kind);

    /// <summary>Lists for the SPCC modal's dropdowns plus which spectral
    /// sources are installed.</summary>
    public object Options() {
        var sensors = EnumerateAll("sensors")
            .Select(s => new CurveOption(
                s.GetProperty("id").GetString()!,
                s.GetProperty("name").GetString()!,
                s.GetProperty("type").GetString()!)).ToList();
        var filters = EnumerateAll("filterSets")
            .Select(f => new CurveOption(
                f.GetProperty("id").GetString()!,
                f.GetProperty("name").GetString()!,
                f.TryGetProperty("for", out var fr) ? fr.GetString()! : "any")).ToList();
        var whiteRefs = EnumerateAll("whiteRefs")
            .Select(w => new CurveOption(
                w.GetProperty("id").GetString()!,
                w.GetProperty("name").GetString()!,
                w.GetProperty("kind").GetString()!)).ToList();
        return new {
            curvesAvailable = CurvesAvailable,
            sirilCurves = SirilAvailable,
            sensors,
            filterSets = filters,
            whiteRefs,
            sources = new {
                blackbody = true,          // always
                pickles = PicklesAvailable,
                gaia = GaiaAvailable,
                best = BestSource,
            },
        };
    }

    // ── Auto-select from FITS header ─────────────────────────────────────

    /// <param name="Type">"osc" or "mono", from the Bayer header.</param>
    /// <param name="SensorId">Best-matching sensor id, or null if no
    /// confident match.</param>
    /// <param name="Reason">Human-readable note on how the match was made.</param>
    public record SpccSuggestion(
        string Type, string? Camera, string? Bayer, string? Filter,
        string? SensorId, string? SensorName,
        string? FilterSetId, string? FilterSetName, string Reason);

    /// <summary>
    /// Best-effort mapping from a FITS <c>INSTRUME</c> camera model to the
    /// sensor chip our curve database is keyed by, for the common astro
    /// cameras (which the Siril DB lists by chip, e.g. "Sony IMX571", not by
    /// camera). Keys are matched as substrings of the normalised camera name.
    /// Only well-established camera↔chip identities are included; anything not
    /// here falls back to direct token matching (which already covers DSLRs
    /// named by model). This is a suggestion the user can always override.
    /// </summary>
    private static readonly (string CamKey, string Chip)[] CameraChipAliases = {
        ("asi2600", "imx571"), ("qhy268", "imx571"), ("poseidon", "imx571"),
        ("asi6200", "imx455"), ("qhy600", "imx455"),
        ("asi2400", "imx410"), ("qhy410", "imx410"),
        ("asi294", "imx294"), ("qhy294", "imx294"), ("sv405", "imx294"),
        ("asi533", "imx533"), ("qhy533", "imx533"), ("sv605", "imx533"), ("uranus", "imx533"),
        ("asi183", "imx183"), ("qhy183", "imx183"),
        ("asi178", "imx178"), ("qhy178", "imx178"),
        ("asi385", "imx385"), ("asi224", "imx224"), ("asi462", "imx462"),
        ("asi482", "imx482"), ("asi585", "imx585"), ("sv705", "imx585"),
        ("asi676", "imx676"), ("asi678", "imx678"), ("asi715", "imx715"),
        ("asi269", "imx269"), ("asi477", "imx477"),
        ("seestar s50", "seestar s50"), ("seestar s30", "seestar s30"),
        ("asi1600", "asi1600"),
    };

    private static string NormAlnum(string? s) {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>
    /// Suggest the sensor + filter set + white-reference type for a frame,
    /// from its camera model and Bayer header. OSC vs mono comes from the
    /// Bayer pattern (present + not "NONE" ⇒ OSC). The sensor is matched by an
    /// alias table (camera→chip) first, then by shared alphanumeric tokens
    /// against the merged sensor list (generic + Siril), gated to the frame's
    /// type. Returns nulls for anything it can't match confidently.
    /// </summary>
    public SpccSuggestion Suggest(string? camera, string? bayer, string? filter) {
        var bayerNorm = (bayer ?? "").Trim().ToUpperInvariant();
        var isOsc = bayerNorm.Length > 0 && bayerNorm != "NONE" && bayerNorm != "MONO";
        var type = isOsc ? "osc" : "mono";

        var (sensorId, sensorName, reason) = MatchSensor(camera, type);

        // Sensible default filter set for the type; the user refines it.
        string? fsId = null, fsName = null;
        var wantFor = isOsc ? "osc" : "mono";
        // OSC: prefer "no filter"; mono: prefer a generic RGB set.
        foreach (var f in EnumerateAll("filterSets")) {
            var id = f.GetProperty("id").GetString();
            var forVal = f.TryGetProperty("for", out var fr) ? fr.GetString() : "any";
            var isNone = id == "none";
            if (isOsc && isNone) { fsId = id; fsName = f.GetProperty("name").GetString(); break; }
            if (!isOsc && forVal == "mono" && fsId == null) {
                fsId = id; fsName = f.GetProperty("name").GetString();
                if (id == "rgb-generic") break;   // preferred mono default
            }
        }

        return new SpccSuggestion(type, camera, bayer, filter,
            sensorId, sensorName, fsId, fsName, reason);
    }

    private (string? id, string? name, string reason) MatchSensor(string? camera, string type) {
        var cam = NormAlnum(camera);
        if (cam.Length < 3) return (null, null, "no camera model in header");

        // Alias table: map the camera to a sensor-chip token if we know it.
        string? chip = null;
        foreach (var (camKey, c) in CameraChipAliases)
            if (cam.Contains(NormAlnum(camKey))) { chip = NormAlnum(c); break; }

        var camTokens = (camera ?? "")
            .Split(new[] { ' ', '-', '_', '/', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormAlnum).Where(t => t.Length >= 3).ToArray();

        string? bestId = null, bestName = null; int bestScore = 0, bestLenDiff = int.MaxValue;
        foreach (var s in EnumerateAll("sensors")) {
            if (s.GetProperty("type").GetString() != type) continue;
            var name = s.GetProperty("name").GetString() ?? "";
            var sn = NormAlnum(name.Replace("(Siril)", ""));
            int score = 0;
            if (chip != null && sn.Contains(chip)) score += 100 + chip.Length;
            foreach (var t in camTokens) if (sn.Contains(t)) score += t.Length;
            if (score == 0) continue;
            var lenDiff = Math.Abs(sn.Length - cam.Length);
            if (score > bestScore || (score == bestScore && lenDiff < bestLenDiff)) {
                bestScore = score; bestLenDiff = lenDiff;
                bestId = s.GetProperty("id").GetString(); bestName = name;
            }
        }

        if (bestId == null) return (null, null, "no sensor matched the camera model");
        var how = chip != null && bestScore >= 100 ? $"matched chip {chip.ToUpperInvariant()}" : "matched camera model";
        return (bestId, bestName, how);
    }

    // ── Channel responses (filter × QE) ──────────────────────────────────

    /// <summary>Build the three channel total-response curves for a
    /// sensor + filter-set selection. OSC: sensor r/g/b × filter.all.
    /// Mono: sensor.qe × filter.{r,g,b}.</summary>
    public (SpccMath.ResponseCurve R, SpccMath.ResponseCurve G, SpccMath.ResponseCurve B)
            BuildResponses(string sensorId, string filterSetId) {
        var sensor = FindById("sensors", sensorId)
            ?? throw new ArgumentException($"Unknown SPCC sensor '{sensorId}'.");
        var filter = FindById("filterSets", filterSetId)
            ?? throw new ArgumentException($"Unknown SPCC filter set '{filterSetId}'.");
        var type = sensor.GetProperty("type").GetString();

        if (type == "osc") {
            var all = ParseCurve(filter.GetProperty("all"));
            var r = SpccMath.CombineResponse(ParseCurve(sensor.GetProperty("r")), all);
            var g = SpccMath.CombineResponse(ParseCurve(sensor.GetProperty("g")), all);
            var b = SpccMath.CombineResponse(ParseCurve(sensor.GetProperty("b")), all);
            return (r, g, b);
        }
        // mono: one QE curve × the R/G/B filters.
        var qe = ParseCurve(sensor.GetProperty("qe"));
        if (!filter.TryGetProperty("r", out _))
            throw new ArgumentException(
                $"Filter set '{filterSetId}' has no R/G/B curves; a mono sensor needs an RGB filter set.");
        var fr = SpccMath.CombineResponse(ParseCurve(filter.GetProperty("r")), qe);
        var fg = SpccMath.CombineResponse(ParseCurve(filter.GetProperty("g")), qe);
        var fb = SpccMath.CombineResponse(ParseCurve(filter.GetProperty("b")), qe);
        return (fr, fg, fb);
    }

    /// <summary>The white-reference spectrum on the standard grid.</summary>
    public SpccMath.Spectrum BuildWhiteRef(string whiteRefId) {
        var w = FindById("whiteRefs", whiteRefId)
            ?? throw new ArgumentException($"Unknown SPCC white reference '{whiteRefId}'.");
        var kind = w.GetProperty("kind").GetString();
        return kind switch {
            "blackbody" => SpccMath.BlackbodySpectrum(w.GetProperty("tempK").GetDouble(), Grid),
            "flat" => new SpccMath.Spectrum((double[])Grid.Clone(), Grid.Select(_ => 1.0).ToArray()),
            "spectrum" => ParseSpectrum(w.GetProperty("spectrum")),
            _ => throw new ArgumentException($"Unknown white-reference kind '{kind}'."),
        };
    }

    // ── Per-star spectra ─────────────────────────────────────────────────

    /// <summary>
    /// Build a star's spectrum from the requested source, on the standard
    /// grid. Falls back gracefully: an uninstalled/failed source degrades
    /// to Pickles (if present) then blackbody, so SPCC always produces a
    /// spectrum. Gaia per-star retrieval is not wired yet, so "gaia"
    /// currently degrades too.
    /// </summary>
    public SpccMath.Spectrum StarSpectrumFromBv(string source, double bv) {
        if ((source == "pickles" || source == "gaia") && PicklesAvailable) {
            try {
                var p = LoadPickles()?.SpectrumForBv(bv, Grid);
                if (p != null) return p;
            } catch (Exception ex) {
                _logger.LogDebug(ex, "SPCC: Pickles lookup failed for B-V {Bv}", bv);
            }
        }
        return SpccMath.BlackbodyFromBv(bv, Grid);
    }

    private PicklesLibrary? LoadPickles() {
        if (_picklesLoaded) return _pickles;
        _picklesLoaded = true;
        if (!PicklesAvailable) return null;
        try {
            _pickles = PicklesLibrary.Load(_picklesPath);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "SPCC: failed to load Pickles library {Path}", _picklesPath);
            _pickles = null;
        }
        return _pickles;
    }

    // ── JSON helpers ─────────────────────────────────────────────────────

    private JsonElement? FindById(string array, string id) {
        foreach (var e in EnumerateAll(array))
            if (e.GetProperty("id").GetString() == id) return e;
        return null;
    }

    private static SpccMath.ResponseCurve ParseCurve(JsonElement c) {
        var wl = c.GetProperty("wl").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var v = c.GetProperty("v").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        return new SpccMath.ResponseCurve(wl, v);
    }

    private static SpccMath.Spectrum ParseSpectrum(JsonElement c) {
        var wl = c.GetProperty("wl").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var v = c.GetProperty("v").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        return new SpccMath.Spectrum(wl, v);
    }

    private static double[] BuildGrid(double lo, double hi, double step) {
        var xs = new List<double>();
        for (double x = lo; x <= hi + 1e-9; x += step) xs.Add(x);
        return xs.ToArray();
    }
}
