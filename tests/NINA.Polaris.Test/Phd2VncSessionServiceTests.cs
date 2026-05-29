using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the OS-gating contract of Phd2VncSessionService. The actual
/// TightVNC integration is Windows-only and depends on a third-party
/// install + a Windows Service — those paths are smoke-tested in
/// PH2VNC-6 end-to-end verification, not here. These tests cover
/// the cross-platform surface: that the service compiles + runs on
/// any OS, that non-Windows hosts cleanly short-circuit with an
/// actionable UnsupportedReason, and that admin-required ops fail
/// fast with a friendly LastError instead of an unhandled exception.
/// </summary>
[TestFixture]
public class Phd2VncSessionServiceTests {

    private static Phd2VncSessionService MakeService(int port = 5900) {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Phd2Vnc:Port"] = port.ToString()
            })
            .Build();
        return new Phd2VncSessionService(config, NullLogger<Phd2VncSessionService>.Instance);
    }

    [Test]
    public void Constructor_HonoursConfiguredPort() {
        var svc = MakeService(port: 5901);
        Assert.That(svc.Port, Is.EqualTo(5901));
    }

    [Test]
    public void Constructor_DefaultsToStandardVncPort() {
        var config = new ConfigurationBuilder().Build();
        var svc = new Phd2VncSessionService(config, NullLogger<Phd2VncSessionService>.Instance);
        Assert.That(svc.Port, Is.EqualTo(5900));
    }

    [Test]
    public void IsSupportedOs_MatchesCurrentPlatform() {
        var svc = MakeService();
        Assert.That(svc.IsSupportedOs,
            Is.EqualTo(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)));
    }

    [Test]
    public void UnsupportedReason_OnNonWindows_IsActionable() {
        if (OperatingSystem.IsWindows()) {
            Assert.Ignore("Windows host has no UnsupportedReason.");
            return;
        }
        var svc = MakeService();
        Assert.That(svc.UnsupportedReason, Is.Not.Null);
        Assert.That(svc.UnsupportedReason!,
            Does.Contain("Windows").IgnoreCase,
            "Reason should explain that VNC path needs Windows.");
        Assert.That(svc.UnsupportedReason,
            Does.Contain("xpra").IgnoreCase,
            "Reason should point Linux users at the existing xpra path.");
    }

    [Test]
    public void UnsupportedReason_OnWindows_IsNull() {
        if (!OperatingSystem.IsWindows()) {
            Assert.Ignore("Non-Windows hosts always have an UnsupportedReason.");
            return;
        }
        var svc = MakeService();
        Assert.That(svc.UnsupportedReason, Is.Null);
    }

    [Test]
    public async Task RefreshDetectionAsync_OnNonWindows_Idempotent() {
        if (OperatingSystem.IsWindows()) {
            Assert.Ignore("This test pins the no-op behaviour on non-Windows.");
            return;
        }
        var svc = MakeService();
        // Should not throw, should not change any state from defaults.
        await svc.RefreshDetectionAsync();
        Assert.That(svc.TightVncInstalled, Is.False);
        Assert.That(svc.ServiceInstalled, Is.False);
        Assert.That(svc.ServiceRunning, Is.False);
    }

    [Test]
    public async Task StartServiceAsync_OnNonWindows_FailsWithActionableError() {
        if (OperatingSystem.IsWindows()) {
            Assert.Ignore("Windows host needs an actual TightVNC + admin context to test.");
            return;
        }
        var svc = MakeService();
        var ok = await svc.StartServiceAsync();
        Assert.That(ok, Is.False);
        Assert.That(svc.LastError, Is.Not.Null.And.Not.Empty,
            "LastError must explain why; the UI surfaces this string.");
    }

    [Test]
    public async Task StopServiceAsync_OnNonWindows_FailsWithActionableError() {
        if (OperatingSystem.IsWindows()) {
            Assert.Ignore("Windows host needs admin context to actually toggle the service.");
            return;
        }
        var svc = MakeService();
        var ok = await svc.StopServiceAsync();
        Assert.That(ok, Is.False);
        Assert.That(svc.LastError, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void ServiceName_IsCanonicalTightVncName() {
        // tvnserver is the public service name TightVNC's installer
        // registers — the bridge endpoint + UI banners rely on this
        // being stable, pin it.
        Assert.That(Phd2VncSessionService.ServiceName, Is.EqualTo("tvnserver"));
    }
}
