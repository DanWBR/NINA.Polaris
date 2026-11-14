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
using Moq;
using NINA.Image.Interfaces;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>A native-SDK camera that drops off the USB bus on its own is
/// reopened in place; one the operator disconnected stays down.</summary>
[TestFixture]
public class CameraLinkRecoveryTests {
    private sealed class FakeCam {
        public bool Connected = true;
        public string? Reason;
        public readonly Mock<ICamera> Mock = new();
        public FakeCam() {
            Mock.SetupGet(c => c.DeviceName).Returns("G3M662C");
            Mock.SetupGet(c => c.IsConnected).Returns(() => Connected);
            Mock.SetupGet(c => c.DisconnectReason).Returns(() => Reason);
        }
        public ICamera Object => Mock.Object;
        public void Drop() { Connected = false; Reason = "the camera reported itself disconnected from the USB bus"; }
    }

    private static readonly DateTime T0 = new(2026, 11, 14, 22, 0, 0, DateTimeKind.Utc);

    private static (CameraLinkRecoveryService Svc, List<ICamera> Reopened) Make(
            Func<ICamera?> guide, string driver, Func<ICamera, bool> reopenWorks) {
        var reopened = new List<ICamera>();
        var svc = new CameraLinkRecoveryService(
            () => new[] { new CameraLinkRecoveryService.Slot("guide camera", guide(), driver) },
            (cam, _) => { reopened.Add(cam); return Task.FromResult(reopenWorks(cam)); },
            null, NullLogger.Instance);
        return (svc, reopened);
    }

    [Test]
    public async Task ADroppedCameraIsReopenedAfterTheFirstWait() {
        var cam = new FakeCam();
        var (svc, reopened) = Make(() => cam.Object, "touptek-sdk", _ => { cam.Connected = true; cam.Reason = null; return true; });
        cam.Drop();

        await svc.TickAsync(T0, CancellationToken.None);
        Assert.That(reopened, Is.Empty, "the first tick only notices the drop");
        Assert.That(svc.IsPending("guide camera"), Is.True);

        await svc.TickAsync(T0 + CameraLinkRecoveryService.Backoff[0], CancellationToken.None);
        Assert.That(reopened, Has.Count.EqualTo(1));
        Assert.That(reopened[0], Is.SameAs(cam.Object), "the same instance is reopened in place");
        Assert.That(svc.IsPending("guide camera"), Is.False);
    }

    [Test]
    public async Task AFailedAttemptIsRetriedAfterALongerWait() {
        var cam = new FakeCam();
        var (svc, reopened) = Make(() => cam.Object, "touptek-sdk", _ => { cam.Reason = null; return false; });
        cam.Drop();
        var b = CameraLinkRecoveryService.Backoff;

        await svc.TickAsync(T0, CancellationToken.None);
        var t1 = T0 + b[0];
        await svc.TickAsync(t1, CancellationToken.None);
        Assert.That(reopened, Has.Count.EqualTo(1));
        // The reopen closed the dead handle, which clears the adapter's reason;
        // the service keeps trying on its own record.
        await svc.TickAsync(t1 + b[1] - TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.That(reopened, Has.Count.EqualTo(1), "not before the wait");
        await svc.TickAsync(t1 + b[1], CancellationToken.None);
        Assert.That(reopened, Has.Count.EqualTo(2));
        Assert.That(svc.IsPending("guide camera"), Is.True);
    }

    [Test]
    public async Task ADeliberateDisconnectIsLeftAlone() {
        var cam = new FakeCam();
        var (svc, reopened) = Make(() => cam.Object, "touptek-sdk", _ => true);
        cam.Connected = false; // no reason: the operator pressed Disconnect

        await svc.TickAsync(T0, CancellationToken.None);
        await svc.TickAsync(T0 + TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.That(reopened, Is.Empty);
    }

    [Test]
    public async Task StopEndsTheRetries() {
        var cam = new FakeCam();
        var (svc, reopened) = Make(() => cam.Object, "touptek-sdk", _ => { cam.Reason = null; return false; });
        cam.Drop();
        await svc.TickAsync(T0, CancellationToken.None);

        svc.Stop(cam.Object);
        await svc.TickAsync(T0 + TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.That(reopened, Is.Empty);
        Assert.That(svc.IsPending("guide camera"), Is.False);
    }

    [Test]
    public async Task ANewSelectionEndsTheRetriesForTheOldCamera() {
        var old = new FakeCam();
        var replacement = new FakeCam();
        ICamera current = old.Object;
        var (svc, reopened) = Make(() => current, "touptek-sdk", _ => true);
        old.Drop();
        await svc.TickAsync(T0, CancellationToken.None);

        current = replacement.Object;
        await svc.TickAsync(T0 + TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.That(reopened, Is.Empty);
        Assert.That(svc.IsPending("guide camera"), Is.False);
    }

    [Test]
    public async Task ItGivesUpAfterTheWindow() {
        var cam = new FakeCam();
        var (svc, reopened) = Make(() => cam.Object, "touptek-sdk", _ => { cam.Reason = null; return false; });
        cam.Drop();
        await svc.TickAsync(T0, CancellationToken.None);

        var late = T0 + CameraLinkRecoveryService.GiveUpAfter + TimeSpan.FromSeconds(1);
        await svc.TickAsync(late, CancellationToken.None);
        Assert.That(reopened, Is.Empty);
        Assert.That(svc.IsPending("guide camera"), Is.False);
    }

    [TestCase("indi")]
    [TestCase("alpaca")]
    [TestCase("ascom-com")]
    public async Task OtherBackendsAreLeftToTheirOwnWatchdogs(string driver) {
        var cam = new FakeCam();
        var (svc, reopened) = Make(() => cam.Object, driver, _ => true);
        cam.Drop();
        await svc.TickAsync(T0, CancellationToken.None);
        await svc.TickAsync(T0 + TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.That(reopened, Is.Empty);
    }
}
