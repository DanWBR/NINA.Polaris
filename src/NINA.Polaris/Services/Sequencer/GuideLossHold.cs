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

namespace NINA.Polaris.Services.Sequencer;

/// <summary>
/// A hold on a running sequence: the guider lost its star for good (the mount
/// safety guard's breaker tripped) while a PLAN was imaging. A cloud bank or a
/// roof edge is usually temporary, so instead of ending the night the plan
/// parks the current target (tracking off, guider stopped), waits, and tries
/// to get it back: tracking on, Slew &amp; Center, guiding. When the target's
/// time window closes, the next target's window opens, or a target with no
/// window has had its share of attempts, the plan gives the target up and
/// moves on to whatever comes next.
/// </summary>
public sealed class SequenceHoldState {
    private readonly object _lock = new();
    private bool _requested;
    private bool _active;
    private string? _reason;
    private int _attempts;
    private DateTime? _nextRetryUtc;
    private DateTime? _sinceUtc;
    private string? _target;

    /// <summary>Set by whoever detects the condition (the safety guard); the
    /// sequence picks it up at its next frame or step boundary.</summary>
    public bool Requested { get { lock (_lock) return _requested; } }
    /// <summary>True from the moment the sequence honours the request until
    /// it resumes or gives the target up.</summary>
    public bool Active { get { lock (_lock) return _active; } }
    public string? Reason { get { lock (_lock) return _reason; } }
    public int Attempts { get { lock (_lock) return _attempts; } }
    public DateTime? NextRetryUtc { get { lock (_lock) return _nextRetryUtc; } }
    public DateTime? SinceUtc { get { lock (_lock) return _sinceUtc; } }
    public string? Target { get { lock (_lock) return _target; } }

    public void Request(string reason) {
        lock (_lock) {
            if (_active) return;   // already holding; keep the first reason
            _requested = true;
            _reason = reason;
        }
    }

    internal bool TakeRequest(string? target) {
        lock (_lock) {
            if (!_requested) return false;
            _requested = false;
            _active = true;
            _attempts = 0;
            _sinceUtc = DateTime.UtcNow;
            _target = target;
            return true;
        }
    }

    internal void Waiting(int attempts, DateTime nextRetryUtc) {
        lock (_lock) { _attempts = attempts; _nextRetryUtc = nextRetryUtc; }
    }

    internal void Clear() {
        lock (_lock) {
            _requested = false; _active = false; _reason = null;
            _attempts = 0; _nextRetryUtc = null; _sinceUtc = null; _target = null;
        }
    }
}

/// <summary>What the hold loop decided to do before one more attempt.</summary>
public enum HoldDecision { Retry, SkipWindowClosed, SkipNextTargetDue, SkipAttemptsExhausted }

/// <summary>The pure timing rules of the hold, kept apart from the equipment
/// calls so they can be tested on a clock.</summary>
public static class GuideLossHold {
    /// <summary>Wait before attempt 1, 2, 3, ...; the last value repeats.</summary>
    public static readonly TimeSpan[] RetryDelays = {
        TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15)
    };

    /// <summary>Attempts granted to a target that has no time window before the
    /// plan moves on (only when there is something to move on to).</summary>
    public const int MaxAttemptsWithoutWindow = 4;

    public static TimeSpan DelayFor(int attempt) {
        if (attempt < 1) attempt = 1;
        return RetryDelays[Math.Min(attempt, RetryDelays.Length) - 1];
    }

    /// <summary>
    /// Decide whether attempt number <paramref name="attempt"/> (1-based) is
    /// worth making at <paramref name="nowUtc"/>.
    /// <paramref name="windowEndUtc"/> is the target's own end time when it has
    /// one; <paramref name="nextTargetStartUtc"/> the following target's start
    /// time when that one has a window; <paramref name="hasNextTarget"/> whether
    /// anything at all follows this target in the plan.
    /// </summary>
    public static HoldDecision Decide(int attempt, DateTime nowUtc, DateTime? windowEndUtc,
            DateTime? nextTargetStartUtc, bool hasNextTarget) {
        if (windowEndUtc.HasValue && nowUtc >= windowEndUtc.Value) return HoldDecision.SkipWindowClosed;
        if (hasNextTarget && nextTargetStartUtc.HasValue && nowUtc >= nextTargetStartUtc.Value)
            return HoldDecision.SkipNextTargetDue;
        if (!windowEndUtc.HasValue && hasNextTarget && attempt > MaxAttemptsWithoutWindow)
            return HoldDecision.SkipAttemptsExhausted;
        return HoldDecision.Retry;
    }

    /// <summary>
    /// Resolve a "HH:mm" UTC time of day to the occurrence that belongs to
    /// this run: the first one at or after <paramref name="runStartedUtc"/>,
    /// which is how the plan's own wait and loop steps read it. Null for an
    /// empty or malformed value.
    /// </summary>
    public static DateTime? ResolveTimeOfDay(string? hhmm, DateTime runStartedUtc) {
        if (string.IsNullOrWhiteSpace(hhmm) || !TimeSpan.TryParse(hhmm, out var tod)) return null;
        var t = runStartedUtc.Date + tod;
        if (t < runStartedUtc) t = t.AddDays(1);
        return t;
    }

    public static string SkipReason(HoldDecision d, int attempts) => d switch {
        HoldDecision.SkipWindowClosed => "its time window closed while waiting for the guide star",
        HoldDecision.SkipNextTargetDue => "the next target's window opened while waiting for the guide star",
        HoldDecision.SkipAttemptsExhausted => $"the guide star did not come back after {attempts} attempts",
        _ => "hold ended"
    };
}

/// <summary>Thrown out of a step to give up the current target and let the
/// plan continue with the next one. Caught at the target container.</summary>
public sealed class TargetSkippedException : Exception {
    public TargetSkippedException(string message) : base(message) { }
}
