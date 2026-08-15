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

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Core.Enum;
using NINA.Image.Interfaces;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// FIELD7-4: the shared camera-ready gate. Every capture path (AUTORUN, ADV, LIVE)
/// now waits on this instead of failing fast against a disconnected camera, so a
/// driver-restart recovery pauses the run instead of burning it.
/// </summary>
[TestFixture]
public class CameraReadyGateTests {
    /// <summary>Minimal camera whose IsConnected can flip. Only the two members the
    /// gate reads are meaningful.</summary>
    private sealed class FlipCamera : ICamera {
        public bool Connected;
        public bool IsConnected => Connected;
        public string DeviceName => "flip";
        public CameraStates State => CameraStates.Idle;
        public double Temperature => 0;
        public bool CoolerOn => false;
        public double CoolerPower => 0;
        public int BinX => 1;
        public int BinY => 1;
        public int BitDepth => 16;
        public int MaxX => 1000;
        public int MaxY => 1000;
        public double PixelSizeX => 3.76;
        public double PixelSizeY => 3.76;
        public int Gain => 0;
        public IReadOnlyList<int> IsoOptions => Array.Empty<int>();
        public int SelectedIso => 0;
        public CameraCapabilities Capabilities => CameraCapabilities.Astro;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IImageData> CaptureAsync(double e, CaptureOptions? o = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task SetBinningAsync(int bx, int by, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetTemperatureAsync(double t, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetCoolerAsync(bool on, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetIsoAsync(int iso, CancellationToken ct = default) => Task.CompletedTask;
        public Task AbortExposureAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [Test]
    public void IsReady_NullOrDisconnected_IsFalse() {
        Assert.That(CameraReadyGate.IsReady(null), Is.False);
        Assert.That(CameraReadyGate.IsReady(new FlipCamera { Connected = false }), Is.False);
        Assert.That(CameraReadyGate.IsReady(new FlipCamera { Connected = true }), Is.True);
    }

    /// <summary>Already ready → returns immediately, no waiting. The steady-state
    /// path must be free.</summary>
    [Test]
    public async Task WaitAsync_CameraReady_ReturnsImmediately() {
        var cam = new FlipCamera { Connected = true };
        var gate = new CameraReadyGate(() => cam, NullLogger<CameraReadyGate>.Instance);

        var task = gate.WaitAsync("test", CancellationToken.None);
        Assert.That(task.IsCompleted, Is.True, "a ready camera must not make the caller await");
        Assert.That(await task, Is.SameAs(cam));
    }

    /// <summary>THE recovery behaviour: disconnected now, connected later → the gate
    /// blocks, then returns the camera once it's back. This is what turns a driver
    /// restart from "run dies" into "run pauses".</summary>
    [Test]
    public async Task WaitAsync_BlocksUntilCameraReconnects() {
        var cam = new FlipCamera { Connected = false };
        var gate = new CameraReadyGate(() => cam, NullLogger<CameraReadyGate>.Instance);

        var wait = gate.WaitAsync("test", CancellationToken.None);
        Assert.That(wait.IsCompleted, Is.False, "must not return while disconnected");

        // Reconnect after a moment; the gate polls at 1s.
        cam.Connected = true;
        var done = await Task.WhenAny(wait, Task.Delay(4000));
        Assert.That(done, Is.SameAs(wait), "gate should have returned after the reconnect");
        Assert.That(await wait, Is.SameAs(cam));
    }

    /// <summary>Cancellation (user stop / run abort) while waiting → null, promptly.
    /// The callers translate this null into a clean cancellation.</summary>
    [Test]
    public async Task WaitAsync_CancelledWhileWaiting_ReturnsNull() {
        var cam = new FlipCamera { Connected = false };
        var gate = new CameraReadyGate(() => cam, NullLogger<CameraReadyGate>.Instance);
        using var cts = new CancellationTokenSource();

        var wait = gate.WaitAsync("test", cts.Token);
        cts.CancelAfter(200);
        var result = await wait;
        Assert.That(result, Is.Null, "a cancelled wait must return null, not throw");
    }

    /// <summary>A reconnect that hands back a DIFFERENT instance: the gate returns
    /// whatever the accessor yields at ready-time, never a cached stale one. This is
    /// the stale-reference hazard the capture loops had.</summary>
    [Test]
    public async Task WaitAsync_ReturnsTheCurrentInstance_NotAStaleOne() {
        var dead = new FlipCamera { Connected = false };
        ICamera? current = dead;
        var gate = new CameraReadyGate(() => current, NullLogger<CameraReadyGate>.Instance);

        var wait = gate.WaitAsync("test", CancellationToken.None);
        // Swap in a fresh, connected instance — as a driver reconnect would.
        var fresh = new FlipCamera { Connected = true };
        current = fresh;

        var done = await Task.WhenAny(wait, Task.Delay(4000));
        Assert.That(done, Is.SameAs(wait));
        Assert.That(await wait, Is.SameAs(fresh), "must return the reconnected instance, not the dead one");
    }

    /// <summary>AUTORUN-BLOB-STUCK (#635): with a finite timeout, a camera that
    /// never comes back returns null once the budget elapses (not a hang), and the
    /// token is NOT cancelled — so the caller can tell "timed out, skip the frame"
    /// apart from a user stop. LIVE keeps the no-timeout overload (waits forever).</summary>
    [Test]
    public async Task WaitAsync_Timeout_ReturnsNullWithoutCancelling() {
        var cam = new FlipCamera { Connected = false };
        var gate = new CameraReadyGate(() => cam, NullLogger<CameraReadyGate>.Instance);
        using var cts = new CancellationTokenSource();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await gate.WaitAsync("test", cts.Token, timeout: TimeSpan.FromSeconds(2));
        sw.Stop();

        Assert.That(result, Is.Null, "a timed-out wait returns null");
        Assert.That(cts.IsCancellationRequested, Is.False, "timeout must not be a cancellation");
        Assert.That(sw.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1.5)),
            "must actually wait roughly the budget before giving up");
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(8)), "but not hang past it");
    }

    /// <summary>onWaiting fires once when it starts blocking; onReady once when it
    /// resumes. LIVE uses these for the WS status line.</summary>
    [Test]
    public async Task WaitAsync_FiresWaitingThenReadyCallbacks() {
        var cam = new FlipCamera { Connected = false };
        var gate = new CameraReadyGate(() => cam, NullLogger<CameraReadyGate>.Instance);
        int waiting = 0, ready = 0;

        var wait = gate.WaitAsync("test", CancellationToken.None,
            onWaiting: _ => waiting++, onReady: () => ready++);
        cam.Connected = true;
        await Task.WhenAny(wait, Task.Delay(4000));
        await wait;

        Assert.That(waiting, Is.EqualTo(1), "onWaiting fires once per outage");
        Assert.That(ready, Is.EqualTo(1), "onReady fires once on resume");
    }
}
