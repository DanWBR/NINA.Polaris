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

using System.Net.Sockets;

namespace NINA.Polaris.Services;

/// <summary>
/// Shared TCP reachability probe for the service health/listen checks.
/// </summary>
/// <remarks>
/// The naive pattern these checks used —
/// <code>
/// var connect = tcp.ConnectAsync(host, port, ct).AsTask();
/// var winner  = await Task.WhenAny(connect, Task.Delay(timeoutMs, ct));
/// return winner == connect &amp;&amp; tcp.Connected;
/// </code>
/// abandons <c>connect</c> when the timeout wins. That task keeps running; if it
/// later completes faulted (a delayed <c>SocketException (111): Connection
/// refused</c> can arrive well after the cap under load), nothing observes the
/// fault, so the finalizer rethrows it as an <c>UnobservedTaskException</c> —
/// the misleading <c>[FATAL]</c> lines that flood the log at the GC's cadence,
/// not the probe's. This helper always OBSERVES the connect task: on timeout it
/// attaches a fault-only continuation that swallows the eventual exception.
/// </remarks>
internal static class NetProbe {
    /// <summary>True if a TCP connection to <paramref name="host"/>:<paramref
    /// name="port"/> completes within <paramref name="timeoutMs"/> ms. Never
    /// throws (any failure, timeout, or cancellation returns false) and never
    /// leaves an unobserved connect task dangling.</summary>
    public static async Task<bool> TryConnectAsync(
            string host, int port, int timeoutMs, CancellationToken ct) {
        var tcp = new TcpClient();
        try {
            var connect = tcp.ConnectAsync(host, port, ct).AsTask();
            var winner = await Task.WhenAny(connect, Task.Delay(timeoutMs, ct))
                .ConfigureAwait(false);
            if (winner == connect) {
                await connect.ConfigureAwait(false);   // observe success/fault
                return tcp.Connected;
            }
            // Timeout won: the connect task is abandoned but must still be
            // observed so a late fault never surfaces as an
            // UnobservedTaskException. A fault-only continuation reads .Exception
            // (which marks it observed) once the task finally completes.
            _ = connect.ContinueWith(static t => { _ = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return false;
        } catch {
            return false;
        } finally {
            tcp.Dispose();
        }
    }
}
