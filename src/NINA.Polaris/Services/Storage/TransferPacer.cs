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

namespace NINA.Polaris.Services.Storage;

/// <summary>
/// Leaves part of the uplink for the live view while a background push runs.
///
/// <para>THE PROBLEM. The network-storage push wrote its chunks in a tight loop
/// with no pacing at all, so a frame going to the NAS took the whole uplink for
/// as long as it took. The operator's browser shares that uplink, so its
/// WebSocket ping and frame freshness blew out and the link watchdog announced
/// a lost or slow connection, several times per pushed frame (field report,
/// 2026-08-12 session). The watchdog was right: the link really was saturated.
/// It was Polaris saturating it.</para>
///
/// <para>WHY A DUTY CYCLE AND NOT A RATE CAP. A cap in MB/s has to be
/// calibrated per site: too low starves the queue on a fast wired NAS, too high
/// does nothing on a weak WiFi link, and the operator has no way to know which
/// they have. A duty cycle needs no calibration: after each chunk the pacer
/// sleeps in proportion to how long that chunk actually took, so the push uses
/// at most its share of whatever the link can do, fast or slow.</para>
///
/// <para>The sleep is also where the transfer yields, which matters as much as
/// the bandwidth: the push used to hold a thread-pool thread for the whole
/// upload.</para>
/// </summary>
public static class TransferPacer {

    /// <summary>Half the link. Enough for the push to keep up with any normal
    /// capture cadence (a 16 MB sub every 60 s is well under 1 MB/s), while
    /// leaving the interactive half alone.</summary>
    public const int DefaultSharePercent = 50;

    /// <summary>How long to wait after a chunk that took
    /// <paramref name="elapsed"/>, to hold the push at
    /// <paramref name="sharePercent"/> of the link.
    ///
    /// <para>At 50% the push waits as long as the chunk took; at 25% it waits
    /// three times as long. 100 (or more) disables pacing, which is what a
    /// wired NAS with its own network wants.</para></summary>
    public static TimeSpan DelayAfterChunk(TimeSpan elapsed, int sharePercent) {
        if (sharePercent >= 100) return TimeSpan.Zero;
        // A share of 0 would mean "never transfer"; treat it as the slowest
        // useful setting rather than a deadlock.
        var share = Math.Clamp(sharePercent, 1, 100);
        if (elapsed <= TimeSpan.Zero) return TimeSpan.Zero;

        var idle = elapsed.TotalMilliseconds * (100 - share) / share;
        // Cap a single sleep: a chunk that took 10s because the link stalled
        // must not park the queue for 30s on top of it.
        return TimeSpan.FromMilliseconds(Math.Min(idle, MaxDelayMs));
    }

    private const double MaxDelayMs = 2000;
}
