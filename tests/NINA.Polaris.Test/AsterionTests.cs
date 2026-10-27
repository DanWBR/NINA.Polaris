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

using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;
using NINA.Polaris.Services.PlateSolving;

namespace NINA.Polaris.Test;

[TestFixture]
public class AsterionTests {
    private string _dir = null!;

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "polaris-asterion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ProfileService Profiles() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Profiles:Directory"] = _dir }).Build(),
        NullLogger<ProfileService>.Instance);

    [Test]
    public void EachSupportedHostGetsItsOwnArchive() {
        Assert.Multiple(() => {
            Assert.That(AsterionInstallService.AssetFor(OSPlatform.Linux, Architecture.Arm64),
                Is.EqualTo("asterion-" + AsterionInstallService.Version + "-linux-aarch_64.zip"));
            Assert.That(AsterionInstallService.AssetFor(OSPlatform.Linux, Architecture.X64), Does.EndWith("-linux-x86_64.zip"));
            Assert.That(AsterionInstallService.AssetFor(OSPlatform.Windows, Architecture.X64), Does.EndWith("-windows-x86_64.zip"));
            Assert.That(AsterionInstallService.AssetFor(OSPlatform.OSX, Architecture.Arm64), Does.EndWith("-osx-aarch_64.zip"));
            Assert.That(AsterionInstallService.AssetFor(OSPlatform.Linux, Architecture.Arm), Is.Null, "no 32-bit ARM build");
            Assert.That(AsterionInstallService.AssetFor(OSPlatform.Windows, Architecture.Arm64), Is.Null);
        });
    }

    /// <summary>Asterion's table, by the field's shorter side.</summary>
    [TestCase(2.4, "tycho2")]
    [TestCase(0.7, "tycho2")]
    [TestCase(0.5, "gaia-500")]
    [TestCase(0.15, "gaia-500")]
    [TestCase(0.12, "gaia-2000")]
    [TestCase(0.09, "gaia-3000")]
    [TestCase(0.05, "gaia-3000")]
    [TestCase(0.0, "tycho2")]
    public void TheSmallestCatalogThatSolvesTheFieldIsRecommended(double shortSideDeg, string expected) {
        Assert.That(AsterionInstallService.Recommend(shortSideDeg), Is.EqualTo(expected));
    }

    [Test]
    public void ChecksumsAreReadAsSha256sumWritesThem() {
        var text = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  asterion-0.3.1-linux-aarch_64.zip\n"
                 + "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210 *asterion-0.3.1-windows-x86_64.zip\r\n"
                 + "not a checksum line\n";
        var map = AsterionInstallService.ParseChecksums(text);
        Assert.Multiple(() => {
            Assert.That(map, Has.Count.EqualTo(2));
            Assert.That(map["asterion-0.3.1-linux-aarch_64.zip"], Does.StartWith("0123"));
            Assert.That(map["asterion-0.3.1-windows-x86_64.zip"], Does.StartWith("fedc"), "binary-mode marker dropped");
        });
    }

    /// <summary>The events --download-catalog --json prints, per Asterion's README.</summary>
    [Test]
    public void CatalogProgressEventsAreRead() {
        var step = AsterionInstallService.ParseProgress("""{"event":"step","step":"download","description":"Downloading tycho2","total":295000000,"unit":"bytes"}""");
        var prog = AsterionInstallService.ParseProgress("""{"event":"progress","step":"download","completed":1000,"total":4000,"unit":"bytes","fraction":0.25,"bytes":1000}""");
        var done = AsterionInstallService.ParseProgress("""{"event":"installed","catalog":"tycho2","file":"/x/tycho2.astx","size":378000000}""");
        Assert.Multiple(() => {
            Assert.That(step!.Event, Is.EqualTo("step"));
            Assert.That(step.Step, Is.EqualTo("download"));
            Assert.That(prog!.Fraction, Is.EqualTo(0.25));
            Assert.That(prog.Bytes, Is.EqualTo(1000));
            Assert.That(done!.Message, Does.Contain("tycho2"));
            Assert.That(AsterionInstallService.ParseProgress("plain text line"), Is.Null);
            Assert.That(AsterionInstallService.ParseProgress("{broken"), Is.Null);
            Assert.That(AsterionInstallService.ParseProgress("""{"no":"event"}"""), Is.Null);
        });
    }

    [Test]
    public void TheSolverIsReadyOnlyWithItsExecutableAndACatalog() {
        var install = new AsterionInstallService(Profiles(), NullLogger<AsterionInstallService>.Instance);
        var solver = new AsterionSolver(new ConfigurationBuilder().Build(), NullLogger<AstapSolver>.Instance, install);
        Assert.That(solver.Id, Is.EqualTo("asterion"));
        Assert.That(solver.SolverPath, Is.EqualTo(install.ExecutablePath));
        Assert.That(solver.IsAvailable, Is.False, "nothing installed");

        Directory.CreateDirectory(install.BinDir);
        File.WriteAllText(install.ExecutablePath, "");
        Assert.That(solver.IsAvailable, Is.False, "an executable without a catalog solves nothing (exit 32)");

        Directory.CreateDirectory(install.CatalogDir);
        File.WriteAllText(Path.Combine(install.CatalogDir, "tycho2.astx"), "");
        File.WriteAllText(Path.Combine(install.CatalogDir, "tycho2.astx.wide"), "");
        Assert.That(install.InstalledCatalogs(), Is.EqualTo(new[] { "tycho2" }), "the wide-field companion is not a catalog");
        Assert.That(solver.IsAvailable, Is.True);
    }

    /// <summary>The ASTAP command line is shared, so a solve through Asterion
    /// carries the same hints as one through ASTAP.</summary>
    [Test]
    public void AsterionTakesTheAstapCommandLine() {
        var install = new AsterionInstallService(Profiles(), NullLogger<AsterionInstallService>.Instance);
        var asterion = new AsterionSolver(new ConfigurationBuilder().Build(), NullLogger<AstapSolver>.Instance, install);
        var opts = new PlateSolveOptions { HintRa = 5.5, HintDec = -5.4, SearchRadiusDeg = 10, FovDeg = 1.2 };
        var args = asterion.BuildArgs("/tmp/x.fits", opts);
        Assert.That(args, Does.Contain("-ra 5.5").And.Contain("-spd 84.6").And.Contain("-r 10").And.Contain("-fov 1.2").And.Contain("-update"));
    }
}
