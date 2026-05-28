using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>Unit tests for the pure parsers + validators of
/// <see cref="NetworkManagerService"/>. The nmcli subprocess paths
/// (SwitchToStationAsync etc.) need a real Linux host with
/// NetworkManager and are exercised in the WIFI-6 end-to-end Pi run,
/// not here.</summary>
[TestFixture]
public class NetworkManagerServiceTests {

    // ----- SplitNmcliTerse -----

    [Test]
    public void SplitNmcliTerse_PlainFields_SplitsOnColon() {
        var fields = NetworkManagerService.SplitNmcliTerse("wlan0:wifi:connected:Polaris-Hotspot");
        Assert.That(fields, Is.EqualTo(new[] { "wlan0", "wifi", "connected", "Polaris-Hotspot" }));
    }

    [Test]
    public void SplitNmcliTerse_EscapedColonInSsid_KeptInsideField() {
        // nmcli -t escapes a literal ':' as '\:' inside field values
        var fields = NetworkManagerService.SplitNmcliTerse(@"*:My\:Network:84:Infra");
        Assert.That(fields.Length, Is.EqualTo(4));
        Assert.That(fields[0], Is.EqualTo("*"));
        Assert.That(fields[1], Is.EqualTo("My:Network"));
        Assert.That(fields[2], Is.EqualTo("84"));
        Assert.That(fields[3], Is.EqualTo("Infra"));
    }

    [Test]
    public void SplitNmcliTerse_EscapedBackslash_KeptInsideField() {
        // nmcli also escapes backslashes
        var fields = NetworkManagerService.SplitNmcliTerse(@"a\\b:c");
        Assert.That(fields, Is.EqualTo(new[] { @"a\b", "c" }));
    }

    [Test]
    public void SplitNmcliTerse_EmptyTrailingField_KeptAsEmpty() {
        // Hidden SSIDs come back as empty between colons
        var fields = NetworkManagerService.SplitNmcliTerse(":50:WPA2:");
        Assert.That(fields, Is.EqualTo(new[] { "", "50", "WPA2", "" }));
    }

    // ----- ParseFirstIp4 -----

    [Test]
    public void ParseFirstIp4_StandardOutput_ReturnsAddressWithoutMask() {
        var input = "IP4.ADDRESS[1]:10.42.0.1/24\nIP4.GATEWAY:10.42.0.1\n";
        Assert.That(NetworkManagerService.ParseFirstIp4(input), Is.EqualTo("10.42.0.1"));
    }

    [Test]
    public void ParseFirstIp4_NoIp_ReturnsNull() {
        var input = "IP4.GATEWAY:--\nIP6.ADDRESS[1]:fe80::1/64\n";
        Assert.That(NetworkManagerService.ParseFirstIp4(input), Is.Null);
    }

    [Test]
    public void ParseFirstIp4_FirstOfMultiple_Wins() {
        var input = "IP4.ADDRESS[1]:192.168.1.42/24\nIP4.ADDRESS[2]:10.0.0.5/8\n";
        Assert.That(NetworkManagerService.ParseFirstIp4(input), Is.EqualTo("192.168.1.42"));
    }

    // ----- ParseGateway -----

    [Test]
    public void ParseGateway_HasGateway_ReturnsIp() {
        var input = "IP4.ADDRESS[1]:192.168.1.42/24\nIP4.GATEWAY:192.168.1.1\n";
        Assert.That(NetworkManagerService.ParseGateway(input), Is.EqualTo("192.168.1.1"));
    }

    [Test]
    public void ParseGateway_DashDash_ReturnsNull() {
        var input = "IP4.ADDRESS[1]:10.42.0.1/24\nIP4.GATEWAY:--\n";
        Assert.That(NetworkManagerService.ParseGateway(input), Is.Null);
    }

    // ----- ValidateSsidPsk -----

    [Test]
    public void ValidateSsidPsk_HappyPath_ReturnsNull() {
        Assert.That(NetworkManagerService.ValidateSsidPsk("HomeNet", "secret-pass-1"), Is.Null);
    }

    [Test]
    public void ValidateSsidPsk_EmptySsid_ReportsError() {
        Assert.That(NetworkManagerService.ValidateSsidPsk("", "secret-pass-1"),
            Does.Contain("SSID is required"));
    }

    [Test]
    public void ValidateSsidPsk_SsidTooLong_ReportsError() {
        Assert.That(NetworkManagerService.ValidateSsidPsk(new string('x', 33), "secret-pass-1"),
            Does.Contain("32 characters"));
    }

    [Test]
    public void ValidateSsidPsk_PskTooShort_ReportsError() {
        Assert.That(NetworkManagerService.ValidateSsidPsk("HomeNet", "1234567"),
            Does.Contain("8 to 63"));
    }

    [Test]
    public void ValidateSsidPsk_PskTooLong_ReportsError() {
        Assert.That(NetworkManagerService.ValidateSsidPsk("HomeNet", new string('x', 64)),
            Does.Contain("8 to 63"));
    }
}
