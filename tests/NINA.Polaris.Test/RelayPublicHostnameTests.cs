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

using NINA.Relay.Server;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Field report 2026-09-23: the relay card on a freshly connected rig showed
/// "Reachable at https://sv550/". The tunnel announces the tenant's hostname,
/// and tenants.json holds the bare label while the domain lives in
/// Proxy:HostnameSuffix, so what reached the operator was a link to nowhere.
/// The announcement has to be an address.
/// </summary>
[TestFixture]
public class RelayPublicHostnameTests {
    private const string Suffix = ".relay.polaris-astro.app.br";

    [Test]
    public void TheLabelGainsTheDeploymentSuffix() {
        Assert.That(TunnelHandler.PublicHostname("sv550", Suffix),
            Is.EqualTo("sv550.relay.polaris-astro.app.br"));
    }

    [Test]
    public void ASuffixWithoutItsLeadingDotStillJoinsCleanly() {
        Assert.That(TunnelHandler.PublicHostname("sv550", "relay.polaris-astro.app.br"),
            Is.EqualTo("sv550.relay.polaris-astro.app.br"));
    }

    [Test]
    public void AnAlreadyQualifiedHostnameIsLeftAlone() {
        // Some deployments put the whole name in tenants.json.
        Assert.That(TunnelHandler.PublicHostname("rig.example.com", Suffix),
            Is.EqualTo("rig.example.com"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void WithNoSuffixTheLabelIsTheName(string? suffix) {
        // A relay serving a single host has no suffix to add.
        Assert.That(TunnelHandler.PublicHostname("sv550", suffix), Is.EqualTo("sv550"));
    }

    [Test]
    public void TrailingDotsDoNotDoubleUp() {
        Assert.That(TunnelHandler.PublicHostname("sv550.", ".relay.example.com."),
            Is.EqualTo("sv550.relay.example.com"));
    }

    [TestCase(null)]
    [TestCase("")]
    public void AnEmptyHostnameStaysEmpty(string? hostname) {
        Assert.That(TunnelHandler.PublicHostname(hostname!, Suffix), Is.Empty);
    }
}
