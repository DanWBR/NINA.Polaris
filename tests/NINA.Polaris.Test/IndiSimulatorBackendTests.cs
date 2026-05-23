using Microsoft.Extensions.Logging.Abstractions;
using NINA.Polaris.Services.Simulator;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Tests for the pure-function helpers in IndiSimulatorBackend. The
/// IO-heavy paths (DetectInstallAsync via real `which`, LaunchAsync
/// spawning indiserver) are smoke-tested manually — they hit real
/// processes and only pass on a Linux/macOS dev box with indi-bin
/// installed. The arg-builder + version parser cover the parts most
/// likely to break under refactoring.
/// </summary>
[TestFixture]
public class IndiSimulatorBackendTests {

    // --- ParseVersion ---
    [TestCase("INDI Server v2.1.4\n",                 "2.1.4")]
    [TestCase("indiserver version 2.0\n",             "2.0")]
    [TestCase("2.1.3.1 (build something)",            "2.1.3")]   // first 3 parts only
    [TestCase("no version here",                      null)]
    [TestCase("",                                     null)]
    [TestCase(null,                                   null)]
    public void ParseVersion_ExtractsFirstSemverLike(string? raw, string? expected) {
        Assert.That(IndiSimulatorBackend.ParseVersion(raw!), Is.EqualTo(expected));
    }

    // --- BuildArgs ---
    [Test]
    public void BuildArgs_DefaultDevices_BuildsExpectedArgv() {
        var req = new SimulatorLaunchRequest(SimulatorDeviceTags.Defaults, 7624);
        var args = IndiSimulatorBackend.BuildArgs(req);
        // Verbose flag, explicit port, then one binary per device,
        // in the exact order from the request. indiserver is order-
        // sensitive only for the @hostname prefix syntax which we
        // don't use.
        Assert.That(args, Is.EqualTo(
            "-v -p 7624 indi_simulator_ccd indi_simulator_telescope "
            + "indi_simulator_focus indi_simulator_wheel"));
    }

    [Test]
    public void BuildArgs_CustomPort_ReflectsInArgs() {
        var req = new SimulatorLaunchRequest([SimulatorDeviceTags.Ccd], 7625);
        Assert.That(IndiSimulatorBackend.BuildArgs(req), Does.Contain("-p 7625"));
    }

    [Test]
    public void BuildArgs_UnknownTag_IsSilentlyDropped() {
        // A stale UI checkbox could send "rotator" before the device
        // is actually wired up here. Drop unknowns instead of failing
        // launch — failing one driver shouldn't sink the whole stack.
        var req = new SimulatorLaunchRequest(
            ["ccd", "made-up-device", "telescope"], 7624);
        var args = IndiSimulatorBackend.BuildArgs(req);
        Assert.That(args, Does.Contain("indi_simulator_ccd"));
        Assert.That(args, Does.Contain("indi_simulator_telescope"));
        Assert.That(args, Does.Not.Contain("made-up"));
    }

    [Test]
    public void BuildArgs_AllSevenDevices_AllAppear() {
        var req = new SimulatorLaunchRequest(SimulatorDeviceTags.All, 7624);
        var args = IndiSimulatorBackend.BuildArgs(req);
        foreach (var binary in new[] {
            "indi_simulator_ccd",
            "indi_simulator_telescope",
            "indi_simulator_focus",
            "indi_simulator_wheel",
            "indi_simulator_guide",
            "indi_simulator_dome",
            "indi_simulator_weather"
        }) {
            Assert.That(args, Does.Contain(binary));
        }
    }

    // --- IsSupported gate ---
    [Test]
    public void IsSupported_TracksRuntimePlatform() {
        var backend = new IndiSimulatorBackend(NullLogger<IndiSimulatorBackend>.Instance);
        // No mocking — just confirm the property reflects reality.
        // CI matrix that runs on Linux + Windows pins both branches.
        var expected = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();
        Assert.That(backend.IsSupported, Is.EqualTo(expected));
    }

    [Test]
    public async Task DetectInstallAsync_OnUnsupportedOs_ReturnsCleanError() {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) {
            Assert.Ignore("This test pins the Windows/other-OS fallback path; skip on Linux/Mac.");
        }
        var backend = new IndiSimulatorBackend(NullLogger<IndiSimulatorBackend>.Instance);
        var install = await backend.DetectInstallAsync();
        Assert.That(install.Installed, Is.False);
        Assert.That(install.Error, Does.Contain("Linux"));
        Assert.That(install.AvailableDevices, Is.Empty);
    }

    [Test]
    public async Task LaunchAsync_OnUnsupportedOs_ReturnsFalseSilently() {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) {
            Assert.Ignore("Linux/Mac path is integration-tested with a real indiserver — see manual verify.");
        }
        var backend = new IndiSimulatorBackend(NullLogger<IndiSimulatorBackend>.Instance);
        var ok = await backend.LaunchAsync(new SimulatorLaunchRequest(["ccd"]));
        Assert.That(ok, Is.False);
    }

    // --- SimulatorDeviceTags consistency ---
    [Test]
    public void SimulatorDeviceTags_DefaultsAreSubsetOfAll() {
        foreach (var tag in SimulatorDeviceTags.Defaults) {
            Assert.That(SimulatorDeviceTags.All, Does.Contain(tag),
                $"Defaults references {tag} but it's not in All.");
        }
    }

    [Test]
    public void SimulatorDeviceTags_AllAreLowercase() {
        // The mapping in IndiSimulatorBackend uses
        // OrdinalIgnoreCase on lookups, but enforcing lowercase
        // tags here keeps JSON payloads / URL query parsing
        // predictable.
        foreach (var tag in SimulatorDeviceTags.All) {
            Assert.That(tag, Is.EqualTo(tag.ToLowerInvariant()),
                $"Tag {tag} should be lowercase.");
        }
    }
}
