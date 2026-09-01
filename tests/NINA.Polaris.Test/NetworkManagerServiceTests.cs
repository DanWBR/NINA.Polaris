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

    // ----- ShouldEngageHotspotFallback (auto AP fallback watchdog) -----

    private static readonly DateTime Now = new(2026, 6, 6, 22, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(45);

    [Test]
    public void Fallback_DisconnectedPastGrace_Engages() {
        // Carried to a new house: no saved network in range, link down
        // for longer than the grace window => bring the AP up.
        var since = Now - TimeSpan.FromSeconds(60);
        Assert.That(NetworkManagerService.ShouldEngageHotspotFallback(
            WifiMode.Disconnected, since, Now, Grace, enabled: true, suppressUntil: DateTime.MinValue),
            Is.True);
    }

    [Test]
    public void Fallback_DisconnectedWithinGrace_WaitsItOut() {
        // Still inside the grace window (e.g. NM mid-association) => do
        // not yank the link to the AP yet.
        var since = Now - TimeSpan.FromSeconds(10);
        Assert.That(NetworkManagerService.ShouldEngageHotspotFallback(
            WifiMode.Disconnected, since, Now, Grace, enabled: true, suppressUntil: DateTime.MinValue),
            Is.False);
    }

    [Test]
    public void Fallback_StationConnected_NeverEngages() {
        var since = Now - TimeSpan.FromSeconds(600);
        Assert.That(NetworkManagerService.ShouldEngageHotspotFallback(
            WifiMode.Station, since, Now, Grace, enabled: true, suppressUntil: DateTime.MinValue),
            Is.False);
    }

    [Test]
    public void Fallback_AlreadyHotspot_NeverEngages() {
        var since = Now - TimeSpan.FromSeconds(600);
        Assert.That(NetworkManagerService.ShouldEngageHotspotFallback(
            WifiMode.Hotspot, since, Now, Grace, enabled: true, suppressUntil: DateTime.MinValue),
            Is.False);
    }

    [Test]
    public void Fallback_Disabled_NeverEngages() {
        var since = Now - TimeSpan.FromSeconds(600);
        Assert.That(NetworkManagerService.ShouldEngageHotspotFallback(
            WifiMode.Disconnected, since, Now, Grace, enabled: false, suppressUntil: DateTime.MinValue),
            Is.False);
    }

    [Test]
    public void Fallback_SuppressedByManualSwitch_DoesNotEngage() {
        // A manual SwitchToStation is mid-flight (suppress window still
        // open) => the watchdog must stay out of the way even though the
        // link transiently reports Disconnected past the grace window.
        var since = Now - TimeSpan.FromSeconds(600);
        var suppressUntil = Now + TimeSpan.FromSeconds(20);
        Assert.That(NetworkManagerService.ShouldEngageHotspotFallback(
            WifiMode.Disconnected, since, Now, Grace, enabled: true, suppressUntil: suppressUntil),
            Is.False);
    }

    [Test]
    public void Fallback_NoDisconnectTimer_DoesNotEngage() {
        Assert.That(NetworkManagerService.ShouldEngageHotspotFallback(
            WifiMode.Disconnected, null, Now, Grace, enabled: true, suppressUntil: DateTime.MinValue),
            Is.False);
    }

    // ----- ShouldAttemptStationReconnect (auto hotspot -> station watchdog) -----

    private static readonly TimeSpan RetryGrace = TimeSpan.FromSeconds(60);

    [Test]
    public void Reconnect_AutoFallbackAp_PastCadence_Attempts() {
        // We are on the AP only because the station was unreachable; a full
        // retry interval has elapsed => probe for the station coming back.
        var lastRetry = Now - TimeSpan.FromSeconds(90);
        Assert.That(NetworkManagerService.ShouldAttemptStationReconnect(
            enabled: true, hotspotFallbackEngaged: true, WifiMode.Hotspot,
            Now, lastRetry, RetryGrace, suppressUntil: DateTime.MinValue),
            Is.True);
    }

    [Test]
    public void Reconnect_WithinCadence_WaitsItOut() {
        // Retried recently => do not blip the radio again yet.
        var lastRetry = Now - TimeSpan.FromSeconds(15);
        Assert.That(NetworkManagerService.ShouldAttemptStationReconnect(
            enabled: true, hotspotFallbackEngaged: true, WifiMode.Hotspot,
            Now, lastRetry, RetryGrace, suppressUntil: DateTime.MinValue),
            Is.False);
    }

    [Test]
    public void Reconnect_UserChoseHotspot_NeverAttempts() {
        // Hotspot up by the user's explicit choice (fallback flag clear) =>
        // never yank them back to station.
        var lastRetry = Now - TimeSpan.FromSeconds(600);
        Assert.That(NetworkManagerService.ShouldAttemptStationReconnect(
            enabled: true, hotspotFallbackEngaged: false, WifiMode.Hotspot,
            Now, lastRetry, RetryGrace, suppressUntil: DateTime.MinValue),
            Is.False);
    }

    [Test]
    public void Reconnect_AlreadyOnStation_NeverAttempts() {
        var lastRetry = Now - TimeSpan.FromSeconds(600);
        Assert.That(NetworkManagerService.ShouldAttemptStationReconnect(
            enabled: true, hotspotFallbackEngaged: true, WifiMode.Station,
            Now, lastRetry, RetryGrace, suppressUntil: DateTime.MinValue),
            Is.False);
    }

    [Test]
    public void Reconnect_Disabled_NeverAttempts() {
        var lastRetry = Now - TimeSpan.FromSeconds(600);
        Assert.That(NetworkManagerService.ShouldAttemptStationReconnect(
            enabled: false, hotspotFallbackEngaged: true, WifiMode.Hotspot,
            Now, lastRetry, RetryGrace, suppressUntil: DateTime.MinValue),
            Is.False);
    }

    [Test]
    public void Reconnect_SuppressedByManualSwitch_DoesNotAttempt() {
        var lastRetry = Now - TimeSpan.FromSeconds(600);
        var suppressUntil = Now + TimeSpan.FromSeconds(20);
        Assert.That(NetworkManagerService.ShouldAttemptStationReconnect(
            enabled: true, hotspotFallbackEngaged: true, WifiMode.Hotspot,
            Now, lastRetry, RetryGrace, suppressUntil: suppressUntil),
            Is.False);
    }

    // ----- HasAssociatedStation (iw station dump parsing) -----
    // The reconnect watchdog must not scan or blip the radio while somebody
    // is associated with the hotspot: the off-channel scan alone kicks every
    // client (field reports: tablet and laptop dropping every ~30-60s).

    [Test]
    public void IwDump_WithAssociatedClient_IsDetected() {
        // Real `iw dev wlan0 station dump` shape: one block per peer.
        const string dump =
            "Station aa:bb:cc:dd:ee:ff (on wlan0)\n" +
            "\tinactive time:\t80 ms\n" +
            "\trx bytes:\t180234\n" +
            "\ttx bytes:\t922144\n" +
            "\tsignal:  \t-44 dBm\n";
        Assert.That(NetworkManagerService.HasAssociatedStation(dump), Is.True);
    }

    [Test]
    public void IwDump_TwoClients_IsDetected() {
        const string dump =
            "Station aa:bb:cc:dd:ee:ff (on wlan0)\n\tsignal: -44 dBm\n" +
            "Station 11:22:33:44:55:66 (on wlan0)\n\tsignal: -60 dBm\n";
        Assert.That(NetworkManagerService.HasAssociatedStation(dump), Is.True);
    }

    [Test]
    public void IwDump_IdleAp_NoClients() {
        // An idle AP prints nothing at all.
        Assert.That(NetworkManagerService.HasAssociatedStation(""), Is.False);
        Assert.That(NetworkManagerService.HasAssociatedStation("   \n"), Is.False);
    }

    [Test]
    public void IwDump_UnrelatedOutput_NoClients() {
        // Defensive: an error message or unexpected text must not read as a
        // client, or the watchdog would silently stop reconnecting forever.
        Assert.That(NetworkManagerService.HasAssociatedStation(
            "command failed: No such device (-19)\n"), Is.False);
    }
}