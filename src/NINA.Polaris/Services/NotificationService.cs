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
