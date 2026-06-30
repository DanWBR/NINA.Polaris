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
/// Tracks the single in-flight classical (Richardson-Lucy) deconvolution so the
/// UI can show real progress + an ETA over the WebSocket status tick, instead
/// of an indeterminate spinner. The RL math is server-side and tiled, so it
/// reports fractional completion as tiles finish; the ETA is a simple linear
/// extrapolation from elapsed time over the completed fraction.
/// </summary>
public sealed class DeconProgressService {
    private readonly object _lock = new();
    private long _runId;
    private bool _active;
    private string? _phase;
    private double _fraction;
    private DateTime _startedUtc;

    /// <summary>Mark a new deconvolution run as started.</summary>
    public IDisposable Begin(string phase) {
        long id;
        lock (_lock) {
            id = ++_runId;
            _active = true;
            _phase = phase;
            _fraction = 0;
            _startedUtc = DateTime.UtcNow;
        }
        return new Scope(this, id);
    }

    /// <summary>Update the completed fraction (0..1) and optional phase text.</summary>
    public void Report(double fraction, string? phase = null) {
        lock (_lock) {
            if (!_active) return;
            _fraction = Math.Clamp(fraction, 0, 1);
            if (phase != null) _phase = phase;
        }
    }

    private void End(long id) {
        lock (_lock) { if (_runId == id) _active = false; }
    }

    public DeconProgressSnapshot Snapshot() {
        lock (_lock) {
            double? eta = null;
            if (_active && _fraction > 0.02) {
                double elapsed = (DateTime.UtcNow - _startedUtc).TotalSeconds;
                eta = elapsed * (1 - _fraction) / _fraction;
            }
            return new DeconProgressSnapshot(
                _runId, _active, _phase, _fraction,
                _active ? (DateTime.UtcNow - _startedUtc).TotalSeconds : 0, eta);
        }
    }

    private sealed class Scope : IDisposable {
        private readonly DeconProgressService _svc;
        private readonly long _id;
        private bool _disposed;
        public Scope(DeconProgressService svc, long id) { _svc = svc; _id = id; }
        public void Dispose() { if (_disposed) return; _disposed = true; _svc.End(_id); }
    }
}

/// <summary>Point-in-time view of <see cref="DeconProgressService"/>.</summary>
public sealed record DeconProgressSnapshot(
    long RunId, bool Active, string? Phase, double Fraction,
    double ElapsedSeconds, double? EtaSeconds);
