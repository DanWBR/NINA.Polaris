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

using NINA.Polaris.WebSocket.Status;

namespace NINA.Polaris.WebSocket;

/// <summary>
/// Assembles the once-per-second /ws/status frame from the subsystems that own
/// its blocks.
///
/// This was a 660-line expression inside the WebSocket handler, which resolved
/// 42 services out of the request container before it would even accept the
/// socket. Two things were wrong with that and only one was the resolving: the
/// shape meant every subsystem edited the same method, next to 33 blocks it did
/// not care about, to add one field of its own.
///
/// Now each subsystem writes its own blocks and declares which ones it owns, and
/// this class only puts the envelope around them and checks that nobody lied.
/// Adding a field is a change to one file that nothing else reads.
/// </summary>
public sealed class StatusPayloadBuilder {
    private readonly IReadOnlyList<IStatusContributor> _contributors;

    public StatusPayloadBuilder(IEnumerable<IStatusContributor> contributors) {
        _contributors = contributors.ToList();

        // Two contributors claiming one key means one silently overwrites the
        // other, and which one wins depends on registration order. Fail at
        // startup instead, where it is a stack trace and not a support ticket.
        var clash = _contributors
            .SelectMany(c => c.Keys.Select(k => (Key: k, Owner: c.GetType().Name)))
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (clash != null) {
            throw new InvalidOperationException(
                $"Status block '{clash.Key}' is claimed by more than one contributor: "
                + string.Join(", ", clash.Select(x => x.Owner)) + ".");
        }
    }

    /// <summary>
    /// The status object for this tick. <paramref name="debugCursor"/> carries the
    /// shared debug-log ring cursor in and the advanced value out.
    /// </summary>
    public object Build(ref long debugCursor) {
        var tick = new StatusTick { DebugCursor = debugCursor };

        tick.Blocks["type"] = "status";
        tick.Blocks["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var contributor in _contributors) {
            contributor.Contribute(tick);

            // A contributor that stops writing a block it still declares is the
            // failure this whole shape exists to prevent: the socket stays up,
            // the frame stays valid, and one UI panel is blank until somebody
            // reports it weeks later.
            foreach (var key in contributor.Keys) {
                if (!tick.Blocks.ContainsKey(key)) {
                    throw new InvalidOperationException(
                        $"{contributor.GetType().Name} declares status block '{key}' but did not write it.");
                }
            }
        }

        debugCursor = tick.DebugCursor;
        return tick.Blocks;
    }
}
