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
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Core.Enum;
using NINA.Image.Interfaces;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Acting on the rig while something is already running (issue #32).
///
/// <para>Two of the three reported behaviours are pinned here. Cancelling a
/// plate solve left the mount not tracking, because the state that goes down
/// during a GoTo is restored when the GoTo completes and a cancelled one never
/// does. And Dither moved the mount the instant it was pressed, including
/// straight through an open shutter.</para>
/// </summary>
[TestFixture]
// The dither half drives CameraCaptureGate, which is process-wide state,
// like the existing CameraCaptureGateTests.
[NonParallelizable]
public class InterruptDuringImagingTests {

    private sealed class FakeMount : ITelescope {
        public List<string> Calls { get; } = new();
        public bool ThrowOnAbort { get; set; }
        public bool Tracking { get; set; }

        public string DeviceName => "Fake";
        public bool IsConnected { get; set; } = true;
        public bool IsTracking => Tracking;
        public bool IsSlewing => false;
        public bool IsParked => false;
        public bool IsAtHome => false;
        public double RightAscension => 0;
        public double Declination => 0;
        public double Altitude => 45;
        public double Azimuth => 180;
        public double SiteLatitude { get; set; }
        public double SiteLongitude { get; set; }
        public double SiteElevation { get; set; }
        public PierSide SideOfPier => PierSide.pierUnknown;
        public MountCapabilities Capabilities => new(
            SupportsPark: false, SupportsTrackingToggle: true, SupportsSync: false,
            SupportsPierSide: false, SupportsManualJog: false);

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SlewAsync(double ra, double dec, CancellationToken ct = default) => Task.CompletedTask;
        public Task SyncAsync(double ra, double dec, CancellationToken ct = default) => Task.CompletedTask;
        public Task ParkAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UnparkAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SetTrackingAsync(bool enabled, CancellationToken ct = default) {
            Calls.Add("tracking=" + enabled);
            Tracking = enabled;
            return Task.CompletedTask;
        }

        public Task AbortSlewAsync(CancellationToken ct = default) {
            Calls.Add("abort");
            // What the field report describes: the mount comes to rest and
            // tracking is not running any more.
            Tracking = false;
            if (ThrowOnAbort) throw new InvalidOperationException("driver said no");
            return Task.CompletedTask;
        }

        public Task MoveNorthAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveSouthAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveEastAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveWestAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopMotionAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [SetUp]
    public void FastSettle() => MountAbort.SettleAfterAbort = TimeSpan.Zero;

    [TearDown]
    public void RestoreSettle() => MountAbort.SettleAfterAbort = TimeSpan.FromMilliseconds(400);

    // ---- 1. Cancelling a centering run leaves the mount as it was found ----

    [Test]
    public async Task CancellingWhileTracking_StopsTheMountAndPutsTrackingBack() {
        var mount = new FakeMount { Tracking = true };
        var wasTracking = MountAbort.WasTracking(mount);

        await MountAbort.AbortAndRestoreTrackingAsync(mount, wasTracking, NullLogger.Instance);

        Assert.Multiple(() => {
            Assert.That(mount.Calls, Is.EqualTo(new[] { "abort", "tracking=True" }),
                "the abort comes first, then the mount is handed back tracking");
            Assert.That(mount.IsTracking, Is.True);
        });
    }

    /// <summary>A mount that was parked or sitting idle stays that way. Turning
    /// tracking on because an operation was cancelled would start the mount
    /// moving on its own, which is the opposite of what Stop means.</summary>
    [Test]
    public async Task CancellingWhileNotTracking_LeavesTrackingOff() {
        var mount = new FakeMount { Tracking = false };
        var wasTracking = MountAbort.WasTracking(mount);

        await MountAbort.AbortAndRestoreTrackingAsync(mount, wasTracking, NullLogger.Instance);

        Assert.Multiple(() => {
            Assert.That(mount.Calls, Is.EqualTo(new[] { "abort" }));
            Assert.That(mount.IsTracking, Is.False);
        });
    }

    /// <summary>A driver that refuses the abort must not take the restore down
    /// with it. The whole point of this path is that the mount ends up in a
    /// known state even when something goes wrong.</summary>
    [Test]
    public async Task AnAbortThatThrows_StillRestoresTracking() {
        var mount = new FakeMount { Tracking = true, ThrowOnAbort = true };

        await MountAbort.AbortAndRestoreTrackingAsync(mount, true, NullLogger.Instance);

        Assert.That(mount.IsTracking, Is.True);
    }

    [Test]
    public void NoMountAtAll_IsNotAnError() {
        Assert.DoesNotThrowAsync(() =>
            MountAbort.AbortAndRestoreTrackingAsync(null, true, NullLogger.Instance));
    }

    [Test]
    public void WasTracking_IgnoresADisconnectedMount() {
        var mount = new FakeMount { Tracking = true, IsConnected = false };
        Assert.That(MountAbort.WasTracking(mount), Is.False);
    }

    /// <summary>Both cancel paths go through the one routine. The solar-system
    /// centering job runs its own final hop after the inner solve, so it can be
    /// cancelled at a point the inner job never sees.</summary>
    [Test]
    public void BothCenteringServicesCancelThroughMountAbort() {
        foreach (var file in new[] { "SlewCenterService.cs", "SolarSystemCenterService.cs" }) {
            var src = Source("src", "NINA.Polaris", "Services", file);
            Assert.Multiple(() => {
                Assert.That(src, Does.Contain("MountAbort.AbortAndRestoreTrackingAsync"), file);
                Assert.That(src, Does.Contain("TrackingWasOn = MountAbort.WasTracking"),
                    file + " must record the state before anything moves");
                Assert.That(src, Does.Not.Contain("_equip.Telescope?.AbortSlewAsync()"),
                    file + " still aborts without restoring tracking");
            });
        }
    }

    // ---- 2. A dither waits for the shutter ----

    [Test]
    public async Task TheGateReportsACaptureInFlight() {
        Assert.That(CameraCaptureGate.CaptureInFlight, Is.False, "nothing is running yet");

        var inCapture = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var capture = CameraCaptureGate.RunAsync(async () => {
            inCapture.SetResult();
            await release.Task;
        });

        await inCapture.Task;
        Assert.That(CameraCaptureGate.CaptureInFlight, Is.True);

        release.SetResult();
        await capture;
        Assert.That(CameraCaptureGate.CaptureInFlight, Is.False);
    }

    /// <summary>The dither runs under the main camera's gate, which is what
    /// makes it wait for the exposure in flight and keeps the next one from
    /// starting before the settle is done.</summary>
    [Test]
    public async Task WorkQueuedBehindACapture_RunsOnlyAfterItFinishes() {
        var inCapture = new TaskCompletionSource();
        var releaseCapture = new TaskCompletionSource();
        var dithered = false;

        var capture = CameraCaptureGate.RunAsync(async () => {
            inCapture.SetResult();
            await releaseCapture.Task;
        });
        await inCapture.Task;

        var dither = CameraCaptureGate.RunAsync(() => {
            dithered = true;
            return Task.CompletedTask;
        }, acquireTimeout: TimeSpan.FromSeconds(5));

        await Task.Delay(50);
        Assert.That(dithered, Is.False, "the mount must not move while the shutter is open");

        releaseCapture.SetResult();
        await capture;
        await dither;
        Assert.That(dithered, Is.True);
    }

    /// <summary>A browser will not hold a request open for a five minute
    /// exposure, so a dither that has to wait is accepted and finished in the
    /// background rather than blocking the response.</summary>
    [Test]
    public void TheDitherEndpointWaitsForTheShutter() {
        var src = Source("src", "NINA.Polaris", "Endpoints", "GuiderEndpoints.cs");

        Assert.Multiple(() => {
            Assert.That(src, Does.Contain("CameraCaptureGate.RunAsync(Dither"),
                "the dither has to hold the capture gate");
            Assert.That(src, Does.Contain("CameraCaptureGate.CaptureInFlight"));
            Assert.That(src, Does.Contain("Results.Accepted"),
                "a queued dither answers 202 instead of holding the request open");
            Assert.That(src, Does.Contain("CameraCaptureGate.ExclusiveOwner != null"),
                "a native video stream has no long shutter to ruin and the gate "
                + "would refuse the dither outright");
        });
    }

    private static string Here([CallerFilePath] string p = "") => p;

    private static string Source(params string[] parts) {
        var here = Path.GetDirectoryName(Here())!;
        var all = new List<string> { here, "..", ".." };
        all.AddRange(parts);
        var path = Path.GetFullPath(Path.Combine(all.ToArray()));
        Assert.That(File.Exists(path), $"nao achei {path}");
        return File.ReadAllText(path);
    }
}
