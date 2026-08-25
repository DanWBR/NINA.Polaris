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

namespace NINA.Polaris.Services;

/// <summary>
/// Resolve the electron-domain sensor constants (e-/ADU, read noise, full well)
/// the sub-exposure advisor needs. Preferred source is a per-camera
/// <see cref="SensorAnalysisService"/> PTC run; a small curated fallback table,
/// read from vendor published gain charts, covers common cameras when no run
/// exists (flagged "estimated"). All e-/ADU values are in the 16-bit FITS ADU
/// domain (what the live path measures), so a 12-bit sensor's native e-/ADU is
/// divided by 16 here.
/// </summary>
public static class SensorConstants {
    public sealed record Constants(double ElectronsPerAdu, double ReadNoiseE, double? FullWellE);

    /// <summary>Nearest valid row (by gain) from a Sensor Analysis result.</summary>
    public static SensorAnalysisRow? NearestRow(SensorAnalysisResult sa, int gain) {
        SensorAnalysisRow? best = null;
        int bestD = int.MaxValue;
        foreach (var r in sa.Rows) {
            if (!r.Valid) continue;
            int d = System.Math.Abs(r.Gain - gain);
            if (d < bestD) { bestD = d; best = r; }
        }
        return best;
    }

    // Curated fallback: (camera-name substring, gain, e-/ADU@16-bit, readNoise e-,
    // fullWell e-). Read off the manufacturer's published Gain / Read Noise /
    // Full Well charts. Two anchor points per camera (low gain + a common HCG
    // working gain); we match the nearest anchor within GainTolerance and flag
    // the result "estimated". Extend as more cameras get characterised.
    private const int GainTolerance = 60; // 0.1 dB units → 6 dB → ~2x gain
    private static readonly (string Match, int Gain, double EAdu, double ReadE, double FwE)[] Table = {
        // ZWO ASI585MC/MM Pro: 12-bit ADC (×16 → 16-bit); unity ≈ 195, HCG @ 200.
        ("ASI585",  0,   0.610,  6.60, 40000),
        ("ASI585",  200, 0.0625, 1.05,  4096),
        // ZWO ASI2600MC/MM Pro: 16-bit ADC, 73 ke- full well, HCG @ 100.
        ("ASI2600", 0,   1.114,  3.30, 73000),
        ("ASI2600", 100, 0.250,  1.50, 18000),
        // ZWO ASI183MC/MM Pro: 12-bit ADC (×16 → 16-bit); unity gain ~120,
        // full well 15 ke- (gain 0), no HCG step. Values read off the chart at
        // gain 111 (a common working point): e/ADU 12-bit ~1.05 → 0.066 16-bit.
        ("ASI183",  0,   0.225,  3.00, 15000),
        ("ASI183",  111, 0.066,  2.15,  4100),
    };

    public static bool TryFallback(string? camera, int gain, out Constants constants) {
        constants = null!;
        if (string.IsNullOrEmpty(camera)) return false;
        int bestD = int.MaxValue;
        foreach (var e in Table) {
            if (camera.IndexOf(e.Match, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
            int d = System.Math.Abs(e.Gain - gain);
            if (d < bestD && d <= GainTolerance) {
                bestD = d;
                constants = new Constants(e.EAdu, e.ReadE, e.FwE);
            }
        }
        return constants != null;
    }
}
