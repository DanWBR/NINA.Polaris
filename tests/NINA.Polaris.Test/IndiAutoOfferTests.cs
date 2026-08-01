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

using NINA.Polaris.Endpoints;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// INDIAUTO. One rule decides whether the equipment assistant opens by itself:
/// whether any INDI profile carries a driver that is not a simulator. Get it
/// wrong in one direction and the dialog never appears on a fresh host; wrong
/// in the other and it interrupts somebody mid-session on a host they set up
/// months ago.
/// </summary>
[TestFixture]
public class IndiAutoOfferTests {

    /// <summary>The exact three drivers indi-web writes into a fresh database
    /// (read from its own database.py: profile "Simulators" with these). If any
    /// of them stopped counting as a simulator, a brand-new host would look
    /// configured and the assistant would never offer itself.</summary>
    [TestCase("Telescope Simulator")]
    [TestCase("CCD Simulator")]
    [TestCase("Focuser Simulator")]
    public void TheStockProfileIsRecognisedAsSimulatorsOnly(string label) {
        Assert.That(IndiDetectEndpoints.IsSimulator(label), Is.True, label);
    }

    /// <summary>The rest of INDI's simulator family, so the rule does not
    /// depend on the three the seed happens to use today.</summary>
    [TestCase("Filter Simulator")]
    [TestCase("Dome Simulator")]
    [TestCase("GPS Simulator")]
    [TestCase("Weather Simulator")]
    [TestCase("Rotator Simulator")]
    [TestCase("Guide Simulator")]
    public void TheWiderSimulatorFamilyCountsToo(string label) {
        Assert.That(IndiDetectEndpoints.IsSimulator(label), Is.True, label);
    }

    /// <summary>Real drivers, taken from a configured host's own profile. One
    /// of these in any profile means somebody has been here, and the assistant
    /// must stay shut.</summary>
    [TestCase("ZWO CCD")]
    [TestCase("ZWO EFW")]
    [TestCase("ZWO AM3 USB")]
    [TestCase("SVBONY CCD")]
    [TestCase("Toupcam")]
    [TestCase("Gemini EAF")]
    [TestCase("EQMod Mount")]
    [TestCase("PlayerOne CCD")]
    public void RealDriversAreNotSimulators(string label) {
        Assert.That(IndiDetectEndpoints.IsSimulator(label), Is.False, label);
    }

    /// <summary>The comparison is case-insensitive, because the label is
    /// whatever indi-web stored and nothing normalises it.</summary>
    [Test]
    public void TheMatchIgnoresCase() {
        Assert.That(IndiDetectEndpoints.IsSimulator("ccd simulator"), Is.True);
        Assert.That(IndiDetectEndpoints.IsSimulator("CCD SIMULATOR"), Is.True);
    }

    /// <summary>A profile of nothing but simulators leaves the host looking
    /// unconfigured; one real driver anywhere flips it. This is the whole
    /// decision, spelled out at the level the endpoint applies it.</summary>
    [Test]
    public void OneRealDriverMakesAProfileCount() {
        var stock = new[] { "Telescope Simulator", "CCD Simulator", "Focuser Simulator" };
        var mixed = new[] { "Telescope Simulator", "ZWO CCD" };

        Assert.That(stock.Any(l => !IndiDetectEndpoints.IsSimulator(l)), Is.False,
            "the seeded profile must not read as a configured host");
        Assert.That(mixed.Any(l => !IndiDetectEndpoints.IsSimulator(l)), Is.True,
            "a profile with one real camera is a host somebody has set up");
    }
}
