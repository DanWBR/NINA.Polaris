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
using NINA.Image.Interfaces;
using NINA.Polaris.Endpoints;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the <see cref="ITelescope"/> abstraction the EquipmentManager,
/// Slew &amp; Center workflow, meridian-flip service and sequencer all
/// depend on. The interface is the seam where direct Wi-Fi drivers
/// (SynScan UDP, NexStar TCP, LX200 TCP) plug in next to the existing
/// INDI mount.
/// </summary>
[TestFixture]
public class ITelescopeContractTests {

    [Test]
    public void Capabilities_GermanEquatorialPreset_EnablesEverything() {
        var c = MountCapabilities.GermanEquatorial;
        Assert.That(c.SupportsPark, Is.True);
        Assert.That(c.SupportsTrackingToggle, Is.True);
        Assert.That(c.SupportsSync, Is.True);
        Assert.That(c.SupportsPierSide, Is.True);
        Assert.That(c.SupportsManualJog, Is.True);
    }

    [Test]
    public void Capabilities_AltAzPreset_HasNoPierSide() {
        // Alt-az mounts (AZ-GTi, NexStar SE fork) don't have a
        // mechanical pier side; the UI hides the indicator.
        var c = MountCapabilities.AltAz;
        Assert.That(c.SupportsPierSide, Is.False);
        Assert.That(c.SupportsPark, Is.True,
            "Alt-az mounts still park (typically to a home position)");
        Assert.That(c.SupportsTrackingToggle, Is.True);
    }

    [Test]
    public void IndiTelescopeImplementsITelescope() {
        // Compile-time sanity: the refactor must keep IndiTelescope
        // implementing the new interface so EquipmentManager's
        // ITelescope-typed property still binds.
        Assert.That(typeof(ITelescope).IsAssignableFrom(typeof(NINA.INDI.Devices.IndiTelescope)),
            Is.True);
    }

    // ----- per-direction jog stop -----

    /// <summary>The d-pad sends long names and the keyboard shortcuts single
    /// letters; both reach the same route.</summary>
    [TestCase("north", MountJogDirection.North)]
    [TestCase("N", MountJogDirection.North)]
    [TestCase("south", MountJogDirection.South)]
    [TestCase("s", MountJogDirection.South)]
    [TestCase("East", MountJogDirection.East)]
    [TestCase("e", MountJogDirection.East)]
    [TestCase(" west ", MountJogDirection.West)]
    [TestCase("w", MountJogDirection.West)]
    public void ParseJogDirection_AcceptsBothSpellingsTheUiSends(string raw, MountJogDirection expected) {
        Assert.That(TelescopeEndpoints.ParseJogDirection(raw), Is.EqualTo(expected));
    }

    /// <summary>Anything else is a 400, never a silent halt of an axis the
    /// caller did not name.</summary>
    [TestCase("stop")]
    [TestCase("up")]
    [TestCase("")]
    [TestCase(null)]
    public void ParseJogDirection_RejectsAnythingElse(string? raw) {
        Assert.That(TelescopeEndpoints.ParseJogDirection(raw), Is.Null);
    }

    private static System.Reflection.MethodInfo? PerDirectionStop(Type t)
        => t.GetMethods().SingleOrDefault(m => m.Name == nameof(ITelescope.StopMotionAsync)
            && m.GetParameters().Length == 2
            && m.GetParameters()[0].ParameterType == typeof(MountJogDirection));

    /// <summary>Releasing one arrow must not go through AbortSlew: on the
    /// LX200-derived ZWO AM3 / AM5 driver abort also stops tracking and
    /// cancels a GoTo. These three backends can halt a single axis, so each
    /// one has to say how instead of inheriting the all-axes default.</summary>
    [Test]
    public void IndiAlpacaAndSynScanEachImplementThePerDirectionStop() {
        var backends = new[] {
            typeof(NINA.INDI.Devices.IndiTelescope),
            typeof(NINA.Polaris.Services.Alpaca.AlpacaTelescope),
            typeof(NINA.Mount.SynScanWifi.SynScanWifiTelescope),
        };
        foreach (var t in backends) {
            var m = PerDirectionStop(t);
            Assert.That(m, Is.Not.Null, $"{t.Name} has no StopMotionAsync(MountJogDirection)");
            Assert.That(m!.DeclaringType, Is.EqualTo(t),
                $"{t.Name} inherits the all-axes stop instead of halting one axis");
        }
    }

    /// <summary>And a backend that has nothing to say keeps working: the
    /// interface default routes it to the all-axes stop.</summary>
    [Test]
    public void ABackendWithoutAPerAxisStopFallsBackToTheInterfaceDefault() {
        Assert.That(PerDirectionStop(typeof(NINA.Polaris.Services.Simulator.Gear.SimMount)),
            Is.Null, "SimMount declares no override");
        var declared = typeof(ITelescope).GetMethods()
            .SingleOrDefault(m => m.Name == nameof(ITelescope.StopMotionAsync)
                && m.GetParameters().Length == 2
                && m.GetParameters()[0].ParameterType == typeof(MountJogDirection));
        Assert.That(declared, Is.Not.Null,
            "the interface must declare the per-direction stop");
        Assert.That(declared!.IsAbstract, Is.False,
            "it must carry a default body, or every backend would have to implement it");
    }

}