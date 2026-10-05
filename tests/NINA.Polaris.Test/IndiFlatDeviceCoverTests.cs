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

using System.Linq;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The property name a motorised flat panel's cover lives under.
///
/// Polaris asked for DUSTCAP_PARK. INDI calls it CAP_PARK, in
/// INDI::DustCapInterface, and the name appears verbatim in libindidriver.so;
/// DUSTCAP_PARK appears nowhere. So Open and Close wrote to a property no
/// driver publishes: nothing moved, and the cover state on screen was the
/// default rather than the panel's. Reported from the field with a Gemini flat
/// panel, whose driver uses that interface.
///
/// This is a string-constant bug, which no amount of exercising the happy path
/// catches without the hardware. What it is worth pinning is the contract
/// itself, so the next person who touches these names has to mean it.
/// </summary>
[TestFixture]
public class IndiFlatDeviceCoverTests {

    private static string Source() {
        var here = System.IO.Path.GetDirectoryName(Here())!;
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            here, "..", "..", "src", "NINA.INDI", "Devices", "IndiFlatDevice.cs"));
        Assert.That(System.IO.File.Exists(path), $"nao achei {path}");
        return System.IO.File.ReadAllText(path);
    }

    private static string Here([System.Runtime.CompilerServices.CallerFilePath] string p = "") => p;

    [Test]
    public void TheCoverUsesTheStandardIndiPropertyName() {
        Assert.That(Source(), Does.Contain("\"CAP_PARK\""),
            "INDI::DustCapInterface publishes CAP_PARK; it is the only name a "
            + "stock driver answers to");
    }

    /// <summary>The old name stays only as a fallback, and must never be the
    /// one tried first.</summary>
    [Test]
    public void TheOldNameIsNoLongerTheFirstChoice() {
        var src = Source();
        int standard = src.IndexOf("\"CAP_PARK\"", System.StringComparison.Ordinal);
        int legacy = src.IndexOf("\"DUSTCAP_PARK\"", System.StringComparison.Ordinal);

        Assert.That(standard, Is.GreaterThan(-1));
        if (legacy >= 0) {
            Assert.That(standard, Is.LessThan(legacy),
                "CAP_PARK has to be probed before the non-standard name");
        }
    }

    /// <summary>Both elements of that switch are standard too, and writing one
    /// without the other leaves a one-of-many vector ambiguous.</summary>
    [Test]
    public void BothSwitchElementsAreWrittenTogether() {
        var src = Source();

        Assert.Multiple(() => {
            Assert.That(src, Does.Contain("[\"PARK\"] = false, [\"UNPARK\"] = true"));
            Assert.That(src, Does.Contain("[\"PARK\"] = true, [\"UNPARK\"] = false"));
        });
    }

    /// <summary>A light panel with no cap is a normal device, not a fault. It
    /// must be distinguishable, so the UI can stop offering Open and Close and
    /// the endpoint can refuse with a sentence instead of a 500.</summary>
    [Test]
    public void ADeviceWithNoCoverIsReportedAsSuch() {
        var src = Source();

        Assert.Multiple(() => {
            Assert.That(src, Does.Contain("public bool HasCover"));
            Assert.That(src, Does.Contain("NotSupportedException"));
        });
    }

    /// <summary>The light box half was always right; this guards it against a
    /// well-meaning rename while the cover half is being fixed.</summary>
    [Test]
    public void TheLightBoxPropertiesAreTheStandardOnes() {
        var src = Source();

        Assert.Multiple(() => {
            Assert.That(src, Does.Contain("\"FLAT_LIGHT_CONTROL\""));
            Assert.That(src, Does.Contain("\"FLAT_LIGHT_ON\""));
            Assert.That(src, Does.Contain("\"FLAT_LIGHT_OFF\""));
            Assert.That(src, Does.Contain("\"FLAT_LIGHT_INTENSITY\""));
            Assert.That(src, Does.Contain("\"FLAT_LIGHT_INTENSITY_VALUE\""));
        });
    }
}
