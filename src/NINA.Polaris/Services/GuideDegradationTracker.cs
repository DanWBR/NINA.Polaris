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
/// Says when guiding has been worse than this session's own normal for a
/// while. It only reports; <see cref="GuideRunawayGuard"/> is the thing that
/// acts.
///
/// <para>The split is deliberate. A restart costs settle time, so the guard has
/// to be conservative and absolute, and it turns out to catch only the outright
/// collapse: of 14 sessions the operator ended by hand on the SV503 that night,
/// one had a signature separable from an ordinary rough patch. A warning costs a
/// glance, so it can be relative and sensitive, and it covers the other twelve,
/// where the judgement was "this is worse than it was" rather than "this is
/// broken".</para>
///
/// <para>Being relative is also what makes it portable. The two rigs that night
/// had different guide scales and different normals; a warning keyed to each
/// session's own median says the same thing on both, where any absolute arcsec
/// figure would be tuned to one of them.</para>
///
/// <para><b>The baseline is frozen while degraded.</b> A plain trailing median
/// learns from the bad patch too: the baseline climbs to meet the degradation
/// and the ratio never crosses. Measured on the same logs, a moving baseline
/// warned for 2 minutes across the whole windy night; freezing it gives 81. So
/// history is only taken from frames that already look normal.</para>
///
/// <para>Calibrated on the same paired night as the guard, two rigs under one
/// sky. At 2x held for 120s the wind-hit SV503 is warned for 15% of its guiding
/// time (81 of 539 minutes) and the sheltered FRA400 for 2% (18 of 729). Raising
/// the factor collapses that: at 2.5x it is 3% against 2%, which is no signal at
/// all. So 2.0 is not a round number picked for looks, it is the only setting in
/// the sweep that separates the rig in trouble from the rig beside it.</para>
/// </summary>
public sealed class GuideDegradationTracker {

    /// <summary>Windows of healthy history the baseline is the median of.</summary>
    private const int HistoryWindows = 300;

    /// <summary>History needed before any judgement. Below this there is no
    /// "normal" to be worse than yet.</summary>
    private const int MinHistory = 60;

    private readonly double _factor;
    private readonly TimeSpan _hold;
    private readonly List<double> _history = new();
    private DateTime? _over;

    /// <summary>True once the error has been above the bar for the hold time.</summary>
    public bool Degraded { get; private set; }

    /// <summary>When the current spell started, i.e. when the error first went
    /// over the bar, not when the warning was raised.</summary>
    public DateTime? DegradedSinceUtc { get; private set; }

    /// <summary>This session's normal, arcsec RMS. Null until there is enough
    /// history.</summary>
    public double? BaselineArcsec { get; private set; }

    /// <summary>Most recent short-window RMS.</summary>
    public double CurrentArcsec { get; private set; }

    public GuideDegradationTracker(double factor = 2.0, TimeSpan? hold = null) {
        _factor = factor <= 1 ? 2.0 : factor;
        _hold = hold ?? TimeSpan.FromSeconds(120);
    }

    /// <summary>Feed one short-window RMS. <paramref name="now"/> is a
    /// parameter rather than DateTime.UtcNow so the hold can be tested without
    /// waiting two minutes.</summary>
    public void Push(double rmsArcsec, DateTime now) {
        CurrentArcsec = rmsArcsec;

        var baseline = _history.Count >= MinHistory ? Median(_history) : (double?)null;
        BaselineArcsec = baseline;

        var over = baseline is > 0 && rmsArcsec >= _factor * baseline.Value;

        // Only learn from frames that look normal. Learning from the bad ones
        // is what made the first version of this useless.
        if (!over) {
            _history.Add(rmsArcsec);
            if (_history.Count > HistoryWindows) _history.RemoveAt(0);
        }

        if (baseline == null) { Clear(); return; }

        if (!over) { Clear(); return; }

        _over ??= now;
        if (now - _over.Value >= _hold) {
            Degraded = true;
            DegradedSinceUtc = _over;
        }
    }

    /// <summary>Forget everything: a new session, a rig change, or a restart,
    /// after which the old normal no longer describes anything.</summary>
    public void Reset() {
        _history.Clear();
        BaselineArcsec = null;
        CurrentArcsec = 0;
        Clear();
    }

    private void Clear() {
        _over = null;
        Degraded = false;
        DegradedSinceUtc = null;
    }

    private static double Median(List<double> values) {
        var s = new List<double>(values);
        s.Sort();
        var mid = s.Count / 2;
        return s.Count % 2 == 1 ? s[mid] : (s[mid - 1] + s[mid]) / 2.0;
    }
}
