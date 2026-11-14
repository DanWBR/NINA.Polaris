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

using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NINA.Camera.SvbonySdk;
using static NINA.Camera.SvbonySdk.Native.SvbonyNative;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>Every change of SVBony camera mode resets the camera on the USB
/// bus, so stills stay in the still mode and only video switches.</summary>
[TestFixture]
public class SvbonyCameraModeTests {
    private static string Here([CallerFilePath] string p = "") => p;
    private static string Source() => File.ReadAllText(Path.Combine(Path.GetDirectoryName(Here())!,
        "..", "..", "src", "NINA.Camera.SvbonySdk", "SvbonySdkCamera.cs"));

    [Test]
    public void StillsUseSoftTriggerWhenTheCameraHasIt() {
        Assert.That(SvbonySdkCamera.StillMode(true), Is.EqualTo(SVB_CAMERA_MODE.SVB_MODE_TRIG_SOFT));
        Assert.That(SvbonySdkCamera.StillMode(false), Is.EqualTo(SVB_CAMERA_MODE.SVB_MODE_NORMAL));
    }

    /// <summary>The mode is only set through the guard that skips a call for
    /// the mode already set; a direct SVBSetCameraMode elsewhere would bring
    /// the per-frame resets back.</summary>
    [Test]
    public void OnlyTheGuardCallsSetCameraMode() {
        var calls = Regex.Matches(Source(), @"SVBSetCameraMode\(_cameraId");
        Assert.That(calls.Count, Is.EqualTo(1));
    }
}
