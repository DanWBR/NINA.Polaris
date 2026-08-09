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
/// Spots a guider whose error has run away and is not coming back, the state
/// where the only fix is to stop and start again.
///
/// <para>Calibrated against two real data sets: the SV503 wind night of
/// 2026-08-07/08 on an OPi 4 Pro (23087 frames, 14 sessions, most of them ended
/// by the operator restarting by hand) and, as a control, an Askar FRA400 on a
/// Q6A across several calm nights (47898 frames, 34 sessions) where the small
/// scope barely felt the wind.</para>
///
/// <para><b>What the logs actually show, and what they killed.</b> The first
/// version of this looked for oscillation: the classic over-correction runaway
/// where each correction overshoots the last, so the error reverses sign almost
/// every frame. That hypothesis is wrong for this failure. In that night's
/// data, large error and high alternation never co-occur:</para>
/// <code>
///   RMS \ alt      &lt;.5   .5-.65  .65-.75   &gt;=.75
///   &lt;1          28574     1956      264      71
///   1-2          5272      722       85      31
///   2-4          1722      229       25       5
///   4-8          1173       62        4       0
///   &gt;=8          1787        0        0       0     &lt;- not one
/// </code>
/// <para>Every window above 8 arcsec RMS had alternation below 0.5. And the
/// last frames before each manual restart, i.e. the moment the operator judged
/// it unusable, ran from 4.6 to 61.9 arcsec RMS at alternation 0.00 to 0.55.
/// The error was not oscillating, it was LEAVING: a monotonic excursion the
/// guider could not pull back. So the test is size and persistence, not sign
/// pattern.</para>
///
/// <para>Restarting fixes it because a fresh start re-locks on the star where
/// it is now, zeroing an error the loop was never going to close, with
/// per-correction limits in the way. Calibration is not redone: it is not what
/// is wrong, and it costs minutes.</para>
///
/// <para><b>What this does NOT do.</b> Of the 14 sessions the operator ended by
/// hand that night, only one carries a telemetry signature separable from an
/// ordinary rough night; the control set spends time in the same 8-15 arcsec
/// band without anything being broken. The other twelve were ended on
/// judgement, watching a graph, and no threshold over these two data sets
/// reproduces that without also firing on the calm scope. So this catches the
/// collapse, not the operator's taste, and the manual stop/start stays the tool
/// for a session that is merely disappointing.</para>
/// </summary>
public static class GuideRunawayDetector {

    /// <summary>Verdict for one axis.</summary>
    /// <param name="RunAway">Large, sustained, and not recovering.</param>
    /// <param name="RmsArcsec">RMS error over the window.</param>
    /// <param name="TrendArcsecPerFrame">Slope of |error| across the window.
    /// Negative means it is already pulling back.</param>
    /// <param name="AlternationRate">Fraction of consecutive pairs that
    /// reversed sign. Not part of the test: reported because it is what
    /// distinguishes this failure from the over-correction one, and the number
    /// is what would let that second mode be calibrated if it ever shows up in
    /// a log.</param>
    public record Verdict(bool RunAway, double RmsArcsec,
                          double TrendArcsecPerFrame, double AlternationRate);

    /// <summary>Judge one axis.</summary>
    /// <param name="errors">Recent per-frame errors in arcsec, oldest first.</param>
    /// <param name="minSamples">Frames required before judging. A single gust
    /// is not a runaway; what matters is the state that persists.</param>
    /// <param name="rmsThresholdArcsec">RMS the window must exceed.
    ///
    /// <para>The default of 30 is deliberately conservative, and the control
    /// data is why. Sweeping the threshold across both sets, counting sessions
    /// touched:</para>
    /// <code>
    ///   thresh   SV503 windy      FRA400 calm (control)
    ///      8"    4/32 sessions    9/34 sessions   &lt;- no separation at all
    ///     12"    4/32             9/34
    ///     16"    3/32             7/34
    ///     20"    2/32             6/34
    ///     25"    1/32             4/34
    ///     30"    1/32             4/34
    ///     60"    1/32             0/34
    /// </code>
    /// <para>Below about 20 arcsec the calm night fires as often as the windy
    /// one, because a small scope in poor seeing also spends time at 8-11
    /// arcsec RMS. That is bad guiding, not a broken loop, and restarting there
    /// would cost settle time for nothing. At 30 the control fires roughly once
    /// per 12000 frames, about one per three hours of guiding, which the
    /// restart budget absorbs.</para></param>
    public static Verdict Judge(IReadOnlyList<double> errors,
                                int minSamples = 12,
                                double rmsThresholdArcsec = 30.0) {
        if (errors == null || errors.Count < Math.Max(4, minSamples))
            return new Verdict(false, 0, 0, 0);

        double sumSq = 0;
        foreach (var e in errors) sumSq += e * e;
        var rms = Math.Sqrt(sumSq / errors.Count);

        var slope = Trend(errors);
        var alternation = Alternation(errors);

        // Recovering already: restarting here would interrupt a loop that is
        // doing its job, and cost the settle time for nothing. The bar scales
        // with the threshold so it means the same thing at any sensitivity.
        var recovering = slope < -rmsThresholdArcsec / 40.0;

        return new Verdict(rms >= rmsThresholdArcsec && !recovering,
                           rms, slope, alternation);
    }

    /// <summary>Least-squares slope of |error| over the window, arcsec per
    /// frame.</summary>
    private static double Trend(IReadOnlyList<double> errors) {
        var n = errors.Count;
        if (n < 4) return 0;
        double mx = (n - 1) / 2.0;
        double my = 0;
        for (int i = 0; i < n; i++) my += Math.Abs(errors[i]);
        my /= n;
        double num = 0, den = 0;
        for (int i = 0; i < n; i++) {
            var dx = i - mx;
            num += dx * (Math.Abs(errors[i]) - my);
            den += dx * dx;
        }
        return den > 0 ? num / den : 0;
    }

    /// <summary>Fraction of consecutive pairs that changed sign. A sample at
    /// exactly zero belongs to neither side and breaks the run.</summary>
    private static double Alternation(IReadOnlyList<double> errors) {
        int reversals = 0, pairs = 0;
        for (int i = 1; i < errors.Count; i++) {
            var a = errors[i - 1];
            var b = errors[i];
            if (a == 0 || b == 0) continue;
            pairs++;
            if ((a > 0) != (b > 0)) reversals++;
        }
        return pairs > 0 ? (double)reversals / pairs : 0;
    }

    /// <summary>Judge both axes and report the worse. Wind moves the tube, not
    /// one motor, so either axis can be the one that goes.</summary>
    public static Verdict JudgeWorst(IReadOnlyList<double> raErrors,
                                     IReadOnlyList<double> decErrors,
                                     int minSamples = 12,
                                     double rmsThresholdArcsec = 30.0) {
        var ra = Judge(raErrors, minSamples, rmsThresholdArcsec);
        var dec = Judge(decErrors, minSamples, rmsThresholdArcsec);
        if (ra.RunAway && !dec.RunAway) return ra;
        if (dec.RunAway && !ra.RunAway) return dec;
        return ra.RmsArcsec >= dec.RmsArcsec ? ra : dec;
    }
}
