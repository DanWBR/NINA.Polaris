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

using NUnit.Framework;
using NINA.Camera.ToupTekSdk;

namespace NINA.Polaris.Test;

/// <summary>On Windows the toupcam id of an ATR2600C is a device path; sent
/// through /api/camera/select/{id} it came back with one leading backslash,
/// matched nothing in the enumeration, and Open failed.</summary>
[TestFixture]
public class ToupTekDeviceIdTests {
    private const string SdkId = @"\\?\usb#vid_0547&pid_13da#6&210c5c95&0&2";

    [TestCase(@"\\?\usb#vid_0547&pid_13da#6&210c5c95&0&2")]
    [TestCase(@"\?\usb#vid_0547&pid_13da#6&210c5c95&0&2")]
    [TestCase(@"\\?\USB#VID_0547&PID_13DA#6&210C5C95&0&2")]
    public void AWindowsDevicePath_MatchesWhateverTheRouteLeftOfIt(string requested) {
        Assert.That(ToupTekSdkCamera.SameDeviceId(SdkId, requested), Is.True);
    }

    [Test]
    public void ADifferentCamera_DoesNotMatch() {
        Assert.Multiple(() => {
            Assert.That(ToupTekSdkCamera.SameDeviceId(SdkId, @"\\?\usb#vid_0547&pid_13da#6&210c5c95&0&3"), Is.False);
            Assert.That(ToupTekSdkCamera.SameDeviceId("tp-1-2", "tp-1-3"), Is.False);
            Assert.That(ToupTekSdkCamera.SameDeviceId("tp-1-2", "tp-1-2"), Is.True);
        });
    }
}
