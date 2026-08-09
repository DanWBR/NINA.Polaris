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
/// Spots a guider that has fallen into runaway oscillation and is not coming
/// back on its own.
///
/// <para>Field report, SV503 in wind, 2026-08-07: guiding "swung up and down
/// and never returned to stability", and the only fix was stopping and
/// restarting by hand. That is the classic wind failure: a gust pushes the
/// scope, the guider chases it, the correction lands late and overshoots, and
/// the two feed each other. Left alone it does not decay.</para>
///
/// <para>The hard part is telling that apart from ordinary bad seeing, which
/// also produces a big RMS and would make a detector that only watches
/// amplitude restart guiding all night for no reason. The discriminator is the
/// SIGN pattern, not the size:</para>
/// <list type="bullet">
///   <item>runaway overshoot reverses almost every frame, because each
///         correction overshoots the last one: alternation near 1.0</item>
///   <item>seeing is uncorrelated, so it reverses about half the time:
///         alternation near 0.5</item>
///   <item>drift (bad polar alignment, flexure) barely reverses at all:
///         alternation near 0</item>
/// </list>
///
/// <para>So: a run of frames that are both LARGE and ALTERNATING. Either alone
/// is normal. Together they are a guider fighting itself.</para>
/// </summary>
public static class GuideOscillationDetector {

    /// <summary>Verdict for one axis.</summary>
    /// <param name="Oscillating">Both tests passed over the whole window.</param>
    /// <param name="RmsArcsec">RMS error over the window.</param>
    /// <param name="AlternationRate">Fraction of consecutive sample pairs that
    /// changed sign, 0..1.</param>
    public record Verdict(bool Oscillating, double RmsArcsec, double AlternationRate);

    /// <summary>Judge one axis.</summary>
    /// <param name="errors">Recent per-frame errors in arcsec, oldest first.</param>
    /// <param name="minSamples">Frames required before judging at all. Too few
    /// and a single gust reads as runaway; the point is to catch the state that
    /// does NOT recover, which takes a few frames to establish.</param>
    /// <param name="rmsThresholdArcsec">Amplitude the window must exceed.</param>
    /// <param name="alternationThreshold">Fraction of sign reversals required.
    /// Uncorrelated noise sits at 0.5, so anything meaningfully above that is
    /// structure rather than seeing.</param>
    public static Verdict Judge(IReadOnlyList<double> errors,
                                int minSamples = 8,
                                double rmsThresholdArcsec = 2.0,
                                double alternationThreshold = 0.75) {
        if (errors == null || errors.Count < Math.Max(2, minSamples))
            return new Verdict(false, 0, 0);

        double sumSq = 0;
        foreach (var e in errors) sumSq += e * e;
        var rms = Math.Sqrt(sumSq / errors.Count);

        // Count sign reversals. A sample at exactly zero belongs to neither
        // side, so it breaks the run rather than counting as a reversal: a
        // guider parked at zero error is the opposite of oscillating.
        int reversals = 0, pairs = 0;
        for (int i = 1; i < errors.Count; i++) {
            var a = errors[i - 1];
            var b = errors[i];
            if (a == 0 || b == 0) continue;
            pairs++;
            if ((a > 0) != (b > 0)) reversals++;
        }
        var alternation = pairs > 0 ? (double)reversals / pairs : 0;

        var oscillating = rms >= rmsThresholdArcsec && alternation >= alternationThreshold;
        return new Verdict(oscillating, rms, alternation);
    }

    /// <summary>Judge both axes and report the worse one. Wind moves the tube,
    /// not one motor, so it can show on either axis; RA usually first because
    /// that is the one already being driven.</summary>
    public static Verdict JudgeWorst(IReadOnlyList<double> raErrors,
                                     IReadOnlyList<double> decErrors,
                                     int minSamples = 8,
                                     double rmsThresholdArcsec = 2.0,
                                     double alternationThreshold = 0.75) {
        var ra = Judge(raErrors, minSamples, rmsThresholdArcsec, alternationThreshold);
        var dec = Judge(decErrors, minSamples, rmsThresholdArcsec, alternationThreshold);
        if (ra.Oscillating && !dec.Oscillating) return ra;
        if (dec.Oscillating && !ra.Oscillating) return dec;
        return ra.RmsArcsec >= dec.RmsArcsec ? ra : dec;
    }
}
