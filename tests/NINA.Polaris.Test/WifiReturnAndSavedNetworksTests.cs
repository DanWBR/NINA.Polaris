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

using System;
using System.IO;
using System.Runtime.CompilerServices;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Rigs on a router that runs off a power bank (field, 2026-10-10): every time
/// the power bank was swapped, the rigs went to their hotspot and never came
/// back to the router. NetworkManager brought the hotspot up on its own, so
/// the watchdog never knew it was a fallback; a tablet on the hotspot also
/// postponed the check indefinitely; and the reconnect tried to join with the
/// hotspot still beaconing. Separately, Polaris kept a single network: every
/// join deleted the previous one and its password.
/// </summary>
[TestFixture]
public class WifiReturnAndSavedNetworksTests {

    // ---- which hotspot is a fallback ----

    [Test]
    public void AHotspotNobodyChose_IsAFallback() {
        Assert.That(NetworkManagerService.HotspotIsFallback(fallbackEngaged: false, userChoseHotspot: false), Is.True,
            "NetworkManager's own autoconnect put the AP up: the rig must still go back");
    }

    [Test]
    public void AHotspotTheOperatorChose_StaysPut() {
        Assert.That(NetworkManagerService.HotspotIsFallback(fallbackEngaged: false, userChoseHotspot: true), Is.False);
    }

    [Test]
    public void TheWatchdogsOwnFallback_IsAFallback() {
        Assert.That(NetworkManagerService.HotspotIsFallback(fallbackEngaged: true, userChoseHotspot: true), Is.True);
    }

    [Test]
    public void ANetworkManagerHotspot_PassesTheReconnectGate() {
        var now = new DateTime(2026, 10, 10, 22, 0, 0, DateTimeKind.Utc);
        Assert.That(NetworkManagerService.ShouldAttemptStationReconnect(
            enabled: true,
            hotspotFallbackEngaged: NetworkManagerService.HotspotIsFallback(false, false),
            WifiMode.Hotspot,
            now, now.AddMinutes(-2), TimeSpan.FromSeconds(60), DateTime.MinValue), Is.True);
    }

    // ---- one profile per network ----

    [Test]
    public void EachNetworkGetsItsOwnProfile() {
        Assert.Multiple(() => {
            Assert.That(NetworkManagerService.StationProfileName("Medeiros_Plus"), Is.EqualTo("polaris-wifi-Medeiros_Plus"));
            Assert.That(NetworkManagerService.StationProfileName("Starlink"),
                Is.Not.EqualTo(NetworkManagerService.StationProfileName("Medeiros_Plus")));
        });
    }

    // ---- the service source: order of operations that matter ----

    private static string Here([CallerFilePath] string p = "") => p;

    private static string Src(params string[] parts) {
        var all = new System.Collections.Generic.List<string> { Path.GetDirectoryName(Here())!, "..", "..", "src" };
        all.AddRange(parts);
        var path = Path.GetFullPath(Path.Combine(all.ToArray()));
        Assert.That(File.Exists(path), $"missing {path}");
        return File.ReadAllText(path);
    }

    private static string Service() => Src("NINA.Polaris", "Services", "NetworkManagerService.cs");

    [Test]
    public void TheAutomaticReturnTakesTheHotspotDownBeforeJoining() {
        var src = Service();
        var method = src.IndexOf("private async Task TryReactivateStationAsync", StringComparison.Ordinal);
        Assert.That(method, Is.GreaterThanOrEqualTo(0));
        var apDown = src.IndexOf("\"connection down polaris-hotspot\"", method, StringComparison.Ordinal);
        var up = src.IndexOf("$\"connection up {Shell(connName)}\"", method, StringComparison.Ordinal);
        Assert.That(apDown, Is.GreaterThan(method));
        Assert.That(up, Is.GreaterThan(apDown), "the AP has to go down first, as in the manual switch");
    }

    [Test]
    public void AClientOnTheHotspotNoLongerStopsTheCheck() {
        var src = Service();
        Assert.Multiple(() => {
            Assert.That(src, Does.Not.Contain("_lastStationRetryAt = now;   // re-check a full retry window later"),
                "the early return that skipped the whole check while a client was attached");
            Assert.That(src, Does.Contain("await ScanAsync(false, ct, cachedOnly: true)"),
                "with a client attached it reads the cached scan instead of skipping");
        });
    }

    [Test]
    public void JoiningANetworkNoLongerDeletesTheOthers() {
        var src = Service();
        var method = src.IndexOf("public async Task<SwitchResult> SwitchToStationAsync", StringComparison.Ordinal);
        Assert.That(method, Is.GreaterThanOrEqualTo(0));
        var body = src.Substring(method, 4000);
        Assert.Multiple(() => {
            Assert.That(body, Does.Not.Contain("\"connection delete polaris-station\""));
            Assert.That(body, Does.Contain("string.Equals(c.Ssid, ssid, StringComparison.Ordinal)"),
                "only the profile of the network being joined is replaced");
        });
    }

    [Test]
    public void TheOperatorsChoiceOfHotspotIsRemembered() {
        var src = Service();
        Assert.Multiple(() => {
            Assert.That(src, Does.Contain("SetUserChoseHotspot(true);"));
            Assert.That(src, Does.Contain("SetUserChoseHotspot(false);"));
        });
    }

    [Test]
    public void SavedNetworksCanBeListedRejoinedAndForgotten() {
        var endpoints = Src("NINA.Polaris", "Endpoints", "NetworkEndpoints.cs");
        Assert.Multiple(() => {
            Assert.That(endpoints, Does.Contain("group.MapGet(\"/saved\""));
            Assert.That(endpoints, Does.Contain("group.MapPost(\"/saved/forget\""));
            Assert.That(endpoints, Does.Contain("public record StationRequest(string Ssid, string? Password);"),
                "the password is optional for a saved network");
        });
    }
}
