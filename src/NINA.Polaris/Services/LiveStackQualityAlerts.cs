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
/// Read the LIVE quality timeline and flag the two failure modes the SNR/HFR
/// curves make visible, the ones the handbook's "Reading SNR over time" section
/// describes:
/// <list type="bullet">
/// <item><b>Clouds / sky glow</b>: the per-frame SNR falls away while HFR stays
/// put — light is being lost, not focus.</item>
/// <item><b>Focus drift</b>: HFR climbs across recent frames while the stack
/// stops gaining SNR — the stars are bloating.</item>
/// </list>
/// A healthy stack has per-frame SNR roughly steady (cumulative rising ~√N) and
/// HFR flat. Pure functional helper over the quality series — no state, fully
/// testable; the caller throttles + surfaces the alert as a toast.
/// </summary>
public static class LiveStackQualityAlerts {
    public enum Kind { None, Clouds, FocusDrift }

    public sealed record Alert(Kind Kind, string Message);

    /// <summary>Recent frames to judge the trend over.</summary>
    public const int Window = 10;
    /// <summary>Fraction the median per-frame SNR must drop, recent vs the
    /// window's earlier half, to read as clouds (30%).</summary>
    public const double SnrDropFraction = 0.30;
    /// <summary>Fraction HFR must rise, recent vs earlier half, to read as
    /// focus drift (20%).</summary>
    public const double HfrRiseFraction = 0.20;

    /// <summary>
    /// Returns None until there are at least <see cref="Window"/> samples.
    /// Focus drift takes precedence over clouds when both look true (a bloating
    /// star also dims per-frame SNR, but the fix is focus, not waiting it out).
    /// </summary>
    public static Alert Analyze(IReadOnlyList<LiveStackingService.LiveStackQualitySample> series) {
        if (series == null || series.Count < Window) return new Alert(Kind.None, "");

        int n = series.Count;
        int half = Window / 2;
        // Earlier half vs recent half of the trailing window.
        var recent = new List<LiveStackingService.LiveStackQualitySample>(half);
        var earlier = new List<LiveStackingService.LiveStackQualitySample>(Window - half);
        for (int i = n - Window; i < n - half; i++) earlier.Add(series[i]);
        for (int i = n - half; i < n; i++) recent.Add(series[i]);

        double earlierSnr = Median(earlier.ConvertAll(s => s.FrameSnr));
        double recentSnr = Median(recent.ConvertAll(s => s.FrameSnr));
        double earlierHfr = Median(earlier.ConvertAll(s => s.MedianHfr));
        double recentHfr = Median(recent.ConvertAll(s => s.MedianHfr));

        bool hfrRose = earlierHfr > 0 && recentHfr >= earlierHfr * (1.0 + HfrRiseFraction);
        bool snrFell = earlierSnr > 0 && recentSnr <= earlierSnr * (1.0 - SnrDropFraction);

        if (hfrRose) {
            return new Alert(Kind.FocusDrift,
                $"Focus drift: HFR rose {Pct(earlierHfr, recentHfr):F0}% over the last {Window} frames. Consider re-focusing.");
        }
        if (snrFell) {
            return new Alert(Kind.Clouds,
                $"Sky degraded: per-frame SNR fell {Pct(recentSnr, earlierSnr, drop: true):F0}% with HFR steady. Clouds or sky glow.");
        }
        return new Alert(Kind.None, "");
    }

    private static double Pct(double from, double to, bool drop = false) {
        if (from <= 0) return 0;
        return drop ? (from - to) / from * 100.0 : (to - from) / from * 100.0;
    }

    private static double Median(List<double> xs) {
        if (xs == null || xs.Count == 0) return 0;
        xs.Sort();
        int m = xs.Count / 2;
        return xs.Count % 2 == 1 ? xs[m] : (xs[m - 1] + xs[m]) / 2.0;
    }
}
