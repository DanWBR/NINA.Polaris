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

namespace NINA.Polaris.WebSocket.Status;

/// <summary>
/// One subsystem's contribution to the once-per-second /ws/status frame.
///
/// Before this existed the whole frame was a single 660-line expression in the
/// WebSocket handler, so every subsystem edited the same method, beside 33
/// blocks it did not care about, to add one field of its own. Now a subsystem
/// owns its blocks and nothing else has to know they exist.
///
/// <see cref="Keys"/> is not documentation. StatusPayloadBuilder checks that a
/// contributor writes exactly what it declared, so two contributors claiming one
/// key fails at startup and a block that quietly stops being written fails on
/// the first tick, rather than surfacing weeks later as a UI panel that has been
/// blank the whole time.
/// </summary>
public interface IStatusContributor {
    /// <summary>Top-level keys of the status frame this contributor owns.</summary>
    IReadOnlyCollection<string> Keys { get; }

    void Contribute(StatusTick tick);
}

/// <summary>State for one tick of the status frame.</summary>
public sealed class StatusTick {
    public Dictionary<string, object?> Blocks { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Cursor into the debug-log ring, shared across every connected client so
    /// one serialization per second serves all of them. Carried in and out
    /// because the log block ships only what is new since the previous tick.
    /// </summary>
    public long DebugCursor { get; set; }
}
