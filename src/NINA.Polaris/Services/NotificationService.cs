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

using System.Collections.Concurrent;

namespace NINA.Polaris.Services;

/// <summary>
/// Server-pushed toast notifications. Singleton ring buffer; the
/// last <see cref="MaxKept"/> entries get folded into every
/// /ws/status broadcast under a <c>notifications</c> field. The
/// browser tracks "seen" by monotonically-increasing <see cref="Id"/>
/// so we don't re-fire the same toast on every WS tick.
///
/// This is the user-facing channel for background-service events
/// the user otherwise wouldn't see, auto-connect outcomes, simulator
/// auto-start results, PHD2 reconnect attempts, etc.
/// </summary>
public class NotificationService {
    /// <summary>How many notifications to retain. Older entries roll off
    /// the ring on the next <see cref="Push"/>. Sized for the typical
    /// "user reloads after a hardware swap and wants to see what just
    /// happened on the server" case.</summary>
    public const int MaxKept = 20;

    private long _nextId;
    private readonly ConcurrentQueue<Notification> _queue = new();

    public IReadOnlyList<Notification> Snapshot() => _queue.ToArray();

    /// <summary>Push a notification. <paramref name="kind"/> is one of
    /// <c>info</c>, <c>ok</c>, <c>warn</c>, <c>error</c>, anything the
    /// front-end toast styler recognises. <paramref name="ttlMs"/> is
    /// advisory; the client may dismiss earlier.</summary>
    public Notification Push(string kind, string text, int ttlMs = 4000) {
        var n = new Notification(
            Id: Interlocked.Increment(ref _nextId),
            Kind: kind ?? "info",
            Text: text ?? string.Empty,
            At: DateTime.UtcNow,
            TtlMs: ttlMs);
        _queue.Enqueue(n);
        while (_queue.Count > MaxKept && _queue.TryDequeue(out _)) { }
        return n;
    }
}

public record Notification(long Id, string Kind, string Text, DateTime At, int TtlMs);