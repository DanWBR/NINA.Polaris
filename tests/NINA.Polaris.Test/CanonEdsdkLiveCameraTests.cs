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
using NINA.Camera.CanonEdsdk;

namespace NINA.Polaris.Test;

/// <summary>
/// A real Canon body over EDSDK: connect, take one picture, get it back. Only
/// runs when POLARIS_TEST_CANON_SDK_DIR points at a folder holding the 64-bit
/// EDSDK.dll and EdsImage.dll and a camera is plugged in and free (no other
/// program holding a session on it). This is the path that hung: the shutter
/// fired, and the picture never reached Polaris because nobody pumped the
/// SDK's events.
/// </summary>
[TestFixture]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class CanonEdsdkLiveCameraTests {
    /// <summary>EdsDirectoryItemInfo as EDSDK 13 lays it out (EDSDKTypes.h):
    /// a 64-bit size first, so the name starts at 20, and packed to 8 the
    /// struct is 288 bytes. A 32-bit size made it 280: the SDK wrote past the
    /// buffer and the file name was read 4 bytes off.</summary>
    [Test]
    public void DirectoryItemInfo_MatchesTheEdsdk13Layout() {
        var t = typeof(NINA.Camera.CanonEdsdk.Native.EdsDirectoryItemInfo);
        Assert.Multiple(() => {
            Assert.That(System.Runtime.InteropServices.Marshal.SizeOf(t), Is.EqualTo(288));
            Assert.That((int)System.Runtime.InteropServices.Marshal.OffsetOf(t, "szFileName"), Is.EqualTo(20));
            Assert.That((int)System.Runtime.InteropServices.Marshal.OffsetOf(t, "Format"), Is.EqualTo(276));
        });
    }

    [Test]
    public async Task ARealCanon_TakesAPictureAndSendsItBack() {
        var dir = Environment.GetEnvironmentVariable("POLARIS_TEST_CANON_SDK_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !OperatingSystem.IsWindows())
            Assert.Ignore("Set POLARIS_TEST_CANON_SDK_DIR to the folder holding the 64-bit EDSDK DLLs, with a Canon plugged in.");

        // Step trace straight to a file: a native crash takes the test host
        // down before NUnit prints anything.
        var trace = Environment.GetEnvironmentVariable("POLARIS_TEST_CANON_TRACE");
        void T(string m) { if (!string.IsNullOrEmpty(trace)) File.AppendAllText(trace, $"{DateTime.Now:HH:mm:ss.fff} {m}{Environment.NewLine}"); }
        T("start");
        // The registry looks in <pack>/canon-edsdk; point the pack at the parent.
        Environment.SetEnvironmentVariable(NINA.Image.NativeLibs.NativeSdkProbe.EnvVar,
            Path.GetDirectoryName(dir!.TrimEnd('\\', '/')));
        CanonEdsdkRegistry.ResetProbe();
        Assert.That(CanonEdsdkRegistry.IsAvailable, Is.True, "EDSDK did not initialise");

        T("sdk available");
        var cams = CanonEdsdkDiscovery.Enumerate();
        T($"cameras: {cams.Count}");
        TestContext.Out.WriteLine($"cameras: {string.Join(", ", cams.Select(c => c.Model))}");
        if (cams.Count == 0) Assert.Ignore("No Canon camera found.");

        var cam = new CanonEdsdkCamera(cams[0].Id);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        T("connect...");
        await cam.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(30));
        T($"connected {cam.DeviceName}");
        TestContext.Out.WriteLine($"connected to {cam.DeviceName} in {sw.ElapsedMilliseconds} ms, ISO {cam.SelectedIso}");
        try {
            sw.Restart();
            T("capture start");
            var img = await cam.CaptureAsync(1.0).WaitAsync(TimeSpan.FromSeconds(90));
            T($"capture done {img.Properties.Width}x{img.Properties.Height}");
            TestContext.Out.WriteLine(
                $"picture back in {sw.ElapsedMilliseconds} ms: {img.Properties.Width}x{img.Properties.Height}, " +
                $"raw {(img as NINA.Image.ImageData.BaseImageData)?.RawFileBytes?.Length ?? 0} bytes " +
                $"({(img as NINA.Image.ImageData.BaseImageData)?.RawFileExtension})");
            var save = Environment.GetEnvironmentVariable("POLARIS_TEST_CANON_SAVE");
            if (!string.IsNullOrEmpty(save) && img is NINA.Image.ImageData.BaseImageData b && b.RawFileBytes != null)
                File.WriteAllBytes(save, b.RawFileBytes);
            Assert.That(img.Properties.Width, Is.GreaterThan(1), "no preview picture");
        } finally {
            T("disconnect");
            await cam.DisconnectAsync();
            T("disconnected");
        }
    }
}
