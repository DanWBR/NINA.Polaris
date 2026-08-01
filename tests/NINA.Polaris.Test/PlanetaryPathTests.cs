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
using NINA.Polaris.Endpoints;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// PLANPATH. Planetary clips used to be written to planetary/ at the capture
/// root, outside the per-rig tree every other output uses, so two rigs piled
/// their recordings into one folder. These cover the new path and, just as
/// importantly, that the rig can still be read back off a path -- including for
/// the legacy layout, which nothing is allowed to lose.
/// </summary>
[TestFixture]
public class PlanetaryPathTests {

    private static string Sep(string p) =>
        p.Replace('/', Path.DirectorySeparatorChar);

    [Test]
    public void RecordingsGoUnderTheRig() {
        Assert.That(ImageWriterService.BuildPlanetarySubDir("EdgeHD", "Jupiter"),
            Is.EqualTo(Sep("EdgeHD/planetary/Jupiter")));
    }

    [Test]
    public void WithoutATargetItIsJustTheRigsPlanetaryFolder() {
        Assert.That(ImageWriterService.BuildPlanetarySubDir("EdgeHD"),
            Is.EqualTo(Sep("EdgeHD/planetary")));
        Assert.That(ImageWriterService.BuildPlanetarySubDir("EdgeHD", "  "),
            Is.EqualTo(Sep("EdgeHD/planetary")));
    }

    /// <summary>Same sanitising as every other capture folder: a rig called
    /// "SV503 80ED" must not produce a path with a space in it, and a target
    /// with a slash must not create a directory level nobody asked for.
    /// </summary>
    [Test]
    public void RigAndTargetAreSanitisedLikeEveryOtherFolder() {
        var p = ImageWriterService.BuildPlanetarySubDir("SV503 80ED", "Mars/2026");
        Assert.That(p, Is.EqualTo(Sep("SV503_80ED/planetary/Mars_2026")));
        Assert.That(p.Split(Path.DirectorySeparatorChar).Length, Is.EqualTo(3),
            "a slash in the target must not add a directory level");
    }

    [TestCase("")]
    [TestCase(null)]
    public void AMissingRigNameFallsBackToDefault(string? rig) {
        Assert.That(ImageWriterService.BuildPlanetarySubDir(rig!, "Saturn"),
            Is.EqualTo(Sep("Default/planetary/Saturn")));
    }

    // ---- reading the rig back off a path ----

    private static string RigOf(string outDir, string filePath) {
        var m = typeof(VideoEndpoints).GetMethod("RigOfRecording",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)m.Invoke(null, new object?[] { outDir, filePath })!;
    }

    [Test]
    public void TheRigIsReadBackFromANewLayoutPath() {
        var root = Sep("/home/polaris/files");
        var clip = Path.Combine(root, Sep("EdgeHD/planetary/Jupiter/2026-08-01T22-10-05.ser"));
        Assert.That(RigOf(root, clip), Is.EqualTo("EdgeHD"));
    }

    /// <summary>A clip in the legacy tree genuinely does not know which rig
    /// shot it, and must say so rather than claiming "planetary" was a rig.
    /// </summary>
    [Test]
    public void ALegacyClipReportsNoRig() {
        var root = Sep("/home/polaris/files");
        var clip = Path.Combine(root, Sep("planetary/Jupiter/2026-07-26T20-25-00.ser"));
        Assert.That(RigOf(root, clip), Is.Empty);
    }

    /// <summary>The pathological case the listing has to survive: a rig
    /// actually named "planetary". Its clips sit at
    /// {root}/planetary/planetary/... and the first segment is the rig.
    /// </summary>
    [Test]
    public void ARigNamedPlanetaryIsStillDistinguishable() {
        var root = Sep("/home/polaris/files");
        var legacy = Path.Combine(root, Sep("planetary/Mars/a.ser"));
        var rigged = Path.Combine(root, Sep("planetary/planetary/Mars/a.ser"));
        Assert.That(RigOf(root, legacy), Is.Empty);
        Assert.That(RigOf(root, rigged), Is.EqualTo("planetary"));
    }

    [Test]
    public void APathOutsideTheCaptureRootReportsNoRig() {
        Assert.That(RigOf(Sep("/home/polaris/files"), Sep("/tmp/loose.ser")), Is.Empty);
    }
}
