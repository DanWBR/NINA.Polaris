using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// Service tests that don't require xpra to be installed. Real xpra
/// session lifecycle is covered by integration tests on Linux only.
/// </summary>
[TestFixture]
public class Phd2GuiSessionServiceTests {

    private Phd2GuiSessionService MakeService(int displayNumber = 100, int bindPort = 14600) {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Phd2Gui:DisplayNumber"] = displayNumber.ToString(),
                ["Phd2Gui:BindPort"]      = bindPort.ToString(),
                ["Phd2Gui:AutoStart"]     = "false"
            })
            .Build();
        return new Phd2GuiSessionService(cfg, NullLogger<Phd2GuiSessionService>.Instance);
    }

    [Test]
    public void DisplayNumber_ReadsFromConfig() {
        var svc = MakeService(displayNumber: 200);
        Assert.That(svc.DisplayNumber, Is.EqualTo(200));
    }

    [Test]
    public void BindPort_ReadsFromConfig() {
        var svc = MakeService(bindPort: 17600);
        Assert.That(svc.BindPort, Is.EqualTo(17600));
    }

    [Test]
    public void IsSupportedOs_MatchesRuntimePlatform() {
        var svc = MakeService();
        var expected = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        Assert.That(svc.IsSupportedOs, Is.EqualTo(expected));
    }

    [Test]
    public async Task StartSessionAsync_OnUnsupportedOs_ReturnsFalseSetsLastError() {
        var svc = MakeService();
        if (svc.IsSupportedOs) {
            Assert.Ignore("Linux host, this test only meaningful on Win/Mac");
        }
        var ok = await svc.StartSessionAsync();
        Assert.That(ok, Is.False);
        Assert.That(svc.LastError, Is.EqualTo("OS not supported"));
    }

    [Test]
    public async Task StopSessionAsync_NoSessionRunning_ReturnsFalseQuietly() {
        var svc = MakeService();
        // On any OS: stop without start should just return false without throwing.
        var ok = await svc.StopSessionAsync();
        Assert.That(ok, Is.False, "Stop with no running session = no-op false");
    }

    [Test]
    public void InitialState_SessionNotRunning() {
        var svc = MakeService();
        Assert.That(svc.SessionRunning, Is.False);
        Assert.That(svc.LastHealthCheckAt, Is.Null);
    }
}
