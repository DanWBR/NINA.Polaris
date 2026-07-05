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
    private readonly string _picklesPath;
    private readonly string _gaiaPath;

    private JsonDocument? _curves;
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
        _picklesPath = Path.Combine(_spccDir, "pickles.json");
        _gaiaPath = Path.Combine(_spccDir, "gaia-spcc.db");
    }

    public bool CurvesAvailable => File.Exists(_curvesPath);
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

    // ── UI options ───────────────────────────────────────────────────────

    public record CurveOption(string Id, string Name, string Kind);

    /// <summary>Lists for the SPCC modal's dropdowns plus which spectral
    /// sources are installed.</summary>
    public object Options() {
        var root = Curves().RootElement;
        var sensors = root.GetProperty("sensors").EnumerateArray()
            .Select(s => new CurveOption(
                s.GetProperty("id").GetString()!,
                s.GetProperty("name").GetString()!,
                s.GetProperty("type").GetString()!)).ToList();
        var filters = root.GetProperty("filterSets").EnumerateArray()
            .Select(f => new CurveOption(
                f.GetProperty("id").GetString()!,
                f.GetProperty("name").GetString()!,
                f.TryGetProperty("for", out var fr) ? fr.GetString()! : "any")).ToList();
        var whiteRefs = root.GetProperty("whiteRefs").EnumerateArray()
            .Select(w => new CurveOption(
                w.GetProperty("id").GetString()!,
                w.GetProperty("name").GetString()!,
                w.GetProperty("kind").GetString()!)).ToList();
        return new {
            curvesAvailable = CurvesAvailable,
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

    // ── Channel responses (filter × QE) ──────────────────────────────────

    /// <summary>Build the three channel total-response curves for a
    /// sensor + filter-set selection. OSC: sensor r/g/b × filter.all.
    /// Mono: sensor.qe × filter.{r,g,b}.</summary>
    public (SpccMath.ResponseCurve R, SpccMath.ResponseCurve G, SpccMath.ResponseCurve B)
            BuildResponses(string sensorId, string filterSetId) {
        var root = Curves().RootElement;
        var sensor = FindById(root, "sensors", sensorId)
            ?? throw new ArgumentException($"Unknown SPCC sensor '{sensorId}'.");
        var filter = FindById(root, "filterSets", filterSetId)
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
        var root = Curves().RootElement;
        var w = FindById(root, "whiteRefs", whiteRefId)
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

    private static JsonElement? FindById(JsonElement root, string array, string id) {
        foreach (var e in root.GetProperty(array).EnumerateArray())
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
