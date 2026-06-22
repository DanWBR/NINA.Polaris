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
/// Tracks the single in-flight camera exposure (start time + planned
/// duration + which subsystem owns it) so the current-frame countdown can be
/// driven by the SERVER, not a client-side timer. This makes every capture
/// button's "Xs of Ys" survive a disconnect/reconnect: a browser that comes
/// back mid-exposure reads the live progress straight off the next
/// <c>/ws/status</c> tick instead of guessing from a lost local start time.
///
/// Only the main imaging camera exposes one frame at a time across all the
/// capture owners (LIVE/stream loop, AUTORUN, the advanced sequencer, manual
/// snaps), so a single slot is enough. The guider camera runs its own loop
/// and reports <c>exposureMs</c> separately — it is intentionally NOT tracked
/// here. Each <see cref="Begin"/> bumps <see cref="CaptureProgressSnapshot.RunId"/>
/// and returns an <see cref="IDisposable"/>; disposing it ends only that run,
/// so a late End from a superseded capture can't clobber a newer one.
/// </summary>
public sealed class CaptureProgressService {
    private readonly object _lock = new();
    private long _runId;
    private bool _active;
    private string? _source;
    private double _exposureSeconds;
    private DateTime _startedUtc;

    /// <summary>Mark a new exposure as started. <paramref name="source"/> is a
    /// short tag the UI maps to a context ("live", "autorun", "sequencer",
    /// "snap", "stream"). Wrap the actual <c>CaptureAsync</c> in
    /// <c>using</c> so the run always ends, even on exception/cancel.</summary>
    public IDisposable Begin(string source, double exposureSeconds) {
        long id;
        lock (_lock) {
            id = ++_runId;
            _active = true;
            _source = source;
            _exposureSeconds = exposureSeconds;
            _startedUtc = DateTime.UtcNow;
        }
        return new Scope(this, id);
    }

    private void End(long id) {
        lock (_lock) {
            // Only the owner of the current run may clear it; a stale End from
            // a previous, already-superseded capture is a no-op.
            if (_runId == id) _active = false;
        }
    }

    /// <summary>Immutable snapshot for the status payload.</summary>
    public CaptureProgressSnapshot Snapshot() {
        lock (_lock) {
            return new CaptureProgressSnapshot(
                _runId, _active, _source, _exposureSeconds,
                _active ? _startedUtc : (DateTime?)null);
        }
    }

    private sealed class Scope : IDisposable {
        private readonly CaptureProgressService _svc;
        private readonly long _id;
        private bool _disposed;
        public Scope(CaptureProgressService svc, long id) { _svc = svc; _id = id; }
        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            _svc.End(_id);
        }
    }
}

/// <summary>Point-in-time view of <see cref="CaptureProgressService"/>.</summary>
public sealed record CaptureProgressSnapshot(
    long RunId, bool Active, string? Source, double ExposureSeconds, DateTime? StartedUtc);
