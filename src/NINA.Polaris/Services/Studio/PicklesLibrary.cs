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
/// The Pickles (1998) stellar spectral flux library as consumed by SPCC:
/// a set of empirical spectral templates tagged with a Johnson B-V colour.
/// SPCC picks the template nearest a matched star's catalog B-V and
/// resamples it onto the working grid, so it integrates a REAL stellar
/// spectrum (with its absorption lines) rather than a smooth blackbody.
///
/// The bundled <c>pickles.json</c> is produced by
/// <c>scripts/download-pickles.py</c>, which fetches the library (Pickles
/// 1998, PASP 110, 863; VizieR J/PASP/110/863) and converts it to:
///   { "grid": [nm...], "templates": [ { "name":"g2v", "bv":0.66,
///     "flux":[...] }, ... ] }
/// with every template pre-sampled onto the shared <c>grid</c>.
/// </summary>
public sealed class PicklesLibrary {
    public sealed record Template(string Name, double Bv, double[] Flux);

    private readonly double[] _grid;
    private readonly List<Template> _templates;

    private PicklesLibrary(double[] grid, List<Template> templates) {
        _grid = grid;
        _templates = templates;
    }

    public int Count => _templates.Count;

    public static PicklesLibrary Load(string path) {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var grid = root.GetProperty("grid").EnumerateArray()
            .Select(x => x.GetDouble()).ToArray();
        var templates = new List<Template>();
        foreach (var t in root.GetProperty("templates").EnumerateArray()) {
            templates.Add(new Template(
                t.GetProperty("name").GetString() ?? "",
                t.GetProperty("bv").GetDouble(),
                t.GetProperty("flux").EnumerateArray().Select(x => x.GetDouble()).ToArray()));
        }
        if (grid.Length == 0 || templates.Count == 0)
            throw new InvalidOperationException("Pickles library is empty.");
        return new PicklesLibrary(grid, templates);
    }

    /// <summary>Spectrum for a star of the given B-V: the nearest-colour
    /// template, resampled onto <paramref name="targetGrid"/>.</summary>
    public SpccMath.Spectrum? SpectrumForBv(double bv, double[] targetGrid) {
        Template? best = null;
        double bestDist = double.PositiveInfinity;
        foreach (var t in _templates) {
            double d = Math.Abs(t.Bv - bv);
            if (d < bestDist) { bestDist = d; best = t; }
        }
        if (best == null) return null;
        var flux = new double[targetGrid.Length];
        for (int i = 0; i < targetGrid.Length; i++)
            flux[i] = SpccMath.Interp(_grid, best.Flux, targetGrid[i]);
        return new SpccMath.Spectrum((double[])targetGrid.Clone(), flux);
    }
}
