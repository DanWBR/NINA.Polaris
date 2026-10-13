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

using System.IO.Compression;
using NUnit.Framework;
using NINA.Polaris.Services.External;

namespace NINA.Polaris.Test;

/// <summary>
/// Installing the Canon EDSDK from what the operator downloaded: the zip (which
/// carries a 32-bit and a 64-bit copy of each DLL) or the DLLs themselves.
/// </summary>
[TestFixture]
public class CanonEdsdkInstallerTests {
    private string _dir = "";

    [SetUp] public void SetUp() => _dir = Path.Combine(Path.GetTempPath(), "edsdk-test-" + Guid.NewGuid().ToString("N"));
    [TearDown] public void TearDown() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>The smallest bytes that read as a PE image of the given machine.</summary>
    private static byte[] FakePe(ushort machine, byte marker = 0) {
        var b = new byte[0x100];
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        BitConverter.GetBytes(0x40).CopyTo(b, 0x3C);
        b[0x40] = (byte)'P'; b[0x41] = (byte)'E';
        BitConverter.GetBytes(machine).CopyTo(b, 0x44);
        b[0xFF] = marker;
        return b;
    }

    private static byte[] Zip(params (string Path, byte[] Data)[] entries) {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) {
            foreach (var (p, d) in entries) {
                using var s = z.CreateEntry(p).Open();
                s.Write(d);
            }
        }
        return ms.ToArray();
    }

    [Test]
    public void PeMachine_ReadsTheHeader() {
        Assert.Multiple(() => {
            Assert.That(CanonEdsdkInstaller.PeMachine(FakePe(0x8664)), Is.EqualTo(0x8664));
            Assert.That(CanonEdsdkInstaller.Is64Bit(FakePe(0x8664)), Is.True);
            Assert.That(CanonEdsdkInstaller.Is64Bit(FakePe(0x14C)), Is.False);
            Assert.That(CanonEdsdkInstaller.PeMachine(new byte[] { 1, 2, 3 }), Is.EqualTo(0));
        });
    }

    [Test]
    public void TheCanonZip_InstallsThe64BitCopies() {
        var zip = Zip(
            ("EDSDK_v13.20.11_Windows/EDSDK/Dll/EDSDK.dll", FakePe(0x14C, 32)),
            ("EDSDK_v13.20.11_Windows/EDSDK/Dll/EdsImage.dll", FakePe(0x14C, 32)),
            ("EDSDK_v13.20.11_Windows/EDSDK_64/Dll/EDSDK.dll", FakePe(0x8664, 64)),
            ("EDSDK_v13.20.11_Windows/EDSDK_64/Dll/EdsImage.dll", FakePe(0x8664, 64)),
            ("EDSDK_v13.20.11_Windows/EDSDK_API_EN.pdf", new byte[] { 1 }));

        var r = CanonEdsdkInstaller.Install(new[] { ("EDSDK_v13.20.11_Windows.zip", zip) }, _dir);

        Assert.That(r.Ok, Is.True, r.Message);
        foreach (var name in CanonEdsdkInstaller.Required) {
            var data = File.ReadAllBytes(Path.Combine(_dir, name));
            Assert.That(data[0xFF], Is.EqualTo(64), $"{name} must be the 64-bit copy");
        }
    }

    [Test]
    public void OnlyThe32BitDlls_AreRefusedWithTheFolderToUse() {
        var r = CanonEdsdkInstaller.Install(new[] {
            ("EDSDK.dll", FakePe(0x14C)), ("EdsImage.dll", FakePe(0x14C))
        }, _dir);
        Assert.That(r.Ok, Is.False);
        Assert.That(r.Message, Does.Contain("32-bit").And.Contain("EDSDK_64"));
        Assert.That(Directory.Exists(_dir), Is.False, "nothing written on a refusal");
    }

    [Test]
    public void LooseDlls_AreInstalled() {
        var r = CanonEdsdkInstaller.Install(new[] {
            ("EDSDK.dll", FakePe(0x8664)), ("EdsImage.dll", FakePe(0x8664))
        }, _dir);
        Assert.That(r.Ok, Is.True, r.Message);
        Assert.That(r.Installed, Has.Count.EqualTo(2));
    }

    [Test]
    public void AMissingDll_IsNamed() {
        var r = CanonEdsdkInstaller.Install(new[] { ("EDSDK.dll", FakePe(0x8664)) }, _dir);
        Assert.That(r.Ok, Is.False);
        Assert.That(r.Message, Does.Contain("EdsImage.dll"));
    }

    [Test]
    public void NotAZip_SaysSo() {
        var r = CanonEdsdkInstaller.Install(new[] { ("sdk.zip", new byte[] { 1, 2, 3, 4 }) }, _dir);
        Assert.That(r.Ok, Is.False);
        Assert.That(r.Message, Does.Contain("zip"));
    }

    /// <summary>End to end against a real EDSDK, when one is pointed at:
    /// install it into a native-SDK folder and let the Canon backend find and
    /// initialise it from there. POLARIS_TEST_EDSDK_DIR is the extracted SDK
    /// folder (the one holding EDSDK_64).</summary>
    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ARealEdsdk_IsFoundAndInitialisedFromTheSdkFolder() {
        var src = Environment.GetEnvironmentVariable("POLARIS_TEST_EDSDK_DIR");
        if (string.IsNullOrWhiteSpace(src) || !OperatingSystem.IsWindows())
            Assert.Ignore("Set POLARIS_TEST_EDSDK_DIR to an extracted Canon EDSDK on Windows.");

        var uploads = Directory.GetFiles(src!, "*.dll", SearchOption.AllDirectories)
            .Select(f => (Path.GetFileName(f), File.ReadAllBytes(f)));
        var pack = Path.Combine(_dir, "native-sdks");
        var r = CanonEdsdkInstaller.Install(uploads,
            Path.Combine(pack, NINA.Camera.CanonEdsdk.CanonEdsdkRegistry.PackFolder));
        Assert.That(r.Ok, Is.True, r.Message);

        var saved = Environment.GetEnvironmentVariable(NINA.Image.NativeLibs.NativeSdkProbe.EnvVar);
        Environment.SetEnvironmentVariable(NINA.Image.NativeLibs.NativeSdkProbe.EnvVar, pack);
        try {
            NINA.Camera.CanonEdsdk.CanonEdsdkRegistry.ResetProbe();
            Assert.That(NINA.Camera.CanonEdsdk.CanonEdsdkRegistry.IsAvailable, Is.True);
            Assert.That(NINA.Camera.CanonEdsdk.CanonEdsdkRegistry.LoadedFrom,
                Is.EqualTo(Path.Combine(pack, NINA.Camera.CanonEdsdk.CanonEdsdkRegistry.PackFolder)));
        } finally {
            Environment.SetEnvironmentVariable(NINA.Image.NativeLibs.NativeSdkProbe.EnvVar, saved);
        }
    }
}
