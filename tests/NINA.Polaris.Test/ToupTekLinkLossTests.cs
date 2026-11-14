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

using System.Reflection;
using NINA.Camera.ToupTekSdk;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>A ToupTek camera whose frames time out three times in a row is
/// reported as dropped, so the recovery reopens it without waiting for the
/// SDK's late disconnect event.</summary>
[TestFixture]
public class ToupTekLinkLossTests {
    private static ToupTekSdkCamera Connected() {
        var cam = new ToupTekSdkCamera("usb-0547-1488-1-1");
        typeof(ToupTekSdkCamera).GetField("_connected", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(cam, true);
        return cam;
    }

    [Test]
    public void TwoTimeoutsKeepTheCameraConnected() {
        var cam = Connected();
        for (int i = 1; i < ToupTekSdkCamera.TimeoutsBeforeLinkLost; i++) cam.NoteFrameTimeout();
        Assert.That(cam.IsConnected, Is.True);
        Assert.That(cam.DisconnectReason, Is.Null);
    }

    [Test]
    public void ThreeInARowDropTheLinkWithAReason() {
        var cam = Connected();
        for (int i = 0; i < ToupTekSdkCamera.TimeoutsBeforeLinkLost; i++) cam.NoteFrameTimeout();
        Assert.That(cam.IsConnected, Is.False);
        Assert.That(cam.DisconnectReason, Does.Contain("stopped sending frames"));
    }
}
