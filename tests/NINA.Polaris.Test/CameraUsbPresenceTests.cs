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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NINA.Image.Interfaces;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// A camera pulled out by its cable is seen leaving the bus in sysfs and
/// marked disconnected, so the recovery retries start. The tree is the FRA400
/// Q6A as it was on 2026-11-18: two ZWO cameras plus a ZWO mount and EAF,
/// all on vendor 03c3.
/// </summary>
[TestFixture]
public class CameraUsbPresenceTests {
    private string _root = "";

    [SetUp]
    public void SetUp() {
        _root = Path.Combine(Path.GetTempPath(), "polaris-usb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Device("1-1", "1a40", "0101", "USB 2.0 Hub", "2");
        Device("1-1.1", "03c3", "4001", "ZWO Device", "5");
        Device("1-1.2", "03c3", "120c", "ASI120MM Mini", "9");
        Device("2-1.1", "03c3", "1f10", "EFF", "4");
        Device("3-1.3", "03c3", "260a", "ASI2600MC Pro", "3");
        // An interface entry. Windows does not allow the colon in a folder name.
        if (!OperatingSystem.IsWindows()) Directory.CreateDirectory(Path.Combine(_root, "1-1.2:1.0"));
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_root, true); } catch { }
    }

    private void Device(string name, string vid, string pid, string product, string devnum) {
        var d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "idVendor"), vid + "\n");
        File.WriteAllText(Path.Combine(d, "idProduct"), pid + "\n");
        File.WriteAllText(Path.Combine(d, "product"), product + "\n");
        File.WriteAllText(Path.Combine(d, "devnum"), devnum + "\n");
    }

    private void Unplug(string name) => Directory.Delete(Path.Combine(_root, name), true);

    private static readonly CameraUsbPresence.Spec Zwo = CameraUsbPresence.SpecFor("zwo-sdk")!.Value;

    [Test]
    public void EachZwoCameraIsFoundByItsModelNotByTheVendor() {
        var nodes = CameraUsbPresence.List(_root);
        Assert.That(nodes, Has.Count.EqualTo(5), "the interface entry is not a device");
        var none = new HashSet<string>();
        Assert.Multiple(() => {
            Assert.That(CameraUsbPresence.Resolve(Zwo, "ZWO ASI120MM Mini", nodes, none)?.Name, Is.EqualTo("1-1.2"));
            Assert.That(CameraUsbPresence.Resolve(Zwo, "ZWO ASI2600MC Pro", nodes, none)?.Name, Is.EqualTo("3-1.3"));
            Assert.That(CameraUsbPresence.Resolve(Zwo, "ZWO ASI585MC Pro", nodes, none), Is.Null,
                "a model that is not on the bus is not mistaken for the mount or the EAF");
        });
    }

    [Test]
    public void GenericProductStringsAreNotModels() {
        Assert.Multiple(() => {
            Assert.That(CameraUsbPresence.NamesTheModel("ZWO Device", "ZWO ASI120MM Mini"), Is.False);
            Assert.That(CameraUsbPresence.NamesTheModel("USB3.0 Camera", "SVBONY SV705C"), Is.False);
            Assert.That(CameraUsbPresence.NamesTheModel("", "ZWO ASI120MM Mini"), Is.False);
            Assert.That(CameraUsbPresence.NamesTheModel("ASI678MC", "ZWO ASI678MC"), Is.True);
        });
    }

    [Test]
    public void ALoneSvbonyDeviceIsTheCameraEvenWithAGenericProductString() {
        Device("1-1.3", "f266", "9a0a", "USB3.0 Camera", "11");
        var spec = CameraUsbPresence.SpecFor("svbony-sdk")!.Value;
        var node = CameraUsbPresence.Resolve(spec, "SVBONY SV705C", CameraUsbPresence.List(_root), new HashSet<string>());
        Assert.That(node?.Name, Is.EqualTo("1-1.3"));
    }

    [Test]
    public void ARenumeratedDeviceIsNotTheSameDevice() {
        var node = CameraUsbPresence.Read(_root, "1-1.2")!;
        Assert.That(CameraUsbPresence.StillThere(_root, node), Is.True);
        Unplug("1-1.2");
        Assert.That(CameraUsbPresence.StillThere(_root, node), Is.False, "unplugged");
        Device("1-1.2", "03c3", "120c", "ASI120MM Mini", "12");
        Assert.That(CameraUsbPresence.StillThere(_root, node), Is.False, "back with a new device number");
    }

    private sealed class FakeCam {
        public bool Connected = true;
        public string? Reason;
        public readonly Mock<ICamera> Mock = new();
        public FakeCam(string name) {
            Mock.SetupGet(c => c.DeviceName).Returns(name);
            Mock.SetupGet(c => c.IsConnected).Returns(() => Connected);
            Mock.SetupGet(c => c.DisconnectReason).Returns(() => Reason);
            Mock.Setup(c => c.MarkRemoved(It.IsAny<string>()))
                .Callback<string>(r => { if (Connected) { Connected = false; Reason = r; } });
        }
        public ICamera Object => Mock.Object;
    }

    private static readonly DateTime T0 = new(2026, 11, 18, 22, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task UnpluggingTheGuideCameraMarksOnlyItAndStartsTheRetries() {
        var main = new FakeCam("ZWO ASI2600MC Pro");
        var guide = new FakeCam("ZWO ASI120MM Mini");
        var reopened = new List<ICamera>();
        var svc = new CameraLinkRecoveryService(
            () => new[] {
                new CameraLinkRecoveryService.Slot("camera", main.Object, "zwo-sdk"),
                new CameraLinkRecoveryService.Slot("guide camera", guide.Object, "zwo-sdk") },
            (cam, _) => { reopened.Add(cam); return Task.FromResult(false); },
            null, NullLogger.Instance, _root);

        await svc.TickAsync(T0, CancellationToken.None);
        Assert.That(guide.Connected && main.Connected, Is.True, "both still there");

        Unplug("1-1.2");
        await svc.TickAsync(T0 + TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Multiple(() => {
            Assert.That(guide.Connected, Is.False);
            Assert.That(guide.Reason, Is.EqualTo(CameraLinkRecoveryService.UnpluggedReason));
            Assert.That(main.Connected, Is.True, "the main camera stayed on the bus");
            Assert.That(svc.IsPending("guide camera"), Is.True);
        });

        await svc.TickAsync(T0 + TimeSpan.FromSeconds(1) + CameraLinkRecoveryService.Backoff[0], CancellationToken.None);
        Assert.That(reopened, Is.EqualTo(new[] { guide.Object }));
    }

    [Test]
    public void AnIndiCameraIsPlacedByTheVendorInItsDeviceName() {
        Assert.Multiple(() => {
            Assert.That(CameraUsbPresence.SpecFor("indi", "ZWO CCD ASI120MM Mini")?.Vendors, Is.EqualTo(new[] { "03c3" }));
            Assert.That(CameraUsbPresence.SpecFor("indi", "SVBONY SV705C")?.Vendors, Is.EqualTo(new[] { "f266" }));
            Assert.That(CameraUsbPresence.SpecFor("indi", "Canon DSLR EOS 600D"), Is.Null);
            Assert.That(CameraUsbPresence.SpecFor("alpaca", "ZWO ASI120MM Mini"), Is.Null);
        });
        var node = CameraUsbPresence.Resolve(CameraUsbPresence.SpecFor("indi", "ZWO CCD ASI120MM Mini")!.Value,
            "ZWO CCD ASI120MM Mini", CameraUsbPresence.List(_root), new HashSet<string>());
        Assert.That(node?.Name, Is.EqualTo("1-1.2"));
    }

    /// <summary>The INDI driver goes on saying CONNECTION=On after the cable
    /// comes out. The camera is marked removed, and the driver is only
    /// touched once the camera is on the bus again.</summary>
    [Test]
    public async Task AnUnpluggedIndiCameraIsReopenedOnlyAfterItIsBack() {
        var guide = new FakeCam("ZWO CCD ASI120MM Mini");
        var reopened = new List<ICamera>();
        var svc = new CameraLinkRecoveryService(
            () => new[] { new CameraLinkRecoveryService.Slot("guide camera", guide.Object, "indi") },
            (_, _) => throw new InvalidOperationException("an INDI camera never takes the SDK reopen"),
            null, NullLogger.Instance, _root,
            (cam, _) => { reopened.Add(cam); guide.Connected = true; guide.Reason = null; return Task.FromResult(true); });

        await svc.TickAsync(T0, CancellationToken.None);
        Unplug("1-1.2");
        var t = T0 + TimeSpan.FromSeconds(1);
        await svc.TickAsync(t, CancellationToken.None);
        Assert.That(guide.Connected, Is.False);
        Assert.That(svc.IsPending("guide camera"), Is.True);

        for (int i = 0; i < 10; i++) {
            t += CameraLinkRecoveryService.IndiReturnPoll;
            await svc.TickAsync(t, CancellationToken.None);
        }
        Assert.That(reopened, Is.Empty, "the camera is still off the bus");

        Device("1-1.2", "03c3", "120c", "ASI120MM Mini", "14");
        t += CameraLinkRecoveryService.IndiReturnPoll;
        await svc.TickAsync(t, CancellationToken.None);
        Assert.That(reopened, Is.EqualTo(new[] { guide.Object }));
        Assert.That(svc.IsPending("guide camera"), Is.False);
    }

    [Test]
    public async Task ACameraThatCannotBeToldApartIsNotWatched() {
        var guide = new FakeCam("ZWO ASI120MM Mini");
        Device("1-1.3", "03c3", "120c", "ASI120MM Mini", "13"); // a second identical camera
        var svc = new CameraLinkRecoveryService(
            () => new[] { new CameraLinkRecoveryService.Slot("guide camera", guide.Object, "zwo-sdk") },
            (_, _) => Task.FromResult(false), null, NullLogger.Instance, _root);

        await svc.TickAsync(T0, CancellationToken.None);
        Unplug("1-1.3");
        await svc.TickAsync(T0 + TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.That(guide.Connected, Is.True, "it could have been either one, so neither is marked");
    }
}
