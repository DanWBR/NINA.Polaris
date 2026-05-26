using Microsoft.Extensions.Logging.Abstractions;
using NINA.Polaris.Services.Simulator;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Tests for the pure-function helpers in IndiSimulatorBackend. The
/// IO-heavy paths (DetectInstallAsync via real `which`, LaunchAsync
/// spawning indiserver) are smoke-tested manually, they hit real
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

    // --- BuildArgs (SIM-8: FIFO mode, drivers no longer in argv) ---
    [Test]
    public void BuildArgs_UsesFifoPath_NotDriverList() {
        // SIM-8 switched indiserver to runtime driver control via
        // FIFO. The argv carries the FIFO path; drivers are started
        // afterwards via FIFO `start` commands. This test pins the
        // wire format so a regression to argv-based driver passing
        // is caught immediately.
        var req = new SimulatorLaunchRequest(SimulatorDeviceTags.Defaults, 7624);
        var args = IndiSimulatorBackend.BuildArgs(req, "/tmp/polaris-indi-test.fifo");
        Assert.That(args, Is.EqualTo("-v -p 7624 -f /tmp/polaris-indi-test.fifo"));
    }

    [Test]
    public void BuildArgs_CustomPort_ReflectsInArgs() {
        var req = new SimulatorLaunchRequest([SimulatorDeviceTags.Ccd], 7625);
        Assert.That(IndiSimulatorBackend.BuildArgs(req, "/tmp/x"), Does.Contain("-p 7625"));
    }

    // --- BuildFifoCommand (SIM-8) ---
    [TestCase("ccd",       true,  "start indi_simulator_ccd")]
    [TestCase("telescope", true,  "start indi_simulator_telescope")]
    [TestCase("focus",     false, "stop indi_simulator_focus")]
    [TestCase("wheel",     false, "stop indi_simulator_wheel")]
    [TestCase("guide",     true,  "start indi_simulator_guide")]
    [TestCase("dome",      true,  "start indi_simulator_dome")]
    [TestCase("weather",   true,  "start indi_simulator_weather")]
    public void BuildFifoCommand_KnownDevices_RendersCorrectVerb(
            string device, bool start, string expected) {
        Assert.That(IndiSimulatorBackend.BuildFifoCommand(device, start),
            Is.EqualTo(expected));
    }

    [Test]
    public void BuildFifoCommand_UnknownDevice_ReturnsNull() {
        // Caller (AddDeviceAsync / RemoveDeviceAsync) checks for
        // null and bails, better than sending indiserver a bogus
        // command and surfacing a confusing error.
        Assert.That(IndiSimulatorBackend.BuildFifoCommand("made-up", true),
            Is.Null);
    }

    // --- IsSupported gate ---
    [Test]
    public void IsSupported_TracksRuntimePlatform() {
        var backend = new IndiSimulatorBackend(NullLogger<IndiSimulatorBackend>.Instance);
        // No mocking, just confirm the property reflects reality.
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
            Assert.Ignore("Linux/Mac path is integration-tested with a real indiserver, see manual verify.");
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
