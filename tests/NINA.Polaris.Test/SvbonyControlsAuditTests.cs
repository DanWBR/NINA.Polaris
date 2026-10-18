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

using NINA.Camera.SvbonySdk;
using NUnit.Framework;
using static NINA.Camera.SvbonySdk.Native.SvbonyNative;

namespace NINA.Polaris.Test;

/// <summary>Flip and frame speed came out as 0..3 and 0..2 sliders. This is
/// the pure part that types them as lists for the panel.</summary>
[TestFixture]
public class SvbonyControlsAuditTests {
    [Test]
    public void FlipIsAListOfItsFourStates() {
        var (vt, options) = SvbonySdkCamera.ControlKind((int)SVB_CONTROL_TYPE.SVB_FLIP, 0, 3);
        Assert.That(vt, Is.EqualTo("enum"));
        Assert.That(options, Is.EqualTo(new[] { "None", "Horizontal", "Vertical", "Both" }));
    }

    [Test]
    public void FrameSpeedIsLowMediumHigh() {
        var (vt, options) = SvbonySdkCamera.ControlKind((int)SVB_CONTROL_TYPE.SVB_FRAME_SPEED_MODE, 0, 2);
        Assert.That(vt, Is.EqualTo("enum"));
        Assert.That(options, Is.EqualTo(new[] { "Low", "Medium", "High" }));
    }

    [Test]
    public void AFrameSpeedWithTwoStepsListsOnlyThose() {
        var (vt, options) = SvbonySdkCamera.ControlKind((int)SVB_CONTROL_TYPE.SVB_FRAME_SPEED_MODE, 0, 1);
        Assert.That(vt, Is.EqualTo("enum"));
        Assert.That(options, Is.EqualTo(new[] { "Low", "Medium" }));
    }

    [TestCase(18, 0, 1, "bool")]                                   // bad pixel correction
    [TestCase((int)SVB_CONTROL_TYPE.SVB_GAIN, 0, 720, "int")]
    [TestCase((int)SVB_CONTROL_TYPE.SVB_FLIP, 0, 9, "int")]        // a range it cannot name
    public void OtherControlsAreToggleOrNumber(int type, double min, double max, string expected) {
        var (vt, options) = SvbonySdkCamera.ControlKind(type, min, max);
        Assert.That(vt, Is.EqualTo(expected));
        Assert.That(options, Is.Null);
    }
}
