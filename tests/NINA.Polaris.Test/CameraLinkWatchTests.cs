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

using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// When the operator hears that a camera fell off the bus, and when they do
/// not. The detection is in the SDK adapters; this is the part that decides
/// whether it is news, and it runs once a second, so getting it wrong means
/// either silence or sixty identical alarms a minute.
/// </summary>
[TestFixture]
public class CameraLinkWatchTests {

    [Test]
    public void AConnectedCameraSaysNothing() {
        var w = new CameraLinkWatch();
        Assert.That(w.Observe("camera", "ASI2600MC", true, null), Is.Null);
        Assert.That(w.Observe("camera", "ASI2600MC", true, null), Is.Null);
    }

    [Test]
    public void ADropWithAReasonIsReportedOnce() {
        var w = new CameraLinkWatch();
        w.Observe("camera", "ASI2600MC", true, null);

        var first = w.Observe("camera", "ASI2600MC", false, "the camera reported itself removed from the USB bus");
        Assert.That(first, Does.Contain("ASI2600MC"));
        Assert.That(first, Does.Contain("removed from the USB bus"));
        Assert.That(first, Does.Contain("cable"), "the message has to say what to do about it");

        // Still gone a second later, and every second after that. One alarm.
        for (int i = 0; i < 5; i++) {
            Assert.That(w.Observe("camera", "ASI2600MC", false, "the camera reported itself removed from the USB bus"),
                Is.Null, "a camera that is still gone is not new news");
        }
    }

    [Test]
    public void ADeliberateDisconnectIsSilent() {
        // No reason recorded means the adapter was asked to disconnect: the
        // operator pressed the button, the rig changed, a driver restarted.
        var w = new CameraLinkWatch();
        w.Observe("camera", "ASI2600MC", true, null);
        Assert.That(w.Observe("camera", "ASI2600MC", false, null), Is.Null);
        Assert.That(w.Observe("camera", "ASI2600MC", false, "  "), Is.Null);
    }

    [Test]
    public void ACameraThatWasNeverConnectedIsNotADrop() {
        // Selected but never connected, then reporting a reason: there was no
        // link to lose, and shouting at startup teaches the operator to ignore
        // the thing.
        var w = new CameraLinkWatch();
        Assert.That(w.Observe("camera", "ASI2600MC", false, "the camera reported itself removed"), Is.Null);
    }

    [Test]
    public void ReconnectingArmsItAgain() {
        var w = new CameraLinkWatch();
        w.Observe("camera", "ASI2600MC", true, null);
        Assert.That(w.Observe("camera", "ASI2600MC", false, "removed"), Is.Not.Null);
        w.Observe("camera", "ASI2600MC", true, null);
        Assert.That(w.Observe("camera", "ASI2600MC", false, "removed"), Is.Not.Null,
            "a second unplug after a reconnect is a second thing worth saying");
    }

    [Test]
    public void EachRoleIsWatchedSeparately() {
        // A guide camera falling off must not be masked by the imager being
        // fine, and vice versa.
        var w = new CameraLinkWatch();
        w.Observe("camera", "ASI2600MC", true, null);
        w.Observe("guide camera", "ASI120MM", true, null);

        Assert.That(w.Observe("camera", "ASI2600MC", true, null), Is.Null);
        var guide = w.Observe("guide camera", "ASI120MM", false, "removed");
        Assert.That(guide, Does.Contain("ASI120MM"));
    }

    [Test]
    public void AnUnnamedDeviceFallsBackToItsRole() {
        var w = new CameraLinkWatch();
        w.Observe("aux camera", "", true, null);
        Assert.That(w.Observe("aux camera", "", false, "removed"), Does.StartWith("aux camera"));
    }

    [Test]
    public void ForgettingADeviceResetsIt() {
        // The rig changed: whatever was connected under this role is not the
        // same device any more, so its history should not produce an alarm.
        var w = new CameraLinkWatch();
        w.Observe("camera", "ASI2600MC", true, null);
        w.Forget("camera");
        Assert.That(w.Observe("camera", "SV605CC", false, "removed"), Is.Null);
    }
}
