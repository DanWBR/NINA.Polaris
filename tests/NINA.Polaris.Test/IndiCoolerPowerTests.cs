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

using System.IO;
using System.Runtime.CompilerServices;
using NINA.INDI.Devices;
using NINA.INDI.Protocol;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Reading cooler power off an INDI camera.
///
/// <para>INDI's CCD base class does not define the CCD_COOLER_POWER vector, so
/// every driver names its element itself: indi_asi_ccd and indi_qhy_ccd use
/// CCD_COOLER_VALUE, indi_toupbase (ToupTek, and the Altair / Omegon /
/// RisingCam / Bresser cameras on the same SDK) uses COOLER_POWER. Polaris
/// asked for one fixed name, so on the others it got zero, and zero is a
/// perfectly plausible cooler power rather than an obvious failure. Reported
/// on a ToupTek ATR533C: the temperature graph drew the power as a flat line
/// at 0 while the camera cooled and the INDI control panel showed 41
/// percent.</para>
/// </summary>
[TestFixture]
public class IndiCoolerPowerTests {

    private static IndiNumberProperty Vector(params (string Name, string Label, double Value)[] els) {
        var p = new IndiNumberProperty { Device = "Cam", Name = "CCD_COOLER_POWER" };
        foreach (var e in els)
            p.Values[e.Name] = new IndiNumberElement { Label = e.Label, Value = e.Value, Min = 0, Max = 100 };
        return p;
    }

    /// <summary>The shape that was already working, and has to keep working.</summary>
    [Test]
    public void AsiAndQhy_PublishCcdCoolerValue() {
        Assert.That(IndiCamera.PickCoolerPower(
            Vector(("CCD_COOLER_VALUE", "Cooling Power (%)", 73))), Is.EqualTo(73));
    }

    /// <summary>The ATR533C, from indi_toupbase: element COOLER_POWER, label
    /// "Percent". This is the one that read as zero.</summary>
    [Test]
    public void ToupTekFamily_PublishesCoolerPower() {
        Assert.That(IndiCamera.PickCoolerPower(
            Vector(("COOLER_POWER", "Percent", 40.98360655737705))), Is.EqualTo(40.98).Within(0.01));
    }

    /// <summary>Every driver that publishes the vector gives it one element, so
    /// a name nobody has seen before is still the answer. Guessing from a list
    /// alone is what produced the silent zero in the first place.</summary>
    [Test]
    public void AnUnknownElementName_IsStillTheCoolerPower() {
        Assert.That(IndiCamera.PickCoolerPower(
            Vector(("TEC_PERCENT", "TEC", 55))), Is.EqualTo(55));
    }

    [Test]
    public void NoVectorAtAll_ReadsZeroRatherThanThrowing() {
        Assert.Multiple(() => {
            Assert.That(IndiCamera.PickCoolerPower(null), Is.Zero);
            Assert.That(IndiCamera.PickCoolerPower(Vector()), Is.Zero);
        });
    }

    /// <summary>With more than one element the known names decide, rather than
    /// whichever the driver happened to publish first.</summary>
    [Test]
    public void WithSeveralElements_TheKnownNameWins() {
        var v = Vector(("TEC_VOLTAGE", "TEC Voltage", 11.7),
                       ("CCD_COOLER_VALUE", "Cooling Power (%)", 62));
        Assert.That(IndiCamera.PickCoolerPower(v), Is.EqualTo(62));
    }

    /// <summary>The graph drew nothing while the cooler flag said off.
    /// indi_toupbase publishes CCD_COOLER write-only, so that flag is not a
    /// reliable gate on a camera that is plainly cooling.</summary>
    [Test]
    public void TheGraphDrawsAReportedPowerEvenWhenTheFlagSaysOff() {
        var here = Path.GetDirectoryName(Here())!;
        var js = File.ReadAllText(Path.GetFullPath(Path.Combine(here, "..", "..",
            "src", "NINA.Polaris", "wwwroot", "js", "app.js")));

        Assert.Multiple(() => {
            Assert.That(js, Does.Contain(
                "power: (eq.camera.coolerOn || (eq.camera.coolerPower || 0) > 0)"));
            Assert.That(js, Does.Not.Contain(
                "power: eq.camera.coolerOn ? (eq.camera.coolerPower || 0) : 0"));
        });
    }

    private static string Here([CallerFilePath] string p = "") => p;
}
