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
/// Keeps ONE fresh copy of the equipment status block, refreshed on a thread of
/// its own, so reading it never blocks a web request.
///
/// <para>Field report 2026-09-22: moving the mount with the video-panel joystick
/// while a stream was running froze the host, and the client painted the amber
/// "slow network" warning, which was a lie. The chain: the 1 Hz status payload
/// reads LIVE hardware (<c>EquipmentManager.GetEquipmentStatus</c> calls
/// <c>Camera.Temperature</c>, <c>CoolerOn</c>, <c>CoolerPower</c>, focuser
/// temperature, and so on). Those reads block. On a ZWO camera each one takes
/// the per-handle SDK lock that the video grab loop re-acquires immediately, and
/// <see cref="System.Threading.Monitor"/> is not fair, so the reader can lose
/// that race for as long as the stream runs. On an Alpaca device each one is a
/// synchronous HTTP call on a client with a 15 s timeout. The payload was built
/// on a THREAD POOL thread, and the per-tick payload cache only helps while
/// builds are fast: as soon as one is slow the cache misses for every client, so
/// each connected browser starts its own build, each parks another pool thread
/// on the same device, and the pool only grows by about one thread every 500 ms.
/// Kestrel then has nothing left to run the joystick's POST on, and the 1 Hz
/// frames stop arriving, which is what the client reported as a network
/// problem.</para>
///
/// <para>So the hardware reads move here, onto a single dedicated thread that
/// paces itself. A device that blocks for fifteen seconds now delays exactly one
/// thing, this loop, and the snapshot simply goes stale: every socket keeps
/// ticking, every request keeps being served, and
/// <see cref="Stalled"/> is what the UI uses to say the host is busy instead of
/// blaming the network.</para>
/// </summary>
public sealed class EquipmentSnapshotService : BackgroundService {
    private readonly EquipmentManager _equip;
    private readonly ILogger<EquipmentSnapshotService> _logger;

    /// <summary>Target refresh rate, matched to the status tick. Self-paced: a
    /// refresh that takes longer than this simply starts the next one late,
    /// rather than stacking up.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>A refresh still running after this long means a device is not
    /// answering, and the snapshot being served is old. Four ticks: long enough
    /// that a normal slow read (a USB filter wheel mid-move) is not called a
    /// stall, short enough that the UI can say so while it matters.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(4);

    private readonly object _gate = new();
    private object? _latest;
    private DateTime _latestAtUtc;
    private DateTime? _refreshStartedAtUtc;
    private double _lastRefreshMs;
    private Thread? _thread;

    public EquipmentSnapshotService(EquipmentManager equip,
                                    ILogger<EquipmentSnapshotService> logger) {
        _equip = equip;
        _logger = logger;
    }

    /// <summary>The newest equipment block, or null before the first refresh
    /// completes.</summary>
    public object? Latest {
        get { lock (_gate) return _latest; }
    }

    /// <summary>How old the served snapshot is. <see cref="TimeSpan.MaxValue"/>
    /// before the first one exists.</summary>
    public TimeSpan Age {
        get {
            lock (_gate) {
                return _latest == null ? TimeSpan.MaxValue : DateTime.UtcNow - _latestAtUtc;
            }
        }
    }

    /// <summary>How long the last completed refresh took. A device that has
    /// started to drag shows up here before it shows up as a stall.</summary>
    public double LastRefreshMs {
        get { lock (_gate) return _lastRefreshMs; }
    }

    /// <summary>True while a refresh has been running longer than
    /// <see cref="StaleAfter"/>: some device is not answering, and what is being
    /// served is old. This is the honest "the host is busy" signal.</summary>
    public bool Stalled {
        get {
            lock (_gate) {
                return _refreshStartedAtUtc is DateTime started
                       && DateTime.UtcNow - started > StaleAfter;
            }
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) {
        // A dedicated thread, not the pool: the whole point is that a device
        // read which blocks for seconds parks THIS thread and nothing the web
        // server needs. Below normal priority because a status refresh must
        // never outrank a capture or the guide loop.
        _thread = new Thread(() => Loop(stoppingToken)) {
            IsBackground = true,
            Name = "equipment-snapshot",
            Priority = ThreadPriority.BelowNormal
        };
        _thread.Start();
        return Task.CompletedTask;
    }

    private void Loop(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            var started = DateTime.UtcNow;
            lock (_gate) _refreshStartedAtUtc = started;
            try {
                var snapshot = _equip.GetEquipmentStatus();
                var done = DateTime.UtcNow;
                lock (_gate) {
                    _latest = snapshot;
                    _latestAtUtc = done;
                    _lastRefreshMs = (done - started).TotalMilliseconds;
                }
            } catch (Exception ex) {
                // Keep the last good snapshot. A device that throws on a
                // property read is not a reason to blank every panel in the UI;
                // the age is what tells the operator it is not fresh.
                _logger.LogDebug(ex, "Equipment snapshot refresh failed");
            } finally {
                lock (_gate) _refreshStartedAtUtc = null;
            }

            var rest = Interval - (DateTime.UtcNow - started);
            if (rest > TimeSpan.Zero) {
                try { ct.WaitHandle.WaitOne(rest); } catch { break; }
            }
        }
    }
}
