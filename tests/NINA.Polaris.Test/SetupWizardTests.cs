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
using NINA.Polaris.Endpoints;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>Unit tests for the setup wizard's pure helpers: the INDI
/// DRIVER_INTERFACE bitmask decoder and the temp-profile driver list builder.
/// The indi-web REST flows need a live indi-web and are exercised on the
/// field Pi, not here.</summary>
[TestFixture]
public class SetupWizardTests {

    // ----- IndiInterfaceRoles.Decode -----

    [Test]
    public void Decode_Ccd_IsCamera() {
        Assert.That(IndiInterfaceRoles.Decode("2"), Is.EqualTo(new[] { "camera" }));
    }

    [Test]
    public void Decode_TelescopeWithGuider_IsMountAndGuider() {
        // EQMod publishes TELESCOPE | GUIDER = 5
        Assert.That(IndiInterfaceRoles.Decode("5"), Is.EqualTo(new[] { "mount", "guider" }));
    }

    [Test]
    public void Decode_CcdWithGuiderAndFilter_DecodesAllBits() {
        // 0x2 | 0x4 | 0x10 = 22
        Assert.That(IndiInterfaceRoles.Decode("22"),
                    Is.EqualTo(new[] { "camera", "guider", "filterwheel" }));
    }

    [Test]
    public void Decode_Focuser_IsFocuser() {
        Assert.That(IndiInterfaceRoles.Decode("8"), Is.EqualTo(new[] { "focuser" }));
    }

    [Test]
    public void Decode_Rotator_IsRotator() {
        Assert.That(IndiInterfaceRoles.Decode("4096"), Is.EqualTo(new[] { "rotator" }));
    }

    [Test]
    public void Decode_AuxOnly_IsAux() {
        Assert.That(IndiInterfaceRoles.Decode("32768"), Is.EqualTo(new[] { "aux" }));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("0")]
    [TestCase("-3")]
    [TestCase("banana")]
    public void Decode_MissingOrInvalid_IsEmpty(string? raw) {
        Assert.That(IndiInterfaceRoles.Decode(raw), Is.Empty);
    }

    [Test]
    public void Decode_WhitespacePadded_StillParses() {
        Assert.That(IndiInterfaceRoles.Decode(" 2 "), Is.EqualTo(new[] { "camera" }));
    }

    // ----- SetupWizardEndpoints.BuildProbeDrivers -----

    private static readonly string[] TypicalInstalled = [
        "ZWO CCD", "ZWO EFW", "ZWO EAF",
        "Toupcam", "PlayerOne CCD", "SVBONY CCD", "QHY CCD", "Altair",
        "EQMod Mount", "LX200 OnStep", "Celestron GPS",
        "CCD Simulator", "Telescope Simulator",
        "Pegasus UPB",
    ];

    [Test]
    public void BuildProbeDrivers_TakesCameraFamilyBrands_NotSerialMounts() {
        var drivers = SetupWizardEndpoints.BuildProbeDrivers(TypicalInstalled, []);
        Assert.That(drivers, Is.EquivalentTo(new[] {
            "ZWO CCD", "ZWO EFW", "ZWO EAF",
            "Toupcam", "PlayerOne CCD", "SVBONY CCD", "QHY CCD", "Altair",
        }));
        // Mount drivers publish a device with or without hardware, so blind-
        // starting them would fake a detection.
        Assert.That(drivers, Does.Not.Contain("EQMod Mount"));
        Assert.That(drivers, Does.Not.Contain("LX200 OnStep"));
    }

    [Test]
    public void BuildProbeDrivers_NeverIncludesSimulators() {
        var drivers = SetupWizardEndpoints.BuildProbeDrivers(TypicalInstalled, []);
        Assert.That(drivers.Any(d => d.Contains("Simulator")), Is.False);
    }

    [Test]
    public void BuildProbeDrivers_SingleUsbCandidate_JoinsEvenOutsideBrandList() {
        var drivers = SetupWizardEndpoints.BuildProbeDrivers(
            TypicalInstalled,
            [new[] { "Canon DSLR" }]);
        Assert.That(drivers, Does.Contain("Canon DSLR"));
    }

    [Test]
    public void BuildProbeDrivers_AmbiguousUsbCandidates_AreNotAdded() {
        var drivers = SetupWizardEndpoints.BuildProbeDrivers(
            ["EQMod Mount"],
            [new[] { "Toupcam", "Altair" }]);
        Assert.That(drivers, Is.Empty);
    }

    [Test]
    public void BuildProbeDrivers_Deduplicates() {
        var drivers = SetupWizardEndpoints.BuildProbeDrivers(
            ["ZWO CCD"],
            [new[] { "ZWO CCD" }]);
        Assert.That(drivers, Is.EqualTo(new[] { "ZWO CCD" }));
    }

    [Test]
    public void BuildProbeDrivers_NothingInstalled_IsEmpty() {
        Assert.That(SetupWizardEndpoints.BuildProbeDrivers([], []), Is.Empty);
    }

    [Test]
    public void BuildProbeDrivers_IsSorted() {
        var drivers = SetupWizardEndpoints.BuildProbeDrivers(TypicalInstalled, []);
        var sorted = drivers.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.That(drivers, Is.EqualTo(sorted));
    }
}
