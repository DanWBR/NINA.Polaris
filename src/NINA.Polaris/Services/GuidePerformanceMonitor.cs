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
/// Watches a native guiding session for the ways it goes bad without saying
/// so, and tells the guider when to move to another star.
///
/// <para>Field report (2026-11-08): guiding sat "active in the loop" reporting
/// neither an error nor a correction, and a very large star showed big errors
/// while nothing moved. Neither raised an alert: the loop kept running, the
/// badge kept reading Guiding, and the stack quietly filled with trailed
/// subs.</para>
///
/// Three verdicts:
/// <list type="bullet">
/// <item><b>Stalled:</b> no correction applied for a while (every frame lost,
/// or dropped by the mass/distance gates). The existing re-acquisition only
/// re-locks onto the star nearest the old one, which is no help when that star
/// is the problem.</item>
/// <item><b>Frozen:</b> the measured offset is exactly zero, or exactly the
/// previous one, frame after frame. No real star does that through seeing; it
/// is a hot pixel or the camera handing back the same frame.</item>
/// <item><b>Big star:</b> the star was flagged large at selection (much wider
/// than the field's median) and the error over the last frames is high. A
/// bloated or clipped core centroids badly and reports motion that is not
/// there, and every correction for it is noise sent to the mount.</item>
/// </list>
///
/// Pure bookkeeping, no camera or mount: the guider feeds it each frame and
/// asks <see cref="Evaluate"/>. Re-selections are rate limited so a sky with
/// nothing better on offer does not make the guider hop star to star.
/// </summary>
internal sealed class GuidePerformanceMonitor {
    internal enum Verdict { None, Stalled, Frozen, BigStar }

    internal readonly record struct Decision(Verdict Verdict, string Message) {
        public static readonly Decision None = new(Verdict.None, "");
    }

    /// <summary>Stall window: at least this long without a correction...</summary>
    internal const double StallMinSeconds = 60;
    /// <summary>...and at least this many exposures, for long guide exposures.</summary>
    internal const int StallExposures = 15;
    /// <summary>Consecutive frames with a frozen offset before acting.</summary>
    internal const int FrozenFrames = 12;
    /// <summary>An offset below this (px) on both axes counts as zero.</summary>
    internal const double FrozenPx = 0.005;
    /// <summary>Applied frames the big-star check averages over.</summary>
    internal const int BigStarWindow = 20;
    /// <summary>RMS (guide px) that, on a star flagged big, means it is
    /// reporting motion rather than measuring it.</summary>
    internal const double BigStarRmsPx = 1.0;
    /// <summary>Re-selections are at least this far apart...</summary>
    internal const long MinReselectGapMs = 120_000;
    /// <summary>...and at most this many inside <see cref="ReselectWindowMs"/>.</summary>
    internal const int MaxReselectsPerWindow = 4;
    internal const long ReselectWindowMs = 30 * 60_000;

    private long _armedMs;
    private long _lastAppliedMs;
    private int _frozenRun;
    private (double dx, double dy)? _prev;
    private readonly Queue<double> _recentSq = new();
    private bool _starIsBig;
    private readonly List<long> _reselects = new();
    private long? _lastLimitAlertMs;

    /// <summary>A new star (or a new session): forget everything measured on
    /// the old one. <paramref name="starIsBig"/> comes from the selection.</summary>
    public void Reset(long nowMs, bool starIsBig = false) {
        _armedMs = nowMs;
        _lastAppliedMs = nowMs;
        _frozenRun = 0;
        _prev = null;
        _recentSq.Clear();
        _starIsBig = starIsBig;
    }

    /// <summary>Restart the clocks without forgetting the star: after a pause
    /// or a settle, which are not stalls.</summary>
    public void Rearm(long nowMs) {
        _armedMs = nowMs;
        _frozenRun = 0;
        _prev = null;
    }

    public bool StarIsBig => _starIsBig;

    /// <summary>A frame whose offset was measured and sent to the mount.</summary>
    public void NoteApplied(long nowMs, double dx, double dy) {
        _lastAppliedMs = nowMs;
        bool zero = Math.Abs(dx) < FrozenPx && Math.Abs(dy) < FrozenPx;
        bool same = _prev is { } p && p.dx == dx && p.dy == dy;
        _frozenRun = zero || same ? _frozenRun + 1 : 0;
        _prev = (dx, dy);
        _recentSq.Enqueue(dx * dx + dy * dy);
        while (_recentSq.Count > BigStarWindow) _recentSq.Dequeue();
    }

    /// <summary>A frame that produced no correction: star lost, or dropped by
    /// a gate.</summary>
    public void NoteNotApplied(long nowMs) {
        _prev = null;
    }

    /// <summary>The stall window for a given guide exposure.</summary>
    internal static long StallWindowMs(int exposureMs)
        => (long)Math.Max(StallMinSeconds * 1000, (long)StallExposures * Math.Max(0, exposureMs));

    internal double RecentRmsPx => _recentSq.Count == 0 ? 0 : Math.Sqrt(_recentSq.Average());

    public Decision Evaluate(long nowMs, int exposureMs) {
        long since = nowMs - Math.Max(_lastAppliedMs, _armedMs);
        if (since >= StallWindowMs(exposureMs))
            return new Decision(Verdict.Stalled,
                $"Guiding stalled: no correction for {since / 1000} s.");
        if (_frozenRun >= FrozenFrames)
            return new Decision(Verdict.Frozen,
                $"Guide offset frozen for {_frozenRun} frames (a hot pixel or a repeated frame, not a star).");
        if (_starIsBig && _recentSq.Count >= BigStarWindow && RecentRmsPx >= BigStarRmsPx)
            return new Decision(Verdict.BigStar,
                $"Guide star is very large and reports {RecentRmsPx:F1} px of error that is mostly its own noise.");
        return Decision.None;
    }

    /// <summary>May the guider re-select now? Records the attempt when it may.</summary>
    public bool TryConsumeReselect(long nowMs) {
        _reselects.RemoveAll(t => nowMs - t > ReselectWindowMs);
        if (_reselects.Count > 0 && nowMs - _reselects[^1] < MinReselectGapMs) return false;
        if (_reselects.Count >= MaxReselectsPerWindow) return false;
        _reselects.Add(nowMs);
        return true;
    }

    /// <summary>When re-selection is rate limited, say so once per window
    /// instead of on every frame.</summary>
    public bool ShouldAlertLimit(long nowMs) {
        if (_lastLimitAlertMs is { } last && nowMs - last < ReselectWindowMs) return false;
        _lastLimitAlertMs = nowMs;
        return true;
    }
}
