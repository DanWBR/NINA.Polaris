// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.

using System;
using System.IO;
using System.Linq;
using NINA.Image.NativeLibs;
using NINA.Polaris.Services.External;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class NativeSdkProbeTests {
    [TearDown]
    public void Clear() => Environment.SetEnvironmentVariable(NativeSdkProbe.EnvVar, null);

    [Test]
    public void Dirs_WithoutEnv_IsBaseDirectoryOnly() {
        Environment.SetEnvironmentVariable(NativeSdkProbe.EnvVar, null);
        var dirs = NativeSdkProbe.Dirs().ToList();
        Assert.That(dirs, Has.Count.EqualTo(1));
        Assert.That(dirs[0], Is.EqualTo(AppContext.BaseDirectory));
    }

    [Test]
    public void Dirs_WithEnv_AppendsThePackDir() {
        var packDir = Path.Combine(Path.GetTempPath(), "polaris-sdk-probe");
        Environment.SetEnvironmentVariable(NativeSdkProbe.EnvVar, packDir);
        var dirs = NativeSdkProbe.Dirs().ToList();
        Assert.That(dirs, Has.Count.EqualTo(2));
        Assert.That(dirs[0], Is.EqualTo(AppContext.BaseDirectory), "base dir must be probed first");
        Assert.That(dirs[1], Is.EqualTo(packDir));
    }

    [Test]
    public void PackDir_IsUnderDataDir() {
        var dir = CameraSdkPackService.PackDir("/home/polaris/.local/share/NINA.Polaris/profiles");
        Assert.That(dir, Is.EqualTo(Path.Combine(
            "/home/polaris/.local/share/NINA.Polaris/profiles", "native-sdks")));
    }
}
