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

using Microsoft.Extensions.Configuration;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// mDNS advertised polaris-app-{shortId}.local so cloned SD cards would not
/// collide, and the certificate carried a hardcoded polaris-app.local. The name
/// a client was handed by discovery was therefore never in the cert, so every
/// connection made the way the app tells people to make it failed validation.
///
/// <para>These tests exist to keep the advertised name and the certified name
/// the same thing, which is only true while both come from here.</para>
/// </summary>
[TestFixture]
public class DeviceIdentityTests {

    private static IConfiguration Config(params (string k, string v)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p =>
                new KeyValuePair<string, string?>(p.k, p.v)))
            .Build();

    [Test]
    public void TheAdvertisedNameIsAmongTheCertifiedNames() {
        var cfg = Config();
        var advertised = DeviceIdentity.InstanceName(cfg);
        var certified = DeviceIdentity.DnsNames(cfg).ToList();

        Assert.That(certified, Contains.Item(advertised),
            "the bare instance name has to be certifiable");
        Assert.That(certified, Contains.Item(advertised + ".local"),
            "this is the name mDNS discovery actually hands to a client");
    }

    /// <summary>An operator who pins Mdns:InstanceName must not thereby lock
    /// themselves out: the cert has to follow the override.</summary>
    [Test]
    public void AnInstanceNameOverrideReachesTheCertificate() {
        var cfg = Config(("Mdns:InstanceName", "telescope-balcony"));

        Assert.That(DeviceIdentity.InstanceName(cfg), Is.EqualTo("telescope-balcony"));
        var names = DeviceIdentity.DnsNames(cfg).ToList();
        Assert.That(names, Contains.Item("telescope-balcony"));
        Assert.That(names, Contains.Item("telescope-balcony.local"));
    }

    [Test]
    public void AnOverrideIsTrimmedAndBlankFallsBackToTheGeneratedName() {
        Assert.That(DeviceIdentity.InstanceName(Config(("Mdns:InstanceName", "  named  "))),
            Is.EqualTo("named"));
        Assert.That(DeviceIdentity.InstanceName(Config(("Mdns:InstanceName", "   "))),
            Does.StartWith("polaris-app-"));
        Assert.That(DeviceIdentity.InstanceName(null), Does.StartWith("polaris-app-"));
    }

    /// <summary>The suffix is what keeps two boards flashed from one image
    /// apart, so it has to be there and has to be stable.</summary>
    [Test]
    public void TheGeneratedNameCarriesAStableFourCharacterSuffix() {
        var a = DeviceIdentity.ShortId();
        var b = DeviceIdentity.ShortId();
        Assert.That(a, Is.EqualTo(b), "two calls on one machine must agree");
        Assert.That(a, Has.Length.EqualTo(4));
        Assert.That(a, Does.Match("^[0-9a-z]{4}$"), "lowercase, and legal in a DNS label");

        Assert.That(DeviceIdentity.InstanceName(Config()),
            Is.EqualTo("polaris-app-" + a));
    }

    /// <summary>Names have to be usable as DNS labels: a SAN entry with a
    /// space or an underscore in it is rejected by some clients outright.
    /// </summary>
    [Test]
    public void EveryCertifiedNameIsAValidHostName() {
        foreach (var n in DeviceIdentity.DnsNames(Config())) {
            Assert.That(n, Is.Not.Empty);
            Assert.That(n, Does.Not.Contain(" "), n);
            Assert.That(n, Does.Not.Contain("_"), n);
            Assert.That(n, Does.Match(@"^[A-Za-z0-9.-]+$"), n);
        }
    }

    /// <summary>The shared aliases stay: whichever board wins the mDNS race for
    /// them still has to present a cert that validates. Their ambiguity across
    /// a fleet is a property of mDNS, not something the cert can fix.</summary>
    [Test]
    public void TheConvenienceAliasesAreStillCertified() {
        var names = DeviceIdentity.DnsNames(Config()).ToList();
        Assert.That(names, Contains.Item("localhost"));
        Assert.That(names, Contains.Item("polaris.local"));
        Assert.That(names, Contains.Item("polaris-app.local"));
    }

    /// <summary>The per-device name leads, because it is the only entry that
    /// stays correct once a second board joins the network.</summary>
    [Test]
    public void ThePerDeviceNameComesFirst() {
        var names = DeviceIdentity.DnsNames(Config()).ToList();
        Assert.That(names[0], Is.EqualTo(DeviceIdentity.InstanceName(Config())));
    }
}
