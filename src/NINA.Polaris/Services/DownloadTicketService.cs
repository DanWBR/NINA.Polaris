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
using System.Security.Cryptography;

namespace NINA.Polaris.Services;

/// <summary>
/// One-shot tickets that turn a POST-shaped download into a plain GET URL.
///
/// A browser download has to be a navigation: a fetch into a blob buffers the
/// whole archive in memory, and the app's WebView ignores the download
/// attribute on a blob anyway, so the phone got nothing at all. A navigation
/// cannot carry a POST body, hence this: the client posts the selection, gets
/// a ticket, and navigates to a URL the OS download manager can take over.
///
/// The ticket is random, single use and short lived, so the URL is no weaker
/// than the token that had to be presented to create it.
/// </summary>
public sealed class DownloadTicketService {
    /// <summary>Long enough for a user to confirm a download dialog, short
    /// enough that a copied URL is useless later.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const int MaxOutstanding = 64;

    private sealed record Entry(object Payload, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _tickets = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _now;

    public DownloadTicketService(Func<DateTimeOffset>? now = null) {
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public int Count {
        get { Sweep(); return _tickets.Count; }
    }

    /// <summary>Park a payload and return the ticket that redeems it.</summary>
    public string Create(object payload) {
        Sweep();
        // A flood of unredeemed tickets (a client that posts and never
        // navigates) must not grow without bound.
        if (_tickets.Count >= MaxOutstanding) {
            var oldest = _tickets.OrderBy(kv => kv.Value.ExpiresAt).FirstOrDefault().Key;
            if (oldest != null) _tickets.TryRemove(oldest, out _);
        }
        string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _tickets[id] = new Entry(payload, _now() + Lifetime);
        return id;
    }

    /// <summary>Redeem a ticket. Single use: a second attempt fails, so a URL
    /// that leaks into a log or a history cannot be replayed.</summary>
    public bool TryTake<T>(string? id, out T? payload) where T : class {
        payload = null;
        if (string.IsNullOrWhiteSpace(id)) return false;
        Sweep();
        if (!_tickets.TryRemove(id, out var e)) return false;
        if (e.ExpiresAt < _now()) return false;
        payload = e.Payload as T;
        return payload != null;
    }

    private void Sweep() {
        var now = _now();
        foreach (var kv in _tickets) {
            if (kv.Value.ExpiresAt < now) _tickets.TryRemove(kv.Key, out _);
        }
    }
}
