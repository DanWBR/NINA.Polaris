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
/// Keeps a service's job dictionary from growing for the lifetime of the
/// process.
///
/// Every long-running operation here follows the same shape: start a job, hand
/// the caller an id, let it poll for progress. The dictionary that backs that
/// only ever gained entries, so a host left running for a week accumulated
/// every plate solve, script run and stack it had ever done. Most jobs are
/// small, but the planetary stacker keeps one quality score per video frame,
/// which is thousands of doubles per recording.
///
/// Two rules make this safe to call from the hot path:
///   - a job that has not finished is never evicted, no matter how many there
///     are, so a burst of concurrent work cannot drop a job the caller is
///     still waiting on;
///   - the oldest finished jobs go first, so the most recent results stay
///     available for a client that polls after the fact.
/// </summary>
public static class JobRetention {

    /// <summary>How many jobs a service keeps around. Far more than any client
    /// polls for, small enough that the retained set never matters.</summary>
    public const int DefaultMaxRetained = 64;

    /// <summary>Drop the oldest finished jobs once the dictionary is over
    /// <paramref name="maxRetained"/>. Call right after inserting a job.</summary>
    /// <param name="jobs">The service's job dictionary.</param>
    /// <param name="startedUtc">When the job was created, used for ordering.</param>
    /// <param name="isFinished">Whether the job reached a terminal state.
    /// Anything that returns false is retained unconditionally.</param>
    public static void TrimFinished<TJob>(
            ConcurrentDictionary<string, TJob> jobs,
            Func<TJob, DateTime> startedUtc,
            Func<TJob, bool> isFinished,
            int maxRetained = DefaultMaxRetained) {
        if (jobs.Count <= maxRetained) return;

        var excess = jobs.Count - maxRetained;
        var evictable = jobs
            .Where(kv => isFinished(kv.Value))
            .OrderBy(kv => startedUtc(kv.Value))
            .Take(excess)
            .ToList();

        foreach (var kv in evictable) {
            if (jobs.TryRemove(kv.Key, out var removed) && removed is IDisposable disposable) {
                try { disposable.Dispose(); } catch { /* the job is on its way out */ }
            }
        }
    }
}
