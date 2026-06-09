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

namespace NINA.Polaris.Services.PlateSolving;

/// <summary>
/// Holds the live console output of the currently-running plate solve so the
/// STUDIO/FILES UI can stream it in real time over <c>/ws/status</c>, the
/// same way the GraXpert local run shows its process output live.
///
/// One solve at a time is the norm in Polaris (the UI disables the button
/// while solving), so a single shared buffer is enough. Each <see cref="Begin"/>
/// bumps <see cref="RunId"/> so the client can tell one solve apart from the
/// next and reset its panel. The line buffer is a bounded rolling tail
/// (solve-field can emit thousands of "did not solve (index ...)" lines), so
/// the payload stays small while still showing the live scroll.
/// </summary>
public sealed class PlateSolveProgressService {
    private const int MaxLines = 600;

    private readonly object _lock = new();
    private readonly List<string> _lines = new();
    private long _runId;
    private bool _active;
    private string? _source;
    private long _seq;       // total lines appended this run (monotonic)
    private bool _truncated; // dropped lines off the head

    /// <summary>Start a new solve run. Returns its run id.</summary>
    public long Begin(string source) {
        lock (_lock) {
            _runId++;
            _active = true;
            _source = source;
            _seq = 0;
            _truncated = false;
            _lines.Clear();
            return _runId;
        }
    }

    /// <summary>Append one console line to the active run (best-effort, thread-safe).</summary>
    public void Append(string? line) {
        if (line == null) return;
        lock (_lock) {
            if (!_active) return;
            _lines.Add(line);
            _seq++;
            if (_lines.Count > MaxLines) {
                _lines.RemoveRange(0, _lines.Count - MaxLines);
                _truncated = true;
            }
        }
    }

    /// <summary>Mark the run finished. The last lines stay visible until the
    /// next Begin so the UI can show the final output next to the result.</summary>
    public void End() {
        lock (_lock) { _active = false; }
    }

    /// <summary>Immutable snapshot for the status payload.</summary>
    public PlateSolveProgressSnapshot Snapshot() {
        lock (_lock) {
            return new PlateSolveProgressSnapshot(
                _runId, _active, _source, _seq, _truncated, _lines.ToArray());
        }
    }
}

/// <summary>Point-in-time view of <see cref="PlateSolveProgressService"/>.</summary>
public sealed record PlateSolveProgressSnapshot(
    long RunId, bool Active, string? Source, long Seq, bool Truncated, string[] Lines);
