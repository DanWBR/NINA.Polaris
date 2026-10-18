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
using System.Collections.Concurrent;
using NINA.Image.Interfaces;
using NINA.INDI.Client;
using NINA.INDI.Protocol;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>Polaris opened an AE676C through the ToupTek SDK while
/// indi_toupcam_ccd, left running from an earlier session, had it connected,
/// and died with a segfault nine seconds after starting.</summary>
[TestFixture]
public class CameraClaimsTests {
    private Func<string, string?>? _saved;

    [SetUp] public void Save() => _saved = CameraClaims.HolderOf;
    [TearDown] public void Restore() => CameraClaims.HolderOf = _saved;

    [TestCase("ToupTek AE676C", "AE676C", true)]
    [TestCase("ToupTek AE676C", "ToupTek AE676C", true)]
    [TestCase("ZWO CCD ASI678MC", "ZWO ASI678MC", true)]
    [TestCase("ZWO CCD ASI585MC", "ZWO ASI678MC", false)]
    [TestCase("ToupTek ATR533C", "AE676C", false)]
    [TestCase("USB Camera", "USB3.0 Camera", false)]
    public void TheModelInTheNativeNameIsLookedForInTheIndiDevice(string indi, string native, bool same) {
        Assert.That(CameraClaims.SameModel(indi, native), Is.EqualTo(same));
    }

    [Test]
    public void AConnectedIndiCameraOfTheSameModelHoldsIt() {
        using var indi = new IndiClient();
        Define(indi, "ToupTek AE676C", ccd: true, connected: true);
        Define(indi, "ZWO AM5 USB", ccd: false, connected: true);

        Assert.That(EquipmentManager.IndiCameraHolding(indi, "AE676C"), Is.EqualTo("ToupTek AE676C"));
        Assert.That(EquipmentManager.IndiCameraHolding(indi, "ZWO ASI678MC"), Is.Null);
    }

    [Test]
    public void ADriverThatIsRunningButNotConnectedHoldsNothing() {
        using var indi = new IndiClient();
        Define(indi, "ToupTek AE676C", ccd: true, connected: false);

        Assert.That(EquipmentManager.IndiCameraHolding(indi, "AE676C"), Is.Null,
            "indi_toupcam_ccd only opens the camera on CONNECT");
    }

    [Test]
    public void TheNativeOpenIsRefusedWithBothWaysOut() {
        CameraClaims.HolderOf = name => name == "AE676C" ? "ToupTek AE676C" : null;

        var ex = Assert.Throws<InvalidOperationException>(() => CameraClaims.ThrowIfHeld("AE676C", "ToupTek SDK"));
        Assert.That(ex!.Message, Does.Contain("ToupTek AE676C").And.Contain("disconnect the INDI device"));
        Assert.DoesNotThrow(() => CameraClaims.ThrowIfHeld("ASI678MC", "ZWO SDK"));
    }

    [Test]
    public void ABrokenLookupDoesNotStopTheOpen() {
        CameraClaims.HolderOf = _ => throw new InvalidOperationException("boom");
        Assert.DoesNotThrow(() => CameraClaims.ThrowIfHeld("AE676C", "ToupTek SDK"));
    }

    private static void Define(IndiClient indi, string device, bool ccd, bool connected) {
        var props = indi.Devices.GetOrAdd(device, _ => new ConcurrentDictionary<string, IndiProperty>());
        props["CONNECTION"] = new IndiSwitchProperty {
            Device = device,
            Name = "CONNECTION",
            Values = new() { ["CONNECT"] = connected, ["DISCONNECT"] = !connected },
        };
        if (ccd) props["CCD_EXPOSURE"] = new IndiNumberProperty { Device = device, Name = "CCD_EXPOSURE" };
    }
}
